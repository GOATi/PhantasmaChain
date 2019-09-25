using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.Storage;
using Phantasma.Storage.Utils;
using System.Collections.Generic;
using System.IO;

namespace Phantasma.Blockchain
{
    public class Archive : IArchive, ISerializable
    {
        public ArchiveFlags Flags { get; private set; }
        public Hash Hash => MerkleTree.Root;
        public MerkleTree MerkleTree { get; private set; }
        public BigInteger Size { get; private set; }
        public byte[] Key { get; private set; }

        public IEnumerable<Hash> Blocks
        {
            get
            {
                var count = this.GetBlockCount();
                for (int i = 0; i < count; i++)
                {
                    yield return MerkleTree.GetHash(i);
                }

                yield break;
            }
        }

        public Archive(MerkleTree tree, BigInteger size, ArchiveFlags flags, byte[] key)
        {
            this.MerkleTree = tree;
            this.Size = size;
            this.Flags = flags;
            this.Key = key;
        }

        public Archive()
        {

        }

        public void SerializeData(BinaryWriter writer)
        {
            MerkleTree.SerializeData(writer);
            writer.Write((long)Size);
            writer.Write((byte)Flags);
            writer.WriteByteArray(Key);
        }

        public void UnserializeData(BinaryReader reader)
        {
            MerkleTree = MerkleTree.Unserialize(reader);
            Size = reader.ReadInt64();
            Flags = (ArchiveFlags)reader.ReadByte();

            Key = reader.ReadByteArray();
            Key = Key ?? new byte[0];
        }

        public static Archive Unserialize(BinaryReader reader)
        {
            var archive = new Archive();
            archive.UnserializeData(reader);
            return archive;
        }
    }
}
