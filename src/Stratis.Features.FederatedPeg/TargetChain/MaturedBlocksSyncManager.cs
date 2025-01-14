﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using NLog;
using Stratis.Bitcoin.AsyncWork;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.Utilities;
using Stratis.Features.FederatedPeg.Controllers;
using Stratis.Features.FederatedPeg.Conversion;
using Stratis.Features.FederatedPeg.Interfaces;
using Stratis.Features.FederatedPeg.Models;
using Stratis.Features.FederatedPeg.SourceChain;
using Stratis.Features.FederatedPeg.Wallet;

namespace Stratis.Features.FederatedPeg.TargetChain
{
    /// <summary>
    /// Handles block syncing between gateways on 2 chains. This node will request
    /// blocks from another chain to look for cross chain deposit transactions.
    /// </summary>
    /// <remarks>Processes matured block deposits from the cirrus chain and creates instances of <see cref="ConversionRequest"/> which are
    /// saved to <see cref="IConversionRequestRepository"/>.</remarks>
    public interface IMaturedBlocksSyncManager : IDisposable
    {
        /// <summary>Starts requesting blocks from another chain.</summary>
        Task StartAsync();
    }

    /// <inheritdoc cref="IMaturedBlocksSyncManager"/>
    public class MaturedBlocksSyncManager : IMaturedBlocksSyncManager
    {
        private readonly IAsyncProvider asyncProvider;
        private readonly ICrossChainTransferStore crossChainTransferStore;
        private readonly IFederationGatewayClient federationGatewayClient;
        private readonly IFederationWalletManager federationWalletManager;
        private readonly IInitialBlockDownloadState initialBlockDownloadState;
        private readonly ILogger logger;
        private readonly INodeLifetime nodeLifetime;
        private readonly IConversionRequestRepository conversionRequestRepository;
        private readonly ChainIndexer chainIndexer;

        private IAsyncLoop requestDepositsTask;

        /// <summary>
        /// If the federation wallet tip is within this amount of blocks from the chain's tip, consider it synced.
        /// </summary>
        private const int FederationWalletTipSyncBuffer = 10;

        /// <summary>When we are fully synced we stop asking for more blocks for this amount of time.</summary>
        private const int RefreshDelaySeconds = 10;

        /// <summary>Delay between initialization and first request to other node.</summary>
        /// <remarks>Needed to give other node some time to start before bombing it with requests.</remarks>
        private const int InitializationDelaySeconds = 10;

        public MaturedBlocksSyncManager(
            IAsyncProvider asyncProvider,
            ICrossChainTransferStore crossChainTransferStore,
            IFederationGatewayClient federationGatewayClient,
            IFederationWalletManager federationWalletManager,
            IInitialBlockDownloadState initialBlockDownloadState,
            INodeLifetime nodeLifetime,
            IConversionRequestRepository conversionRequestRepository,
            ChainIndexer chainIndexer)
        {
            this.asyncProvider = asyncProvider;
            this.chainIndexer = chainIndexer;
            this.conversionRequestRepository = conversionRequestRepository;
            this.crossChainTransferStore = crossChainTransferStore;
            this.federationGatewayClient = federationGatewayClient;
            this.federationWalletManager = federationWalletManager;
            this.initialBlockDownloadState = initialBlockDownloadState;
            this.nodeLifetime = nodeLifetime;

            this.logger = LogManager.GetCurrentClassLogger();
        }

        /// <inheritdoc />
        public async Task StartAsync()
        {
            // Initialization delay; give the counter chain node some time to start it's API service.
            await Task.Delay(TimeSpan.FromSeconds(InitializationDelaySeconds), this.nodeLifetime.ApplicationStopping).ConfigureAwait(false);

            this.requestDepositsTask = this.asyncProvider.CreateAndRunAsyncLoop($"{nameof(MaturedBlocksSyncManager)}.{nameof(this.requestDepositsTask)}", async token =>
            {
                bool delayRequired = await this.SyncDepositsAsync().ConfigureAwait(false);
                if (delayRequired)
                {
                    // Since we are synced or had a problem syncing there is no need to ask for more blocks right away.
                    // Therefore awaiting for a delay during which new block might be accepted on the alternative chain
                    // or alt chain node might be started.
                    await Task.Delay(TimeSpan.FromSeconds(RefreshDelaySeconds), this.nodeLifetime.ApplicationStopping).ConfigureAwait(false);
                }
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpan.FromSeconds(RefreshDelaySeconds));
        }

        /// <summary>Asks for blocks from another gateway node and then processes them.</summary>
        /// <returns><c>true</c> if delay between next time we should ask for blocks is required; <c>false</c> otherwise.</returns>
        protected async Task<bool> SyncDepositsAsync()
        {
            // First ensure that we the node is out of IBD.
            if (this.initialBlockDownloadState.IsInitialBlockDownload())
            {
                this.logger.Info("The CCTS will start processing deposits once the node is out of IBD.");
                return true;
            }

            // Then ensure that the federation wallet is synced with the chain.
            if (this.federationWalletManager.WalletTipHeight < this.chainIndexer.Tip.Height - FederationWalletTipSyncBuffer)
            {
                this.logger.Info($"The CCTS will start processing deposits once the federation wallet is synced with the chain; height {this.federationWalletManager.WalletTipHeight}");
                return true;
            }

            SerializableResult<List<MaturedBlockDepositsModel>> matureBlockDeposits = await this.federationGatewayClient.GetMaturedBlockDepositsAsync(this.crossChainTransferStore.NextMatureDepositHeight, this.nodeLifetime.ApplicationStopping).ConfigureAwait(false);

            if (matureBlockDeposits == null)
            {
                this.logger.Debug("Failed to fetch normal deposits from counter chain node; {0} didn't respond.", this.federationGatewayClient.EndpointUrl);
                return true;
            }

            if (matureBlockDeposits.Value == null)
            {
                this.logger.Debug("Failed to fetch normal deposits from counter chain node; {0} didn't reply with any deposits; Message: {1}", this.federationGatewayClient.EndpointUrl, matureBlockDeposits.Message ?? "none");
                return true;
            }

            return await ProcessMatureBlockDepositsAsync(matureBlockDeposits);
        }

        private async Task<bool> ProcessMatureBlockDepositsAsync(SerializableResult<List<MaturedBlockDepositsModel>> matureBlockDeposits)
        {
            // "Value"'s count will be 0 if we are using NewtonSoft's serializer, null if using .Net Core 3's serializer.
            if (matureBlockDeposits.Value.Count == 0)
            {
                this.logger.Debug("Considering ourselves fully synced since no blocks were received.");

                // If we've received nothing we assume we are at the tip and should flush.
                // Same mechanic as with syncing headers protocol.
                await this.crossChainTransferStore.SaveCurrentTipAsync().ConfigureAwait(false);

                return true;
            }

            // Filter out conversion transactions & also log what we've received for diagnostic purposes.
            foreach (MaturedBlockDepositsModel maturedBlockDeposit in matureBlockDeposits.Value)
            {
                foreach (IDeposit conversionTransaction in maturedBlockDeposit.Deposits.Where(d =>
                    d.RetrievalType == DepositRetrievalType.ConversionSmall ||
                    d.RetrievalType == DepositRetrievalType.ConversionNormal ||
                    d.RetrievalType == DepositRetrievalType.ConversionLarge))
                {
                    this.logger.Info("Conversion mint transaction " + conversionTransaction + " received in matured blocks.");

                    if (this.conversionRequestRepository.Get(conversionTransaction.Id.ToString()) != null)
                    {
                        this.logger.Info("Conversion mint transaction " + conversionTransaction + " already exists, ignoring.");

                        continue;
                    }

                    // Get the first block on this chain that has a timestamp after the deposit's block time on the counterchain.
                    // This is so that we can assign a block height that the deposit 'arrived' on the sidechain.
                    // TODO: This can probably be made more efficient than looping every time. 
                    ChainedHeader header = this.chainIndexer.Tip;
                    bool found = false;

                    while (true)
                    {
                        if (header == this.chainIndexer.Genesis)
                        {
                            break;
                        }

                        if (header.Previous.Header.Time <= maturedBlockDeposit.BlockInfo.BlockTime)
                        {
                            found = true;

                            break;
                        }

                        header = header.Previous;
                    }

                    if (!found)
                    {
                        continue;
                    }

                    this.conversionRequestRepository.Save(new ConversionRequest()
                    {
                        RequestId = conversionTransaction.Id.ToString(),
                        RequestType = ConversionRequestType.Mint,
                        Processed = false,
                        RequestStatus = ConversionRequestStatus.Unprocessed,
                        // We do NOT convert to wei here yet. That is done when the minting transaction is submitted on the Ethereum network.
                        Amount = (ulong)conversionTransaction.Amount.Satoshi,
                        BlockHeight = header.Height,
                        DestinationAddress = conversionTransaction.TargetAddress,
                        DestinationChain = conversionTransaction.TargetChain
                    });
                }

                // Order all other transactions in the block deterministically.
                maturedBlockDeposit.Deposits = maturedBlockDeposit.Deposits.Where(d =>
                    d.RetrievalType != DepositRetrievalType.ConversionSmall &&
                    d.RetrievalType != DepositRetrievalType.ConversionNormal &&
                    d.RetrievalType != DepositRetrievalType.ConversionLarge).OrderBy(x => x.Id, Comparer<uint256>.Create(DeterministicCoinOrdering.CompareUint256)).ToList();

                foreach (IDeposit deposit in maturedBlockDeposit.Deposits)
                {
                    this.logger.Trace(deposit.ToString());
                }
            }

            // If we received a portion of blocks we can ask for a new portion without any delay.
            RecordLatestMatureDepositsResult result = await this.crossChainTransferStore.RecordLatestMatureDepositsAsync(matureBlockDeposits.Value).ConfigureAwait(false);
            return !result.MatureDepositRecorded;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.requestDepositsTask?.Dispose();
        }
    }
}
