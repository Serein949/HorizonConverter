using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AeroflyConv.Core.Lzham;

/// <summary>
/// Bit-level reader implementing the <c>lzham::symbol_codec</c> decode side (64-bit buffer), including
/// the binary arithmetic coder state. The input pointer may reach the end of the buffer; reads past the
/// end yield zero bits, which matches LZHAM's EOF behaviour when the whole stream is supplied at once.
/// </summary>
internal unsafe struct BitReader
{
    public byte* Ptr;
    public byte* End;

    public ulong BitBuf;
    public int BitCount;

    public uint ArithValue;
    public uint ArithLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureBits()
    {
        while (BitCount < 56)
        {
            byte c = 0;
            if (Ptr < End)
                c = *Ptr++;
            BitCount += 8;
            BitBuf |= (ulong)c << (64 - BitCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits(int numBits)
    {
        if (numBits == 0)
            return 0;

        while (BitCount < numBits)
        {
            byte c = 0;
            if (Ptr < End)
                c = *Ptr++;
            BitCount += 8;
            BitBuf |= (ulong)c << (64 - BitCount);
        }

        uint result = (uint)(BitBuf >> (64 - numBits));
        BitBuf <<= numBits;
        BitCount -= numBits;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveBits(int numBits)
    {
        if (numBits == 0)
            return;

        while (BitCount < numBits)
        {
            byte c = 0;
            if (Ptr < End)
                c = *Ptr++;
            BitCount += 8;
            BitBuf |= (ulong)c << (64 - BitCount);
        }

        BitBuf <<= numBits;
        BitCount -= numBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        int n = BitCount & 7;
        if (n != 0)
            RemoveBits(n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RemoveByteFromBitBuf()
    {
        if (BitCount < 8)
            return -1;
        int result = (int)(BitBuf >> 56);
        BitBuf <<= 8;
        BitCount -= 8;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StartArith()
    {
        ArithLength = 0xFFFFFFFFu;
        ArithValue = GetBits(8) << 24;
        ArithValue |= GetBits(8) << 16;
        ArithValue |= GetBits(8) << 8;
        ArithValue |= GetBits(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeArithBit(ref ushort bit0Prob)
    {
        while (ArithLength < 0x01000000u)
        {
            uint c = GetBits(8);
            ArithValue = (ArithValue << 8) | c;
            ArithLength <<= 8;
        }

        uint x = (uint)bit0Prob * (ArithLength >> 11);
        uint bit = ArithValue >= x ? 1u : 0u;

        if (bit == 0)
        {
            bit0Prob = (ushort)(bit0Prob + ((2048 - bit0Prob) >> 5));
            ArithLength = x;
        }
        else
        {
            bit0Prob = (ushort)(bit0Prob - (bit0Prob >> 5));
            ArithValue -= x;
            ArithLength -= x;
        }

        return bit;
    }
}

/// <summary>Thrown internally when the pre-allocated output buffer is too small; the caller retries bigger.</summary>
internal sealed class LzhamOutputOverflowException : Exception
{
    public readonly long Required;

    public LzhamOutputOverflowException(long required) => Required = required;
}

/// <summary>
/// Port of the core LZHAM decompressor (<c>lzham_lzdecomp.cpp</c>, unbuffered memory variant).
/// The whole decompressed stream is written into one contiguous output buffer; distances are direct
/// offsets into that buffer, exactly like LZHAM's "output unbuffered" mode.
/// </summary>
internal sealed unsafe class LzhamDecoder
{
    private readonly int _dictLog2;
    private readonly int _numLzxSlots;
    private readonly int _startOffset;
    private readonly int _inputLength;

    private readonly HuffmanModel _litTable;
    private readonly HuffmanModel _deltaLitTable;
    private readonly HuffmanModel _mainTable;
    private readonly HuffmanModel _repLenTable0;
    private readonly HuffmanModel _repLenTable1;
    private readonly HuffmanModel _largeLenTable0;
    private readonly HuffmanModel _largeLenTable1;
    private readonly HuffmanModel _distLsbTable;

    // All binary arithmetic models live in one array (6 groups of cNumStates entries) so the hot loop
    // can index it through a single interior reference.
    private const int ArithGroupStride = LzhamTables.NumStates;
    private const int ArithIsMatch = 0;
    private const int ArithIsRep = ArithGroupStride;
    private const int ArithIsRep0 = ArithGroupStride * 2;
    private const int ArithIsRep0SingleByte = ArithGroupStride * 3;
    private const int ArithIsRep1 = ArithGroupStride * 4;
    private const int ArithIsRep2 = ArithGroupStride * 5;
    private readonly ushort[] _arithProbs = new ushort[ArithGroupStride * 6];

    public uint StoredAdler32;

    public static long StatLit, StatDelta, StatMatch, StatMain, StatUpdates;

    
    public static int NumLzxSlotsFor(int dictLog2)
    {
        int idx = dictLog2 - LzhamTables.MinDictSizeLog2;
        if (idx < 0 || idx >= LzhamTables.NumLzxPositionSlots.Length)
            throw new InvalidDataException("LZHAM: unsupported dictionary size log2 " + dictLog2);
        return LzhamTables.NumLzxPositionSlots[idx];
    }

    public LzhamDecoder(int dictLog2, int tableRate, int startOffset, int inputLength)
    {
        _dictLog2 = dictLog2;
        _startOffset = startOffset;
        _inputLength = inputLength;
        _numLzxSlots = NumLzxSlotsFor(dictLog2);

        int rate = tableRate;
        if (rate == 0)
            rate = 20;
        if (rate < 1)
            rate = 1;
        else if (rate > 20)
            rate = 20;
        rate -= 1;

        int maxUpdateInterval = LzhamTables.TableUpdateMaxInterval[rate];
        int slowRate = LzhamTables.TableUpdateSlowRate[rate];

        _litTable = new HuffmanModel(256);
        _deltaLitTable = new HuffmanModel(256);
        _mainTable = new HuffmanModel(LzhamTables.LzxNumSpecialLengths + (_numLzxSlots - LzhamTables.LzxLowestUsableMatchSlot) * 8);
        _repLenTable0 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1));
        _repLenTable1 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1));
        _largeLenTable0 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + LzhamTables.LzxNumSecondaryLengths);
        _largeLenTable1 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + LzhamTables.LzxNumSecondaryLengths);
        _distLsbTable = new HuffmanModel(16);

        _litTable.Init(maxUpdateInterval, slowRate);
        _deltaLitTable.Init(maxUpdateInterval, slowRate);
        _mainTable.Init(maxUpdateInterval, slowRate);
        _repLenTable0.Init(maxUpdateInterval, slowRate);
        _repLenTable1.Init(maxUpdateInterval, slowRate);
        _largeLenTable0.Init(maxUpdateInterval, slowRate);
        _largeLenTable1.Init(maxUpdateInterval, slowRate);
        _distLsbTable.Init(maxUpdateInterval, slowRate);

        ResetArithTables();
    }

    private void ResetArithTables()
    {
        Array.Fill(_arithProbs, (ushort)1024);
    }

    private void ResetHuffTables()
    {
        _litTable.Reset();
        _deltaLitTable.Reset();
        _mainTable.Reset();
        _repLenTable0.Reset();
        _repLenTable1.Reset();
        _largeLenTable0.Reset();
        _largeLenTable1.Reset();
        _distLsbTable.Reset();
    }

    private void ResetAllTables()
    {
        ResetHuffTables();
        ResetArithTables();
    }

    private void ResetHuffmanTableUpdateRates()
    {
        _litTable.ResetUpdateRate();
        _deltaLitTable.ResetUpdateRate();
        _mainTable.ResetUpdateRate();
        _repLenTable0.ResetUpdateRate();
        _repLenTable1.ResetUpdateRate();
        _largeLenTable0.ResetUpdateRate();
        _largeLenTable1.ResetUpdateRate();
        _distLsbTable.ResetUpdateRate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint DecodeHugeMatchLen(ref BitReader br)
    {
        int matchLen = 0;
        do
        {
            uint b = br.GetBits(1);
            if (b == 0)
                break;
            matchLen++;
        } while (matchLen < 3);

        uint k = br.GetBits(LzhamTables.HugeMatchCodeLen[matchLen]);
        return LzhamTables.HugeMatchBaseLen[matchLen] + k;
    }

    /// <summary>
    /// Decodes the raw LZHAM stream at <paramref name="pInput"/> into a newly allocated buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public byte[] Decode(byte* pInput, int initialCapacity)
    {
        if (initialCapacity < 16)
            initialCapacity = 16;

        // Uninitialized: every output byte is written before it can be read back (matches/delta literals
        // only reference already-written history), and the tail is trimmed below.
        var output = GC.AllocateUninitializedArray<byte>(initialCapacity);
        int capacity = initialCapacity;
        int dstOfs = 0;

        fixed (byte* pDstBase = output)
        {
            byte* pDst = pDstBase;
            var br = new BitReader
            {
                Ptr = pInput + _startOffset,
                End = pInput + _inputLength,
            };

            int matchHist0 = 1, matchHist1 = 1, matchHist2 = 1, matchHist3 = 1;
            int curState = 0;

            for (; ; )
            {
                uint blockType = br.GetBits(LzhamTables.BlockHeaderBits);

                if (blockType == LzhamTables.SyncBlock)
                {
                    uint flushType = br.GetBits(LzhamTables.BlockFlushTypeBits);
                    if (flushType == 1)
                        ResetHuffmanTableUpdateRates();
                    else if (flushType == 2)
                        ResetAllTables();

                    br.AlignToByte();

                    uint n = br.GetBits(16);
                    if (n != 0)
                        throw new InvalidDataException("LZHAM: bad sync block");
                    n = br.GetBits(16);
                    if (n != 0xFFFF)
                        throw new InvalidDataException("LZHAM: bad sync block");
                }
                else if (blockType == LzhamTables.RawBlock)
                {
                    uint numRawBytesRemaining = br.GetBits(24);

                    uint checkBits = br.GetBits(8);
                    uint r0 = numRawBytesRemaining & 0xFF;
                    uint r1 = (numRawBytesRemaining >> 8) & 0xFF;
                    uint r2 = (numRawBytesRemaining >> 16) & 0xFF;
                    if (checkBits != ((r0 ^ r1) ^ r2))
                        throw new InvalidDataException("LZHAM: bad raw block length check");

                    numRawBytesRemaining++;

                    br.AlignToByte();

                    // Flush any full bytes already sitting in the bit buffer.
                    do
                    {
                        int b = br.RemoveByteFromBitBuf();
                        if (b < 0)
                            break;

                        if (dstOfs >= capacity)
                            throw new LzhamOutputOverflowException((long)dstOfs + 1);

                        pDst[dstOfs++] = (byte)b;
                        numRawBytesRemaining--;
                    } while (numRawBytesRemaining != 0);

                    // Then bulk-copy the rest straight from the input.
                    if (numRawBytesRemaining != 0)
                    {
                        long required = (long)dstOfs + numRawBytesRemaining;
                        if (required > capacity)
                            throw new LzhamOutputOverflowException(required);

                        long available = br.End - br.Ptr;
                        if (available < numRawBytesRemaining)
                            throw new InvalidDataException("LZHAM: truncated raw block");

                        Unsafe.CopyBlockUnaligned(ref pDst[dstOfs], ref br.Ptr[0], numRawBytesRemaining);
                        br.Ptr += numRawBytesRemaining;
                        dstOfs += (int)numRawBytesRemaining;
                    }
                }
                else if (blockType == LzhamTables.CompBlock)
                {
                    br.StartArith();

                    matchHist0 = matchHist1 = matchHist2 = matchHist3 = 1;
                    curState = 0;

                    uint blockFlushType = br.GetBits(LzhamTables.BlockFlushTypeBits);
                    if (blockFlushType == 1)
                        ResetHuffmanTableUpdateRates();
                    else if (blockFlushType == 2)
                        ResetAllTables();

                    for (; ; )
                    {
                        uint isMatch = br.DecodeArithBit(ref _arithProbs[ArithIsMatch + curState]);

                        if (isMatch == 0)
                        {
                            // Literal.
                            if (dstOfs >= capacity)
                                throw new LzhamOutputOverflowException((long)dstOfs + 1);

                            if (curState < LzhamTables.NumLitStates)
                            {
                                uint r = _litTable.DecodeSymbol(ref br);
                                StatLit++; pDst[dstOfs] = (byte)r;
                            }
                            else
                            {
                                int matchHist0Ofs = dstOfs - matchHist0;
                                if (matchHist0Ofs < 0)
                                    throw new InvalidDataException("LZHAM: bad delta literal distance");
                                byte repLit0 = pDst[matchHist0Ofs];

                                uint r = _deltaLitTable.DecodeSymbol(ref br);
                                StatDelta++; pDst[dstOfs] = (byte)(r ^ repLit0);
                            }

                            curState = LzhamTables.LiteralNextState[curState];
                            dstOfs++;
                        }
                        else
                        {
                            // Match.
                            StatMatch++; int matchLen = 1;

                            uint isRep = br.DecodeArithBit(ref _arithProbs[ArithIsRep + curState]);
                            if (isRep != 0)
                            {
                                uint isRep0 = br.DecodeArithBit(ref _arithProbs[ArithIsRep0 + curState]);
                                if (isRep0 != 0)
                                {
                                    uint isRep0Len1 = br.DecodeArithBit(ref _arithProbs[ArithIsRep0SingleByte + curState]);
                                    if (isRep0Len1 != 0)
                                    {
                                        curState = curState < LzhamTables.NumLitStates ? 9 : 11;
                                    }
                                    else
                                    {
                                        matchLen = (int)(curState >= LzhamTables.NumLitStates ? _repLenTable1 : _repLenTable0).DecodeSymbol(ref br) + LzhamTables.MinMatchLen;
                                        if (matchLen == LzhamTables.MaxMatchLen + 1)
                                            matchLen = (int)DecodeHugeMatchLen(ref br);

                                        curState = curState < LzhamTables.NumLitStates ? 8 : 11;
                                    }
                                }
                                else
                                {
                                    matchLen = (int)(curState >= LzhamTables.NumLitStates ? _repLenTable1 : _repLenTable0).DecodeSymbol(ref br) + LzhamTables.MinMatchLen;
                                    if (matchLen == LzhamTables.MaxMatchLen + 1)
                                        matchLen = (int)DecodeHugeMatchLen(ref br);

                                    uint isRep1 = br.DecodeArithBit(ref _arithProbs[ArithIsRep1 + curState]);
                                    if (isRep1 != 0)
                                    {
                                        int temp = matchHist1;
                                        matchHist1 = matchHist0;
                                        matchHist0 = temp;
                                    }
                                    else
                                    {
                                        uint isRep2 = br.DecodeArithBit(ref _arithProbs[ArithIsRep2 + curState]);
                                        if (isRep2 != 0)
                                        {
                                            int temp = matchHist2;
                                            matchHist2 = matchHist1;
                                            matchHist1 = matchHist0;
                                            matchHist0 = temp;
                                        }
                                        else
                                        {
                                            int temp = matchHist3;
                                            matchHist3 = matchHist2;
                                            matchHist2 = matchHist1;
                                            matchHist1 = matchHist0;
                                            matchHist0 = temp;
                                        }
                                    }

                                    curState = curState < LzhamTables.NumLitStates ? 8 : 11;
                                }
                            }
                            else
                            {
                                // Normal/full match.
                                StatMain++; uint sym = _mainTable.DecodeSymbol(ref br);
                                int s = (int)sym - LzhamTables.LzxNumSpecialLengths;

                                if (s < 0)
                                {
                                    if (s == (int)LzhamTables.LzxSpecialCodeEndOfBlockCode - LzhamTables.LzxNumSpecialLengths)
                                        break;

                                    // Must be cLZXSpecialCodePartialStateReset.
                                    matchHist0 = matchHist1 = matchHist2 = matchHist3 = 1;
                                    curState = 0;
                                    continue;
                                }

                                matchLen = (s & 7) + 2;

                                int matchSlot = (s >> 3) + LzhamTables.LzxLowestUsableMatchSlot;

                                if (matchLen == 9)
                                {
                                    uint e = (curState >= LzhamTables.NumLitStates ? _largeLenTable1 : _largeLenTable0).DecodeSymbol(ref br);
                                    matchLen += (int)e;

                                    if (matchLen == LzhamTables.MaxMatchLen + 1)
                                        matchLen = (int)DecodeHugeMatchLen(ref br);
                                }

                                int numExtraBits = LzhamTables.LzxPositionExtraBits[matchSlot];
                                uint extraBits;

                                if (numExtraBits < 3)
                                {
                                    extraBits = br.GetBits(numExtraBits);
                                }
                                else
                                {
                                    extraBits = 0;
                                    if (numExtraBits > 4)
                                    {
                                        extraBits = br.GetBits(numExtraBits - 4);
                                        extraBits <<= 4;
                                    }

                                    uint j = _distLsbTable.DecodeSymbol(ref br);
                                    extraBits += j;
                                }

                                matchHist3 = matchHist2;
                                matchHist2 = matchHist1;
                                matchHist1 = matchHist0;
                                matchHist0 = (int)(LzhamTables.LzxPositionBase[matchSlot] + extraBits);

                                curState = curState < LzhamTables.NumLitStates ? LzhamTables.NumLitStates : LzhamTables.NumLitStates + 3;
                            }

                            // We have the match's length and distance, now do the copy.
                            if (matchHist0 <= 0 || (uint)matchHist0 > (uint)dstOfs)
                                throw new InvalidDataException("LZHAM: bad match distance");

                            long required = (long)dstOfs + matchLen;
                            if (required > capacity)
                                throw new LzhamOutputOverflowException(required);

                            int srcOfs = dstOfs - matchHist0;

                            if (matchHist0 == 1)
                            {
                                // Byte runs.
                                byte c = pDst[srcOfs];
                                if (matchLen < 8)
                                {
                                    for (int i = matchLen; i > 0; i--)
                                        pDst[dstOfs++] = c;
                                }
                                else
                                {
                                    Unsafe.InitBlock(ref pDst[dstOfs], c, (uint)matchLen);
                                    dstOfs += matchLen;
                                }
                            }
                            else if (matchLen < 8 || matchLen > matchHist0)
                            {
                                byte* pCopyDst = pDst + dstOfs;
                                byte* pCopySrc = pDst + srcOfs;
                                for (int i = matchLen; i > 0; i--)
                                    *pCopyDst++ = *pCopySrc++;
                                dstOfs += matchLen;
                            }
                            else
                            {
                                Unsafe.CopyBlockUnaligned(ref pDst[dstOfs], ref pDst[srcOfs], (uint)matchLen);
                                dstOfs += matchLen;
                            }
                        }
                    }

                    br.AlignToByte();
                }
                else if (blockType == LzhamTables.EofBlock)
                {
                    break;
                }
                else
                {
                    throw new InvalidDataException("LZHAM: undefined block type");
                }
            }

            // Trailing adler32 (of the uncompressed stream) after the EOF block.
            br.AlignToByte();
            uint adlerHi = br.GetBits(16);
            uint adlerLo = br.GetBits(16);
            StoredAdler32 = (adlerHi << 16) | adlerLo;
        }

        if (dstOfs != output.Length)
        {
            var exact = new byte[dstOfs];
            Buffer.BlockCopy(output, 0, exact, 0, dstOfs);
            output = exact;
        }

        return output;
    }
}
