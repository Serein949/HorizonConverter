using System.Runtime.CompilerServices;

namespace AeroflyConv.Core.Lzham;

internal struct SymFreq
{
    public uint Freq;
    public ushort Left;
    public ushort Right;
}

/// <summary>
/// Port of <c>lzham::raw_quasi_adaptive_huffman_data_model</c> (decoder side) plus the prefix coding
/// helpers from <c>lzham_prefix_coding.cpp</c> and <c>lzham_huffman_codes.cpp</c>.
/// </summary>
internal sealed class HuffmanModel
{
    private const int MaxExpectedCodeSize = 16;
    private const int MaxSupportedSyms = 1024;

    public readonly int NumSyms;
    public readonly ushort[] SymFreq;
    private readonly byte[] _codeSizes;

    // Decoder tables.
    private readonly uint[] _maxCodes = new uint[MaxExpectedCodeSize + 2];
    private readonly int[] _valPtrs = new int[MaxExpectedCodeSize + 2];
    private readonly ushort[] _sorted;
    private uint[]? _lookup;
    private int _decoderTableBits;

    // Huffman generation scratch.
    private readonly SymFreq[] _syms0;
    private readonly SymFreq[] _syms1;
    private readonly int[] _x;

    private int _maxUpdateInterval;
    private int _adaptRate;

    public int SymbolsUntilUpdate;
    public int UpdateCycle;
    public int MaxCycle;
    public int TotalCount;

    public int TableBits;
    public int TableShift;
    public uint TableMaxCode;
    public int DecodeStartCodeSize;
    public int MinCodeSize;
    public int MaxCodeSize;
    public int TotalUsedSyms;

    public HuffmanModel(int numSyms)
    {
        if (numSyms <= 0 || numSyms > MaxSupportedSyms)
            throw new InvalidDataException("LZHAM: invalid adaptive Huffman symbol count " + numSyms);

        NumSyms = numSyms;
        SymFreq = new ushort[numSyms];
        _codeSizes = new byte[numSyms];
        _syms0 = new SymFreq[numSyms];
        _syms1 = new SymFreq[numSyms];
        _x = new int[numSyms];
        _sorted = new ushort[numSyms];
    }

    public void Init(int maxUpdateInterval, int adaptRate)
    {
        _maxUpdateInterval = maxUpdateInterval;
        _adaptRate = adaptRate;
        SymbolsUntilUpdate = 0;

        int maxTableBits = NumSyms <= 8 ? 4 : 1 + CeilLog2((uint)NumSyms);
        _decoderTableBits = Math.Min(maxTableBits, 11);

        MaxCycle = Math.Min((Math.Max(24, NumSyms) + 6) * (maxUpdateInterval != 0 ? maxUpdateInterval : 64), 32767);

        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < NumSyms; i++)
            SymFreq[i] = 1;

        // Slam update cycle to a specific value so UpdateTables() sets TotalCount to the proper value.
        UpdateCycle = NumSyms;
        TotalCount = 0;
        SymbolsUntilUpdate = 0;

        UpdateTables(Math.Min(MaxCycle, 16), symFreqAllOnes: true);
    }

    public void Rescale()
    {
        uint totalFreq = 0;
        for (int i = 0; i < NumSyms; i++)
        {
            uint freq = (uint)((SymFreq[i] + 1) >> 1);
            totalFreq += freq;
            SymFreq[i] = (ushort)freq;
        }
        TotalCount = (int)totalFreq;
    }

    public void ResetUpdateRate()
    {
        TotalCount += UpdateCycle - SymbolsUntilUpdate;

        if (TotalCount > NumSyms)
            Rescale();

        SymbolsUntilUpdate = UpdateCycle = Math.Min(8, UpdateCycle);
    }

    public void UpdateTables(int forceUpdateCycle = -1, bool symFreqAllOnes = false)
    {
        LzhamDecoder.StatUpdates++; TotalCount += UpdateCycle;

        while (TotalCount >= 32768)
            Rescale();

        int maxCodeSize = 0;

        if (symFreqAllOnes && NumSyms >= 2)
        {
            // Shortcut building the Huffman codes if we know all the symbol freqs are 1.
            int baseCodeSize = FloorLog2((uint)NumSyms);
            int numLeft = NumSyms - (1 << baseCodeSize);
            numLeft *= 2;
            if (numLeft > NumSyms)
                numLeft = NumSyms;

            Array.Fill(_codeSizes, (byte)(baseCodeSize + 1), 0, numLeft);
            Array.Fill(_codeSizes, (byte)baseCodeSize, numLeft, NumSyms - numLeft);

            maxCodeSize = baseCodeSize + (numLeft != 0 ? 1 : 0);
        }

        if (maxCodeSize == 0)
        {
            if (!GenerateHuffmanCodes(out maxCodeSize, out int totalFreq))
                throw new InvalidDataException("LZHAM: Huffman code generation failed");
            if (totalFreq != TotalCount)
                throw new InvalidDataException("LZHAM: Huffman frequency mismatch");

            if (maxCodeSize > MaxExpectedCodeSize)
            {
                if (!LimitMaxCodeSize(MaxExpectedCodeSize))
                    throw new InvalidDataException("LZHAM: Huffman code size limiting failed");
            }
        }

        if (forceUpdateCycle >= 0)
        {
            SymbolsUntilUpdate = UpdateCycle = forceUpdateCycle;
        }
        else
        {
            UpdateCycle = (int)((31U + (uint)UpdateCycle * (uint)Math.Max(32, _adaptRate != 0 ? _adaptRate : 64)) >> 5);
            if (UpdateCycle > MaxCycle)
                UpdateCycle = MaxCycle;
            SymbolsUntilUpdate = UpdateCycle;
        }

        // Try to see if using the accel table is actually worth the trouble of constructing it.
        int actualTableBits = _decoderTableBits;
        int costToUseTable = (1 << actualTableBits) + 64;
        int costToNotUseTable = SymbolsUntilUpdate * FloorLog2((uint)NumSyms);
        if (costToNotUseTable <= costToUseTable)
            actualTableBits = 0;

        GenerateDecoderTables(actualTableBits);
    }

    private bool GenerateHuffmanCodes(out int maxCodeSizeOut, out int totalFreqRet)
    {
        maxCodeSizeOut = 0;
        totalFreqRet = 0;

        uint maxFreq = 0;
        uint totalFreq = 0;
        int numUsedSyms = 0;

        for (int i = 0; i < NumSyms; i++)
        {
            uint freq = SymFreq[i];
            if (freq == 0)
            {
                _codeSizes[i] = 0;
            }
            else
            {
                totalFreq += freq;
                if (freq > maxFreq)
                    maxFreq = freq;

                ref SymFreq sf = ref _syms0[numUsedSyms];
                sf.Left = (ushort)i;
                sf.Right = ushort.MaxValue;
                sf.Freq = freq;
                numUsedSyms++;
            }
        }

        totalFreqRet = (int)totalFreq;

        if (numUsedSyms == 1)
        {
            _codeSizes[_syms0[0].Left] = 1;
            return true;
        }

        SymFreq[] syms = RadixSortSyms(numUsedSyms, _syms0, _syms1);

        for (int i = 0; i < numUsedSyms; i++)
            _x[i] = (int)syms[i].Freq;

        CalculateMinimumRedundancy(_x, numUsedSyms);

        int maxLen = 0;
        for (int i = 0; i < numUsedSyms; i++)
        {
            int len = _x[i];
            if (len > maxLen)
                maxLen = len;
            _codeSizes[syms[i].Left] = (byte)len;
        }

        maxCodeSizeOut = maxLen;
        return true;
    }

    private static SymFreq[] RadixSortSyms(int numSyms, SymFreq[] syms0, SymFreq[] syms1)
    {
        const int cMaxPasses = 2;
        Span<int> hist = stackalloc int[256 * cMaxPasses];
        hist.Clear();

        SymFreq[] pCurSyms = syms0;
        SymFreq[] pNewSyms = syms1;

        {
            SymFreq[] p = syms0;
            int q = (numSyms >> 1) * 2;

            for (int i = 0; i < q; i += 2)
            {
                uint freq0 = p[i].Freq;
                uint freq1 = p[i + 1].Freq;

                hist[(int)(freq0 & 0xFF)]++;
                hist[256 + (int)((freq0 >> 8) & 0xFF)]++;

                hist[(int)(freq1 & 0xFF)]++;
                hist[256 + (int)((freq1 >> 8) & 0xFF)]++;
            }

            if ((numSyms & 1) != 0)
            {
                uint freq = p[q].Freq;
                hist[(int)(freq & 0xFF)]++;
                hist[256 + (int)((freq >> 8) & 0xFF)]++;
            }
        }

        int totalPasses = (hist[256] == numSyms) ? 1 : cMaxPasses;

        Span<int> offsets = stackalloc int[256];

        for (int pass = 0; pass < totalPasses; pass++)
        {
            offsets.Clear();
            int histOfs = pass << 8;

            int curOfs = 0;
            for (int i = 0; i < 256; i += 2)
            {
                offsets[i] = curOfs;
                curOfs += hist[histOfs + i];

                offsets[i + 1] = curOfs;
                curOfs += hist[histOfs + i + 1];
            }

            int passShift = pass << 3;

            SymFreq[] p = pCurSyms;
            int q = (numSyms >> 1) * 2;

            for (int i = 0; i < q; i += 2)
            {
                uint c0 = p[i].Freq;
                uint c1 = p[i + 1].Freq;

                if (pass != 0)
                {
                    c0 >>= 8;
                    c1 >>= 8;
                }

                c0 &= 0xFF;
                c1 &= 0xFF;

                if (c0 == c1)
                {
                    int dstOffset0 = offsets[(int)c0];
                    offsets[(int)c0] = dstOffset0 + 2;

                    pNewSyms[dstOffset0] = p[i];
                    pNewSyms[dstOffset0 + 1] = p[i + 1];
                }
                else
                {
                    int dstOffset0 = offsets[(int)c0]++;
                    int dstOffset1 = offsets[(int)c1]++;

                    pNewSyms[dstOffset0] = p[i];
                    pNewSyms[dstOffset1] = p[i + 1];
                }
            }

            if ((numSyms & 1) != 0)
            {
                uint c = (p[q].Freq >> passShift) & 0xFF;
                int dstOffset = offsets[(int)c];
                offsets[(int)c] = dstOffset + 1;
                pNewSyms[dstOffset] = p[q];
            }

            SymFreq[] t = pCurSyms;
            pCurSyms = pNewSyms;
            pNewSyms = t;
        }

        return pCurSyms;
    }

    private static void CalculateMinimumRedundancy(int[] a, int n)
    {
        if (n == 0)
            return;
        if (n == 1)
        {
            a[0] = 0;
            return;
        }

        a[0] += a[1];
        int root = 0, leaf = 2;

        for (int next = 1; next < n - 1; next++)
        {
            if (leaf >= n || a[root] < a[leaf])
            {
                a[next] = a[root];
                a[root++] = next;
            }
            else
            {
                a[next] = a[leaf++];
            }

            if (leaf >= n || (root < next && a[root] < a[leaf]))
            {
                a[next] += a[root];
                a[root++] = next;
            }
            else
            {
                a[next] += a[leaf++];
            }
        }

        a[n - 2] = 0;
        for (int next = n - 3; next >= 0; next--)
            a[next] = a[a[next]] + 1;

        int avbl = 1, used = 0, dpth = 0;
        root = n - 2;
        int nxt = n - 1;

        while (avbl > 0)
        {
            while (root >= 0 && a[root] == dpth)
            {
                used++;
                root--;
            }
            while (avbl > used)
            {
                a[nxt--] = dpth;
                avbl--;
            }
            avbl = 2 * used;
            dpth++;
            used = 0;
        }
    }

    private bool LimitMaxCodeSize(int maxCodeSize)
    {
        const int MaxEverCodeSize = 34;

        if (NumSyms == 0 || NumSyms > MaxSupportedSyms || maxCodeSize < 1 || maxCodeSize > MaxEverCodeSize)
            return false;

        Span<int> numCodes = stackalloc int[MaxEverCodeSize + 1];
        numCodes.Clear();

        bool shouldLimit = false;

        for (int i = 0; i < NumSyms; i++)
        {
            int c = _codeSizes[i];
            numCodes[c]++;
            if (c > maxCodeSize)
                shouldLimit = true;
        }

        if (!shouldLimit)
            return true;

        int ofs = 0;
        Span<int> nextSortedOfs = stackalloc int[MaxEverCodeSize + 1];
        for (int i = 1; i <= MaxEverCodeSize; i++)
        {
            nextSortedOfs[i] = ofs;
            ofs += numCodes[i];
        }

        if (ofs < 2 || ofs > MaxSupportedSyms)
            return true;

        if (ofs > (1 << maxCodeSize))
            return false;

        for (int i = maxCodeSize + 1; i <= MaxEverCodeSize; i++)
            numCodes[maxCodeSize] += numCodes[i];

        int total = 0;
        for (int i = maxCodeSize; i > 0; --i)
            total += numCodes[i] << (maxCodeSize - i);

        if (total == (1 << maxCodeSize))
            return true;

        do
        {
            numCodes[maxCodeSize]--;

            int i;
            for (i = maxCodeSize - 1; i > 0; --i)
            {
                if (numCodes[i] == 0)
                    continue;
                numCodes[i]--;
                numCodes[i + 1] += 2;
                break;
            }
            if (i == 0)
                return false;

            total--;
        } while (total != (1 << maxCodeSize));

        Span<byte> newCodeSizes = stackalloc byte[MaxSupportedSyms];
        int pos = 0;
        for (int i = 1; i <= maxCodeSize; i++)
        {
            int n = numCodes[i];
            if (n != 0)
            {
                newCodeSizes.Slice(pos, n).Fill((byte)i);
                pos += n;
            }
        }

        for (int i = 0; i < NumSyms; i++)
        {
            int c = _codeSizes[i];
            if (c != 0)
            {
                int nextOfs = nextSortedOfs[c];
                nextSortedOfs[c] = nextOfs + 1;
                _codeSizes[i] = newCodeSizes[nextOfs];
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetUnshiftedMaxCode(int len)
    {
        uint k = _maxCodes[len - 1];
        if (k == 0)
            return uint.MaxValue;
        return (k - 1) >> (16 - len);
    }

    private void GenerateDecoderTables(int tableBits)
    {
        if (NumSyms == 0 || tableBits > 11)
            throw new InvalidDataException("LZHAM: invalid decoder table request");

        Span<int> numCodes = stackalloc int[MaxExpectedCodeSize + 1];
        numCodes.Clear();
        Span<uint> minCodes = stackalloc uint[MaxExpectedCodeSize];
        minCodes.Clear(); // C++ leaves unused entries uninitialized; the values are never read for used lengths.
        Span<int> sortedPositions = stackalloc int[MaxExpectedCodeSize + 1];

        for (int i = 0; i < NumSyms; i++)
        {
            int c = _codeSizes[i];
            numCodes[c]++;
        }

        uint nextCode = 0;
        int totalUsedSyms = 0;
        int maxCodeSize = 0;
        int minCodeSize = int.MaxValue;

        for (int i = 1; i <= MaxExpectedCodeSize; i++)
        {
            int n = numCodes[i];

            if (n == 0)
            {
                _maxCodes[i - 1] = 0;
            }
            else
            {
                if (i < minCodeSize)
                    minCodeSize = i;
                if (i > maxCodeSize)
                    maxCodeSize = i;

                minCodes[i - 1] = nextCode;

                _maxCodes[i - 1] = nextCode + (uint)n - 1;
                _maxCodes[i - 1] = 1 + ((_maxCodes[i - 1] << (16 - i)) | ((1u << (16 - i)) - 1));

                _valPtrs[i - 1] = totalUsedSyms;
                sortedPositions[i] = totalUsedSyms;

                nextCode += (uint)n;
                totalUsedSyms += n;
            }

            nextCode <<= 1;
        }

        TotalUsedSyms = totalUsedSyms;

        MinCodeSize = minCodeSize;
        MaxCodeSize = maxCodeSize;

        for (int i = 0; i < NumSyms; i++)
        {
            int c = _codeSizes[i];
            if (c != 0)
            {
                int sortedPos = sortedPositions[c]++;
                _sorted[sortedPos] = (ushort)i;
            }
        }

        if (tableBits <= MinCodeSize)
            tableBits = 0;
        TableBits = tableBits;

        if (tableBits != 0)
        {
            int tableSize = 1 << tableBits;
            if (_lookup == null || _lookup.Length < tableSize)
                _lookup = new uint[1 << _decoderTableBits];

            Span<uint> lookup = _lookup.AsSpan(0, tableSize);
            lookup.Fill(uint.MaxValue);

            for (int codeSize = 1; codeSize <= tableBits; codeSize++)
            {
                if (numCodes[codeSize] == 0)
                    continue;

                int fillSize = tableBits - codeSize;
                uint fillNum = 1u << fillSize;

                uint minCode = minCodes[codeSize - 1];
                uint maxCode = GetUnshiftedMaxCode(codeSize);
                int valPtr = _valPtrs[codeSize - 1];

                for (uint code = minCode; code <= maxCode; code++)
                {
                    uint symIndex = _sorted[valPtr + (int)(code - minCode)];

                    for (uint j = 0; j < fillNum; j++)
                    {
                        int t = (int)(j + (code << fillSize));
                        lookup[t] = symIndex | ((uint)codeSize << 16);
                    }
                }
            }
        }

        for (int i = 0; i < MaxExpectedCodeSize; i++)
            _valPtrs[i] -= (int)minCodes[i];

        TableMaxCode = 0;
        DecodeStartCodeSize = MinCodeSize;

        if (tableBits != 0)
        {
            int i;
            for (i = tableBits; i >= 1; i--)
            {
                if (numCodes[i] != 0)
                {
                    TableMaxCode = _maxCodes[i - 1];
                    break;
                }
            }
            if (i >= 1)
            {
                DecodeStartCodeSize = tableBits + 1;
                for (i = tableBits + 1; i <= maxCodeSize; i++)
                {
                    if (numCodes[i] != 0)
                    {
                        DecodeStartCodeSize = i;
                        break;
                    }
                }
            }
        }

        // Sentinels.
        _maxCodes[MaxExpectedCodeSize] = uint.MaxValue;
        _valPtrs[MaxExpectedCodeSize] = 0xFFFFF;

        TableShift = 32 - TableBits;
    }

    /// <summary>Decodes one symbol, updates the model and (rarely) rebuilds its tables.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeSymbol(ref BitReader br)
    {
        br.EnsureBits();

        uint k = (uint)((br.BitBuf >> 48) + 1);
        uint sym;
        uint len;

        if (k <= TableMaxCode)
        {
            uint t = _lookup![(int)(br.BitBuf >> (64 - TableBits))];
            sym = t & 0xFFFF;
            len = t >> 16;
        }
        else
        {
            len = (uint)DecodeStartCodeSize;
            while (k > _maxCodes[len - 1])
                len++;

            int valPtr = _valPtrs[len - 1] + (int)(br.BitBuf >> (64 - (int)len));
            if ((uint)valPtr >= (uint)NumSyms)
                throw new InvalidDataException("LZHAM: corrupt Huffman symbol");

            sym = _sorted[valPtr];
        }

        br.BitBuf <<= (int)len;
        br.BitCount -= (int)len;

        uint freq = (uint)(SymFreq[sym] + 1);
        SymFreq[sym] = (ushort)freq;

        if (--SymbolsUntilUpdate == 0)
            UpdateTables();

        return sym;
    }

    public static int FloorLog2(uint v)
    {
        int l = 0;
        while (v > 1)
        {
            v >>= 1;
            l++;
        }
        return l;
    }

    public static int CeilLog2(uint v)
    {
        int l = FloorLog2(v);
        if (v > (1u << l))
            l++;
        return l;
    }
}
