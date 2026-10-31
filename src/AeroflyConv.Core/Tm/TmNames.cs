using System.Buffers.Binary;

namespace AeroflyConv.Core.Tm;

/// <summary>Type / name ids observed in IPACS containers (hex on disk, little endian).</summary>
public static class TmNames
{
    // ---- property types (first 8 bytes of a chunk header) ----
    public static readonly ulong TypeString8 = Id("626e73bd8c7628a4");
    public static readonly ulong TypeInt32 = Id("a223d13e723446c3");
    public static readonly ulong TypeUInt32 = Id("9e8caf8498703ec2");
    public static readonly ulong TypeUInt64 = Id("bc628ed22a7082f3");
    public static readonly ulong TypeListU8 = Id("9e76560202020202");

    // ---- property names ----
    public static readonly ulong NameOuter = Id("c3e386cb206d5fec");
    public static readonly ulong NameNested = Id("ce25086b845bd426");
    public static readonly ulong NameType = Id("7760d7aaef9e7abb");
    public static readonly ulong NameTarget = Id("3843ed38abf750ef");
    public static readonly ulong NameWidth = Id("8d2fb0411a10b566");
    public static readonly ulong NameHeight = Id("c6ef1dc0d6753c0b");
    public static readonly ulong NameLayers = Id("9e6f762f839d2bf2");
    public static readonly ulong NameMips = Id("51af1172e6fd50f9");
    public static readonly ulong NamePixels = Id("7fc676a557237c3d");
    public static readonly ulong NameSizeUncomp = Id("ce52fa6eeab214e0");
    public static readonly ulong NameSizeComp = Id("830052acb2538fa0");
    public static readonly ulong NameCrcUncomp = Id("e85a613f0a820d7c");
    public static readonly ulong NameCrcComp = Id("195fe06aabefe29c");
    public static readonly ulong NameCompBlob = Id("9cb7d88489f1f234");

    // TM compress blob header
    public const uint TmCompressMagic = 0xA810BEF4;
    public const int TmCompressHeaderSize = 64;
    public const ulong TmCompressConst = 0x17F34DF32797945C;
    public const int LzhamDictLog2 = 21;
    public const int LzhamTableRate = 20;

    /// <summary>Parses a 16 char hex id (as printed by <c>tm_ids.py</c>) into a ulong.</summary>
    public static ulong Id(string hex) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(hex));
}
