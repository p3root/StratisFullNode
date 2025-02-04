﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Protocol;

namespace NBitcoin
{
    [Flags]
    public enum BlockFlag //block index flags
    {
        BLOCK_PROOF_OF_STAKE = (1 << 0), // is proof-of-stake block
        BLOCK_STAKE_ENTROPY = (1 << 1), // entropy bit for stake modifier
        BLOCK_STAKE_MODIFIER = (1 << 2), // regenerated stake modifier
    };

    public class BlockStake : IBitcoinSerializable
    {
        public int Mint;

        public OutPoint PrevoutStake;

        public uint StakeTime;

        public ulong StakeModifier; // hash modifier for proof-of-stake

        public uint256 StakeModifierV2;

        private int flags;

        public uint256 HashProof;

        public BlockStake()
        {
        }

        public BlockFlag Flags
        {
            get
            {
                return (BlockFlag)this.flags;
            }
            set
            {
                this.flags = (int)value;
            }
        }

        public static bool IsProofOfStake(Block block)
        {
            return block.Transactions.Count > 1 && block.Transactions[1].IsCoinStake;
        }

        public static bool IsProofOfWork(Block block)
        {
            return !IsProofOfStake(block);
        }

        public void ReadWrite(BitcoinStream stream)
        {
            stream.ReadWrite(ref this.flags);
            stream.ReadWrite(ref this.Mint);
            stream.ReadWrite(ref this.StakeModifier);
            stream.ReadWrite(ref this.StakeModifierV2);
            if (this.IsProofOfStake())
            {
                stream.ReadWrite(ref this.PrevoutStake);
                stream.ReadWrite(ref this.StakeTime);
            }
            stream.ReadWrite(ref this.HashProof);
        }

        public bool IsProofOfWork()
        {
            return !((this.Flags & BlockFlag.BLOCK_PROOF_OF_STAKE) > 0);
        }

        public bool IsProofOfStake()
        {
            return (this.Flags & BlockFlag.BLOCK_PROOF_OF_STAKE) > 0;
        }

        public void SetProofOfStake()
        {
            this.Flags |= BlockFlag.BLOCK_PROOF_OF_STAKE;
        }

        public uint GetStakeEntropyBit()
        {
            return (uint)(this.Flags & BlockFlag.BLOCK_STAKE_ENTROPY) >> 1;
        }

        public bool SetStakeEntropyBit(uint nEntropyBit)
        {
            if (nEntropyBit > 1)
                return false;
            this.Flags |= (nEntropyBit != 0 ? BlockFlag.BLOCK_STAKE_ENTROPY : 0);
            return true;
        }

        /// <summary>
        /// Constructs a stake block from a given block.
        /// </summary>
        public static BlockStake Load(Block block)
        {
            var blockStake = new BlockStake
            {
                StakeModifierV2 = uint256.Zero,
                HashProof = uint256.Zero
            };

            if (IsProofOfStake(block))
            {
                blockStake.SetProofOfStake();
                blockStake.StakeTime = block.Header.Time;
                blockStake.PrevoutStake = block.Transactions[1].Inputs[0].PrevOut;
            }

            return blockStake;
        }

        /// <summary>
        /// Constructs a stake block from a set bytes and the given network.
        /// </summary>
        public static BlockStake Load(byte[] bytes, ConsensusFactory consensusFactory)
        {
            var blockStake = new BlockStake();
            blockStake.ReadWrite(bytes, consensusFactory);
            return blockStake;
        }

        /// <summary>
        /// Check PoW and that the blocks connect correctly
        /// </summary>
        /// <param name="network">The network being used</param>
        /// <param name="chainedHeader">Chained block header</param>
        /// <returns>True if PoW is correct</returns>
        public static bool Validate(Network network, ChainedHeader chainedHeader)
        {
            if (network == null)
                throw new ArgumentNullException("network");

            if (chainedHeader.Height != 0 && chainedHeader.Previous == null)
                return false;

            bool heightCorrect = chainedHeader.Height == 0 || chainedHeader.Height == chainedHeader.Previous.Height + 1;
            bool genesisCorrect = chainedHeader.Height != 0 || chainedHeader.HashBlock == network.GetGenesis().GetHash();
            bool hashPrevCorrect = chainedHeader.Height == 0 || chainedHeader.Header.HashPrevBlock == chainedHeader.Previous.HashBlock;
            bool hashCorrect = chainedHeader.HashBlock == chainedHeader.Header.GetHash();

            return heightCorrect && genesisCorrect && hashPrevCorrect && hashCorrect;
        }
    }

    /// <summary>
    /// A Proof Of Stake transaction.
    /// </summary>
    /// <remarks>
    /// TODO: later we can move the POS timestamp field in this class.
    /// serialization can be refactored to have a common array that will be serialized and each inheritance can add to the array)
    /// </remarks>
    public class PosTransaction : Transaction
    {
        public bool IsColdCoinStake { get; set; }

        public PosTransaction() : base()
        {
        }

        public PosTransaction(string hex, ProtocolVersion version = ProtocolVersion.PROTOCOL_VERSION) : this()
        {
            this.FromBytes(Encoders.Hex.DecodeData(hex), version);
        }

        public PosTransaction(byte[] bytes) : this()
        {
            this.FromBytes(bytes);
        }

        public override bool IsProtocolTransaction()
        {
            return this.IsCoinStake || this.IsCoinBase;
        }
    }

    /// <summary>
    /// The consensus factory for creating POS protocol types.
    /// </summary>
    public class PosConsensusFactory : ConsensusFactory
    {
        /// <summary>
        /// A dictionary for types assignable from <see cref="ProvenBlockHeader"/>.
        /// </summary>
        private readonly ConcurrentDictionary<Type, bool> isAssignableFromProvenBlockHeader = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// The <see cref="ProvenBlockHeader"/> type.
        /// </summary>
        private readonly TypeInfo provenBlockHeaderType = typeof(ProvenBlockHeader).GetTypeInfo();

        public PosConsensusFactory()
            : base()
        {
        }

        /// <summary>
        /// Check if the generic type is assignable from <see cref="BlockHeader"/>.
        /// </summary>
        /// <typeparam name="T">The type to check if it is IsAssignable from <see cref="BlockHeader"/>.</typeparam>
        /// <returns><c>true</c> if it is assignable.</returns>
        protected bool IsProvenBlockHeader<T>()
        {
            return this.IsAssignable<T>(this.provenBlockHeaderType, this.isAssignableFromProvenBlockHeader);
        }

        /// <inheritdoc />
        public override T TryCreateNew<T>()
        {
            if (this.IsProvenBlockHeader<T>())
                return (T)(object)this.CreateProvenBlockHeader();

            return base.TryCreateNew<T>();
        }

        /// <inheritdoc />
        public override Block CreateBlock()
        {
            return new PosBlock(this.CreateBlockHeader());
        }

        /// <inheritdoc />
        public override BlockHeader CreateBlockHeader()
        {
            return new PosBlockHeader();
        }

        public virtual ProvenBlockHeader CreateProvenBlockHeader()
        {
            return new ProvenBlockHeader();
        }

        public virtual ProvenBlockHeader CreateProvenBlockHeader(PosBlock block)
        {
            var provenBlockHeader = new ProvenBlockHeader(block);

            // Serialize the size.
            provenBlockHeader.ToBytes(this);

            return provenBlockHeader;
        }

        /// <inheritdoc />
        public override Transaction CreateTransaction()
        {
            return new PosTransaction();
        }

        /// <inheritdoc />
        public override Transaction CreateTransaction(string hex)
        {
            return new PosTransaction(hex);
        }

        /// <inheritdoc />
        public override Transaction CreateTransaction(byte[] bytes)
        {
            return new PosTransaction(bytes);
        }
    }

    public interface ISmartContractBlockHeader
    {
        uint256 HashStateRoot { get; set; }

        uint256 ReceiptRoot { get; set; }

        Bloom LogsBloom { get; set; }
    }

    /// <summary>
    /// A POS block header, this will create a work hash based on the X13 hash algos.
    /// </summary>
#pragma warning disable 618
    public class PosBlockHeader : BlockHeader, ISmartContractBlockHeader
#pragma warning restore 618
    {
        // Indicates that the header contains additional fields.
        // The first field is a uint "Size" field to indicate the serialized size of additional fields.
        public const int ExtendedHeaderBit = 0x10000000;

        // Determines whether this object should serialize the new fields associated with smart contracts.
        public bool HasSmartContractFields => (this.version & ExtendedHeaderBit) != 0;

        /// <inheritdoc />
        public override int CurrentVersion => 7;

        private ushort extendedHeaderSize => (ushort)(hashStateRootSize + receiptRootSize + this.logsBloom.GetCompressedSize());

        /// <summary>
        /// Root of the state trie after execution of this block. 
        /// </summary>
        private uint256 hashStateRoot;
        public uint256 HashStateRoot { get { return this.hashStateRoot; } set { this.hashStateRoot = value; } }
        private static int hashStateRootSize = (new uint256()).GetSerializeSize();

        /// <summary>
        /// Root of the receipt trie after execution of this block.
        /// </summary>
        private uint256 receiptRoot;
        public uint256 ReceiptRoot { get { return this.receiptRoot; } set { this.receiptRoot = value; } }
        private static int receiptRootSize = (new uint256()).GetSerializeSize();

        /// <summary>
        /// Bitwise-OR of all the blooms generated from all of the smart contract transactions in the block.
        /// </summary>
        private Bloom logsBloom;
        public Bloom LogsBloom { get { return this.logsBloom; } set { this.logsBloom = value; } }

        public PosBlockHeader()
        {
            this.hashStateRoot = 0;
            this.receiptRoot = 0;
            this.logsBloom = new Bloom();
        }

        /// <inheritdoc />
        public override uint256 GetHash()
        {
            uint256 hash = null;
            uint256[] innerHashes = this.hashes;

            if (innerHashes != null)
                hash = innerHashes[0];

            if (hash != null)
                return hash;

            if (this.version > 6)
            {
                using (var hs = new HashStream())
                {
                    this.ReadWriteHashingStream(new BitcoinStream(hs, true));
                    hash = hs.GetHash();
                }
            }
            else
            {
                hash = this.GetPoWHash();
            }

            innerHashes = this.hashes;
            if (innerHashes != null)
            {
                innerHashes[0] = hash;
            }

            return hash;
        }

        /// <inheritdoc />
        public override uint256 GetPoWHash()
        {
            using (var ms = new MemoryStream())
            {
                this.ReadWriteHashingStream(new BitcoinStream(ms, true));
                return HashX13.Instance.Hash(ms.ToArray());
            }
        }

        #region IBitcoinSerializable Members

        public override void ReadWrite(BitcoinStream stream)
        {
            base.ReadWrite(stream);
            if (this.HasSmartContractFields)
            {
                stream.ReadWrite(ref this.hashStateRoot);
                stream.ReadWrite(ref this.receiptRoot);
                stream.ReadWriteCompressed(ref this.logsBloom);
            }
        }

        #endregion

        public override void CopyFields(BlockHeader source)
        {
            base.CopyFields(source);
            if (source is PosBlockHeader header && header.HasSmartContractFields)
            {
                this.HashStateRoot = header.HashStateRoot;
                this.ReceiptRoot = header.ReceiptRoot;
                this.LogsBloom = header.LogsBloom;
            }
        }

        /// <summary>Populates stream with items that will be used during hash calculation.</summary>
        protected override void ReadWriteHashingStream(BitcoinStream stream)
        {
            base.ReadWriteHashingStream(stream);
            if (this.HasSmartContractFields)
            {
                stream.ReadWrite(ref this.hashStateRoot);
                stream.ReadWrite(ref this.receiptRoot);
                stream.ReadWriteCompressed(ref this.logsBloom);
            }
        }

        /// <summary>Gets the total header size - including the <see cref="BlockHeader.Size"/> - in bytes.</summary>
        public override long HeaderSize => this.HasSmartContractFields ? Size + this.extendedHeaderSize : Size;
    }

    /// <summary>
    /// A POS block that contains the additional block signature serialization.
    /// </summary>
    public class PosBlock : Block
    {
        /// <summary>
        /// A block signature - signed by one of the coin base txout[N]'s owner.
        /// </summary>
        private BlockSignature blockSignature = new BlockSignature();

        public PosBlock(BlockHeader blockHeader) : base(blockHeader)
        {
        }

        /// <summary>
        /// The block signature type.
        /// </summary>
        public BlockSignature BlockSignature
        {
            get { return this.blockSignature; }
            set { this.blockSignature = value; }
        }

        /// <summary>
        /// The additional serialization of the block POS block.
        /// </summary>
        public override void ReadWrite(BitcoinStream stream)
        {
            // Capture the value in BlockSize as calling base will change it.
            long? blockSize = this.BlockSize;

            base.ReadWrite(stream);
            stream.ReadWrite(ref this.blockSignature);

            if (blockSize == null)
            {
                this.BlockSize = stream.Serializing ? stream.Counter.WrittenBytes : stream.Counter.ReadBytes;
            }
        }

        /// <summary>
        /// Gets the block's coinstake transaction or returns the coinbase transaction if there is no coinstake.
        /// </summary>
        /// <returns>Coinstake transaction or coinbase transaction.</returns>
        /// <remarks>
        /// <para>In PoS blocks, coinstake transaction is the second transaction in the block.</para>
        /// <para>In PoW there isn't a coinstake transaction, return coinbase instead to be able to compute stake modifier for the next eventual PoS block.</para>
        /// </remarks>
        public Transaction GetProtocolTransaction()
        {
            return (this.Transactions.Count > 1 && this.Transactions[1].IsCoinStake) ? this.Transactions[1] : this.Transactions[0];
        }
    }
}