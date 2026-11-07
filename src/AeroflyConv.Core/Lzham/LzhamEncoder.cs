using System.Buffers.Binary;
using System.Numerics;

namespace AeroflyConv.Core.Lzham;

/// <summary>
/// The adaptive model set shared by every block of one LZHAM stream: the six binary arithmetic models
/// (indexed by coding state) and the eight adaptive Huffman models. Mirrors
/// <c>lzhamcomp/lzham_lzcomp_internal.h:245-251</c> and initializes exactly like <see cref="LzhamDecoder"/>,
/// so encoder and decoder stay in lock-step symbol for symbol.
/// </summary>
internal sealed class LzhamEncoderState
{
    public const int ArithGroupStride = LzhamTables.NumStates;

    public const int ArithIsMatch = 0;
    public const int ArithIsRep = ArithGroupStride * 1;
    public const int ArithIsRep0 = ArithGroupStride * 2;
    public const int ArithIsRep0SingleByte = ArithGroupStride * 3;
    public const int ArithIsRep1 = ArithGroupStride * 4;
    public const int ArithIsRep2 = ArithGroupStride * 5;

    public readonly ushort[] ArithProbs = new ushort[ArithGroupStride * 6];

    public readonly HuffmanModel LitTable;
    public readonly HuffmanModel DeltaLitTable;
    public readonly HuffmanModel MainTable;
    public readonly HuffmanModel RepLenTable0;
    public readonly HuffmanModel RepLenTable1;
    public readonly HuffmanModel LargeLenTable0;
    public readonly HuffmanModel LargeLenTable1;
    public readonly HuffmanModel DistLsbTable;

    public LzhamEncoderState(int dictLog2, int tableRate)
    {
        int numLzxSlots = LzhamDecoder.NumLzxSlotsFor(dictLog2);

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

        LitTable = new HuffmanModel(256);
        DeltaLitTable = new HuffmanModel(256);
        MainTable = new HuffmanModel(LzhamTables.LzxNumSpecialLengths + (numLzxSlots - LzhamTables.LzxLowestUsableMatchSlot) * 8);
        RepLenTable0 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1));
        RepLenTable1 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1));
        LargeLenTable0 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + LzhamTables.LzxNumSecondaryLengths);
        LargeLenTable1 = new HuffmanModel(LzhamTables.NumHugeMatchCodes + LzhamTables.LzxNumSecondaryLengths);
        DistLsbTable = new HuffmanModel(16);

        LitTable.Init(maxUpdateInterval, slowRate);
        DeltaLitTable.Init(maxUpdateInterval, slowRate);
        MainTable.Init(maxUpdateInterval, slowRate);
        RepLenTable0.Init(maxUpdateInterval, slowRate);
        RepLenTable1.Init(maxUpdateInterval, slowRate);
        LargeLenTable0.Init(maxUpdateInterval, slowRate);
        LargeLenTable1.Init(maxUpdateInterval, slowRate);
        DistLsbTable.Init(maxUpdateInterval, slowRate);

        Array.Fill(ArithProbs, (ushort)1024);
    }

    /// <summary>
    /// Copies the whole adaptive state. Used to compress a block optimistically: if the result would
    /// not be smaller than the raw bytes, the copy is thrown away and the original state survives
    /// untouched (the reference encoder reverts its state the same way).
    /// </summary>
    public LzhamEncoderState Clone()
    {
        var clone = new LzhamEncoderState(0, 0, LitTable, DeltaLitTable, MainTable, RepLenTable0,
            RepLenTable1, LargeLenTable0, LargeLenTable1, DistLsbTable);
        Array.Copy(ArithProbs, clone.ArithProbs, ArithProbs.Length);
        return clone;
    }

    // Private copy constructor used by Clone(); the table objects are already cloned by the caller.
    private LzhamEncoderState(
        int _,
        int __,
        HuffmanModel lit,
        HuffmanModel deltaLit,
        HuffmanModel main,
        HuffmanModel repLen0,
        HuffmanModel repLen1,
        HuffmanModel largeLen0,
        HuffmanModel largeLen1,
        HuffmanModel distLsb)
    {
        LitTable = lit.Clone();
        DeltaLitTable = deltaLit.Clone();
        MainTable = main.Clone();
        RepLenTable0 = repLen0.Clone();
        RepLenTable1 = repLen1.Clone();
        LargeLenTable0 = largeLen0.Clone();
        LargeLenTable1 = largeLen1.Clone();
        DistLsbTable = distLsb.Clone();
    }
}

/// <summary>
/// Records the symbols of one block in encoding order and assembles them into bytes exactly like the
/// reference <c>symbol_codec</c> (bit symbols interleaved with a mirrored view of the arithmetic
/// decoder's state, so the arithmetic coder's output bytes land in the same places the decoder expects).
/// </summary>
internal sealed class LzhamBlockEncoder
{
    private const uint ArithMinLen = 0x01000000u;
    private const uint ArithMaxLen = 0xFFFFFFFFu;

    private struct RecordedSym
    {
        public uint Bits;
        public int NumBits;      // -1 = arithmetic bit, -2 = arithmetic-coder init, >=0 = plain bits
        public ushort Prob0;
    }

    private RecordedSym[] _syms;
    private int _count;

    private byte[] _arithBuf;
    private int _arithLen;
    private uint _arithBase;
    private uint _arithLength;

    private readonly LzhamEncoderState _state;

    public LzhamBlockEncoder(LzhamEncoderState state, int expectedBytes)
    {
        _state = state;
        _syms = new RecordedSym[Math.Max(64, expectedBytes)];
        _arithBuf = new byte[Math.Max(64, expectedBytes)];
        _arithLength = ArithMaxLen;
    }

    private void AddSym(uint bits, int numBits, ushort prob0)
    {
        if (_count == _syms.Length)
            Array.Resize(ref _syms, _syms.Length * 2);

        _syms[_count++] = new RecordedSym { Bits = bits, NumBits = numBits, Prob0 = prob0 };
    }

    /// <summary>Plain bit write (the reference <c>encode_bits</c>): values wider than 16 bits are split.</summary>
    public void WriteBits(uint bits, int numBits)
    {
        if (numBits <= 0)
            return;

        if (numBits > 16)
        {
            AddSym(bits >> 16, numBits - 16, 0);
            AddSym(bits & 0xFFFF, 16, 0);
        }
        else
        {
            AddSym(bits, numBits, 0);
        }
    }

    /// <summary>Emits the arithmetic coder init marker; the decoder reads its four seed bytes here.</summary>
    public void ArithInit() => AddSym(0, -2, 0);

    /// <summary>Arithmetic-codes one bit, updating the model exactly like the decoder does.</summary>
    public void EncodeArithBit(int bit, ref ushort prob0)
    {
        AddSym((uint)bit, -1, prob0);

        uint x = (uint)prob0 * (_arithLength >> 11);

        if (bit == 0)
        {
            prob0 = (ushort)(prob0 + ((2048 - prob0) >> 5));
            _arithLength = x;
        }
        else
        {
            prob0 = (ushort)(prob0 - (prob0 >> 5));
            uint origBase = _arithBase;
            _arithBase += x;
            _arithLength -= x;

            if (origBase > _arithBase)
                PropagateCarry();
        }

        if (_arithLength < ArithMinLen)
            RenormEncInterval();
    }

    /// <summary>Writes one Huffman symbol and advances that model's adaptive state.</summary>
    public void EncodeHuff(HuffmanModel model, int sym)
    {
        int numBits = model.CodeSize(sym);
        if (numBits > 0)
            AddSym(model.Code(sym), numBits, 0);

        model.EncodeSymbol(sym);
    }

    private void PropagateCarry()
    {
        int index = _arithLen - 1;
        while (index >= 0)
        {
            if (_arithBuf[index] == 0xFF)
            {
                _arithBuf[index] = 0;
            }
            else
            {
                _arithBuf[index]++;
                break;
            }
            index--;
        }
    }

    private void RenormEncInterval()
    {
        do
        {
            EnsureArith(1);
            _arithBuf[_arithLen++] = (byte)(_arithBase >> 24);
            _arithBase <<= 8;
            _arithLength <<= 8;
        }
        while (_arithLength < ArithMinLen);
    }

    private void EnsureArith(int extra)
    {
        if (_arithLen + extra <= _arithBuf.Length)
            return;

        int newSize = _arithBuf.Length * 2;
        while (newSize < _arithLen + extra)
            newSize *= 2;
        Array.Resize(ref _arithBuf, newSize);
    }

    private void ArithStop()
    {
        uint origBase = _arithBase;

        if (_arithLength > 2 * ArithMinLen)
        {
            _arithBase += ArithMinLen;
            _arithLength = ArithMinLen >> 1;
        }
        else
        {
            _arithBase += ArithMinLen >> 1;
            _arithLength = ArithMinLen >> 9;
        }

        if (origBase > _arithBase)
            PropagateCarry();

        RenormEncInterval();

        while (_arithLen < 4)
        {
            EnsureArith(1);
            _arithBuf[_arithLen++] = 0;
        }
    }

    /// <summary>
    /// Assembles the recorded symbols into the final byte stream. This is the exact mirror of the
    /// reference <c>assemble_output_buf()</c>: it replays the decoder's arithmetic state so the emitted
    /// raw bytes are pulled from the arithmetic buffer at precisely the right moments.
    /// </summary>
    public byte[] Finish()
    {
        ArithStop();

        var writer = new LzhamBitWriter(_count * 2 + 64);

        uint arithLength = 0;
        uint arithValue = 0;
        int arithOfs = 0;

        for (int i = 0; i < _count; i++)
        {
            RecordedSym sym = _syms[i];

            if (sym.NumBits == -2)
            {
                // Arithmetic coder init: the decoder pulls four bytes and seeds value/length with them.
                arithLength = ArithMaxLen;
                arithValue = 0;

                for (int j = 0; j < 4; j++)
                {
                    byte c = arithOfs < _arithLen ? _arithBuf[arithOfs++] : (byte)0;
                    arithValue = (arithValue << 8) | c;
                    writer.PutBits(c, 8);
                }
            }
            else if (sym.NumBits == -1)
            {
                // Mirror the decoder's renormalisation before it would consume this bit's bytes.
                if (arithLength < ArithMinLen)
                {
                    do
                    {
                        byte c = arithOfs < _arithLen ? _arithBuf[arithOfs++] : (byte)0;
                        writer.PutBits(c, 8);
                        arithValue = (arithValue << 8) | c;
                        arithLength <<= 8;
                    }
                    while (arithLength < ArithMinLen);
                }

                uint x = (uint)sym.Prob0 * (arithLength >> 11);
                bool bit = arithValue >= x;

                if (bit)
                {
                    arithValue -= x;
                    arithLength -= x;
                }
                else
                {
                    arithLength = x;
                }

                // Self-check: the recorded bit must match what the decoder will see. If this ever fires
                // the arithmetic coder bookkeeping is wrong and the stream would be undecodable.
                if ((uint)(bit ? 1 : 0) != sym.Bits)
                    throw new InvalidOperationException("LZHAM encoder: arithmetic coder self-check failed");
            }
            else
            {
                writer.PutBits(sym.Bits, sym.NumBits);
            }
        }

        writer.FlushBits();
        return writer.ToArray();
    }
}

/// <summary>
/// Straightforward hash-chain match finder (3-byte hash for long matches, 2-byte hash for the shortest
/// ones). It is deliberately simple - ratio matters, cycle-level micro-optimisation does not.
/// </summary>
internal sealed class LzhamMatchFinder
{
    private const int HashLog = 18;
    private const int HashSize = 1 << HashLog;
    private const int Hash2Size = 1 << 16;
    private const int MaxChainDepth = 64;

    private readonly int[] _head;
    private readonly int[] _prev;
    private readonly int[] _head2;
    private readonly int _maxDist;
    private int _inserted;

    public LzhamMatchFinder(int length, int dictLog2)
    {
        _head = new int[HashSize];
        _prev = new int[Math.Max(1, length)];
        _head2 = new int[Hash2Size];
        Array.Fill(_head, -1);
        Array.Fill(_head2, -1);
        _maxDist = (1 << dictLog2) - 1;
    }

    private static uint Hash3(ReadOnlySpan<byte> data, int pos)
    {
        uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
        return (v * 2654435761u) >> (32 - HashLog);
    }

    /// <summary>Inserts every position below <paramref name="upTo"/> into the chains (each exactly once).</summary>
    public void EnsureInserted(ReadOnlySpan<byte> data, int upTo)
    {
        int limit = Math.Min(upTo, data.Length - 2);
        while (_inserted < limit)
        {
            int p = _inserted++;
            uint h = Hash3(data, p);
            _prev[p] = _head[h];
            _head[h] = p;

            int h2 = data[p] | (data[p + 1] << 8);
            _head2[h2] = p;
        }

        if (_inserted < upTo)
            _inserted = upTo;
    }

    /// <summary>Finds the longest match for <paramref name="pos"/> using the hash chains.</summary>
    public void FindMatch(ReadOnlySpan<byte> data, int pos, int maxLen, out int dist, out int len)
    {
        dist = 0;
        len = 0;

        if (maxLen < LzhamTables.MinMatchLen || pos == 0)
            return;

        int lowest = Math.Max(0, pos - _maxDist);

        // A two-byte match is a valid LZHAM match, and the 3-byte chain cannot find those.
        if (maxLen >= 2)
        {
            int cand = _head2[data[pos] | (data[pos + 1] << 8)];
            if (cand >= lowest && cand < pos)
            {
                int l = MatchLen(data, pos, cand, maxLen);
                if (l >= 2)
                {
                    len = l;
                    dist = pos - cand;
                }
            }
        }

        if (maxLen < 3 || pos + 2 >= data.Length)
            return;

        int cur = _head[Hash3(data, pos)];
        int depth = MaxChainDepth;

        while (cur >= lowest && depth-- > 0)
        {
            if (cur >= pos)
            {
                cur = _prev[cur];
                continue;
            }

            // Cheap reject: the candidate cannot beat the current best if it differs at that offset.
            if (len < maxLen && data[cur + len] == data[pos + len])
            {
                int l = MatchLen(data, pos, cur, maxLen);
                if (l > len)
                {
                    len = l;
                    dist = pos - cur;
                    if (l >= maxLen)
                        break;
                }
            }

            cur = _prev[cur];
        }
    }

    private static int MatchLen(ReadOnlySpan<byte> data, int a, int b, int maxLen)
    {
        int n = 0;

        while (n + 8 <= maxLen)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(a + n))
                    ^ BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(b + n));
            if (x != 0)
                return n + (BitOperations.TrailingZeroCount(x) >> 3);
            n += 8;
        }

        while (n < maxLen && data[a + n] == data[b + n])
            n++;

        return n;
    }
}

/// <summary>
/// Real LZHAM compressor: a greedy/lazy LZ parse emitted through the adaptive arithmetic + Huffman
/// models the decoder expects, with per-block raw-block fallback.
///
/// The produced stream uses the same framing as <see cref="LzhamRawEncoder"/> (zlib-style 2-byte header,
/// block headers, EOF block, adler32 trailer), so it is a drop-in replacement - only the block payload
/// is genuinely compressed now.
/// </summary>
internal static class LzhamEncoder
{
    /// <summary>Reference default block size (LZHAM clamps it to dictionary_size / 8).</summary>
    private const int DefaultBlockSize = 1024 * 512;

    private const int MaxHugeMatchLen = 65536;

    private readonly struct Candidate
    {
        public readonly int Len;
        public readonly int Dist;
        public readonly int Rep;   // -1 = full (new) match, 0..3 = rep index
        public readonly int Score; // estimated bits saved versus literals

        public Candidate(int len, int dist, int rep, int score)
        {
            Len = len;
            Dist = dist;
            Rep = rep;
            Score = score;
        }
    }

    public static byte[] Compress(ReadOnlySpan<byte> data, int dictLog2, int tableUpdateRate)
    {
        if (dictLog2 < LzhamTables.MinDictSizeLog2 || dictLog2 > LzhamTables.MaxDictSizeLog2)
            throw new ArgumentOutOfRangeException(nameof(dictLog2), "LZHAM dictionary log2 must be in [15,29]");
        if (tableUpdateRate < 0 || tableUpdateRate > 20)
            throw new ArgumentOutOfRangeException(nameof(tableUpdateRate), "LZHAM table update rate must be in [0,20]");

        // Same zlib-style header the raw encoder writes (and which real game .ttx streams carry):
        // CM = 14 (LZHAM), CINFO = dict_size_log2 - 15, FDICT = 0.
        int cmf = 14 | ((dictLog2 - 15) << 4);
        int flg = 3 << 6;
        int check = ((cmf << 8) + flg) % 31;
        if (check != 0)
            flg += 31 - check;

        var writer = new LzhamBitWriter(Math.Max(64, (data.Length / 2) + 64));
        writer.WriteByteRaw((byte)cmf);
        writer.WriteByteRaw((byte)flg);

        var state = new LzhamEncoderState(dictLog2, tableUpdateRate);
        var matchFinder = new LzhamMatchFinder(data.Length, dictLog2);

        int blockSize = BlockSizeFor(dictLog2);
        int pos = 0;

        while (pos < data.Length)
        {
            int blockLen = Math.Min(blockSize, data.Length - pos);

            // Compress against a copy of the models; if that does not actually save space, the block
            // is stored with the untouched models, exactly like the reference encoder reverts state.
            LzhamEncoderState workState = state.Clone();
            var blockEncoder = new LzhamBlockEncoder(workState, Math.Min(blockLen, 1 << 16) + 64);

            EncodeCompressedBlock(blockEncoder, workState, matchFinder, data, pos, blockLen);
            byte[] compressed = blockEncoder.Finish();

            if (compressed.Length < blockLen)
            {
                writer.WriteBytesRaw(compressed);
                state = workState;
            }
            else
            {
                WriteRawBlock(writer, data.Slice(pos, blockLen));
            }

            pos += blockLen;
        }

        // EOF block: block type, byte align, then the big-endian adler32 of the uncompressed data.
        writer.PutBits(LzhamTables.EofBlock, LzhamTables.BlockHeaderBits);
        writer.AlignToByte();
        writer.PutBits(Adler32.Compute(data), 32);
        writer.FlushBits();

        return writer.ToArray();
    }

    private static int BlockSizeFor(int dictLog2)
    {
        int limit = (1 << dictLog2) / 8;
        return Math.Max(1024, Math.Min(DefaultBlockSize, limit));
    }

    private static void WriteRawBlock(LzhamBitWriter writer, ReadOnlySpan<byte> chunk)
    {
        uint lenMinusOne = (uint)(chunk.Length - 1);

        writer.PutBits(LzhamTables.RawBlock, LzhamTables.BlockHeaderBits);
        writer.PutBits(lenMinusOne, 24);
        writer.PutBits(((lenMinusOne & 0xFF) ^ ((lenMinusOne >> 8) & 0xFF) ^ ((lenMinusOne >> 16) & 0xFF)) & 0xFF, 8);

        writer.AlignToByte();
        writer.WriteBytesRaw(chunk);
    }

    private static void EncodeCompressedBlock(
        LzhamBlockEncoder enc,
        LzhamEncoderState st,
        LzhamMatchFinder matchFinder,
        ReadOnlySpan<byte> data,
        int start,
        int blockLen)
    {
        enc.WriteBits(LzhamTables.CompBlock, LzhamTables.BlockHeaderBits);
        enc.ArithInit();

        // Block flush type: 0 = no Huffman table resets, which matches the reference default.
        enc.WriteBits(0, LzhamTables.BlockFlushTypeBits);

        int p = start;
        int end = start + blockLen;

        // Per-block coding state (the reference calls this start_of_block()).
        int curState = 0;
        int h0 = 1, h1 = 1, h2 = 1, h3 = 1;

        while (p < end)
        {
            matchFinder.EnsureInserted(data, p);

            int maxLen = Math.Min(end - p, MaxHugeMatchLen);
            Candidate best = FindBestCandidate(data, matchFinder, p, maxLen, h0, h1, h2, h3);

            // One step of lazy matching: if the next position yields a clearly better match, emit a
            // literal here instead and let the next iteration take it.
            bool deferToNext = false;
            if (best.Len >= 3 && best.Len < 64 && p + 1 < end && maxLen > 1)
            {
                matchFinder.EnsureInserted(data, p + 1);
                int nextMax = Math.Min(end - (p + 1), MaxHugeMatchLen);
                Candidate next = FindBestCandidate(data, matchFinder, p + 1, nextMax, h0, h1, h2, h3);
                if (next.Len > best.Len + 1 || (next.Len == best.Len && next.Score > best.Score + 2))
                    deferToNext = true;
            }

            if (best.Len >= 2 && !deferToNext)
            {
                if (best.Rep >= 0)
                    EncodeRepMatch(enc, st, ref curState, ref h0, ref h1, ref h2, ref h3, best.Rep, best.Len);
                else
                    EncodeFullMatch(enc, st, ref curState, ref h0, ref h1, ref h2, ref h3, best.Dist, best.Len);

                p += best.Len;
                continue;
            }

            // No useful match: either a zero-distance (rep0, length 1) code or a plain literal.
            if (h0 > 0 && h0 <= p && data[p] == data[p - h0] && !deferToNext)
            {
                EncodeRepMatch(enc, st, ref curState, ref h0, ref h1, ref h2, ref h3, 0, 1);
            }
            else
            {
                EncodeLiteral(enc, st, data, p, ref curState, h0);
            }

            p++;
        }

        // End of block symbol: is_match = 1, is_rep = 0, main symbol 0.
        ref ushort isMatch = ref st.ArithProbs[LzhamEncoderState.ArithIsMatch + curState];
        enc.EncodeArithBit(1, ref isMatch);

        ref ushort isRep = ref st.ArithProbs[LzhamEncoderState.ArithIsRep + curState];
        enc.EncodeArithBit(0, ref isRep);

        enc.EncodeHuff(st.MainTable, (int)LzhamTables.LzxSpecialCodeEndOfBlockCode);
    }

    private static Candidate FindBestCandidate(
        ReadOnlySpan<byte> data,
        LzhamMatchFinder matchFinder,
        int p,
        int maxLen,
        int h0,
        int h1,
        int h2,
        int h3)
    {
        Candidate best = default;

        matchFinder.FindMatch(data, p, maxLen, out int fullDist, out int fullLen);
        if (fullLen >= LzhamTables.MinMatchLen)
        {
            int slot = ComputeSlot(fullDist);
            int cost = 18 + LzhamTables.LzxPositionExtraBits[slot];
            int fullScore = fullLen * MatchCost.BitsPerByte - cost;
            if (fullScore > 0)
                best = new Candidate(fullLen, fullDist, -1, fullScore);
        }

        Span<int> reps = stackalloc int[4];
        reps[0] = h0;
        reps[1] = h1;
        reps[2] = h2;
        reps[3] = h3;

        for (int i = 0; i < 4; i++)
        {
            int d = reps[i];
            if (d <= 0 || d > p)
                continue;

            int l = MatchLenAt(data, p, p - d, maxLen);
            if (l < LzhamTables.MinMatchLen)
                continue;

            int score = l * MatchCost.BitsPerByte - 8;
            if (score <= 0)
                continue;

            // Repeated distances are cheaper, so a rep wins ties, and also wins when it is only a
            // single byte shorter than the best fresh match.
            bool better;
            if (best.Len == 0)
                better = true;
            else if (l > best.Len)
                better = true;
            else if (l == best.Len)
                better = best.Rep < 0;
            else
                better = l + 1 == best.Len && best.Rep < 0;

            if (better)
                best = new Candidate(l, d, i, score);
        }

        return best;
    }

    private static class MatchCost
    {
        /// <summary>Rough cost of one literal byte, used to score candidate matches.</summary>
        public const int BitsPerByte = 9;
    }

    private static int MatchLenAt(ReadOnlySpan<byte> data, int a, int b, int maxLen)
    {
        if (maxLen <= 0)
            return 0;

        int n = 0;

        while (n + 8 <= maxLen)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(a + n))
                    ^ BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(b + n));
            if (x != 0)
                return n + (BitOperations.TrailingZeroCount(x) >> 3);
            n += 8;
        }

        while (n < maxLen && data[a + n] == data[b + n])
            n++;

        return n;
    }

    /// <summary>Maps a match distance to its LZX position slot and the corresponding extra bits value.</summary>
    private static int ComputeSlot(int dist)
    {
        if (dist < 4)
            return dist;

        int b = BitOperations.Log2((uint)dist);
        return (b << 1) + (int)(((uint)dist >> (b - 1)) & 1);
    }

    private static void EncodeLiteral(
        LzhamBlockEncoder enc,
        LzhamEncoderState st,
        ReadOnlySpan<byte> data,
        int p,
        ref int curState,
        int h0)
    {
        ref ushort isMatch = ref st.ArithProbs[LzhamEncoderState.ArithIsMatch + curState];
        enc.EncodeArithBit(0, ref isMatch);

        byte lit = data[p];

        if (curState < LzhamTables.NumLitStates)
        {
            enc.EncodeHuff(st.LitTable, lit);
        }
        else
        {
            // Context-dependent literal: XOR against the byte one rep0 distance back.
            byte predicted = data[p - h0];
            enc.EncodeHuff(st.DeltaLitTable, predicted ^ lit);
        }

        curState = LzhamTables.LiteralNextState[curState];
    }

    private static void EncodeRepMatch(
        LzhamBlockEncoder enc,
        LzhamEncoderState st,
        ref int curState,
        ref int h0,
        ref int h1,
        ref int h2,
        ref int h3,
        int repIndex,
        int len)
    {
        ref ushort isMatch = ref st.ArithProbs[LzhamEncoderState.ArithIsMatch + curState];
        enc.EncodeArithBit(1, ref isMatch);

        ref ushort isRep = ref st.ArithProbs[LzhamEncoderState.ArithIsRep + curState];
        enc.EncodeArithBit(1, ref isRep);

        ref ushort isRep0 = ref st.ArithProbs[LzhamEncoderState.ArithIsRep0 + curState];

        if (repIndex == 0)
        {
            enc.EncodeArithBit(1, ref isRep0);

            ref ushort isRep0SingleByte = ref st.ArithProbs[LzhamEncoderState.ArithIsRep0SingleByte + curState];

            if (len == 1)
            {
                enc.EncodeArithBit(1, ref isRep0SingleByte);
                curState = curState < LzhamTables.NumLitStates ? 9 : 11;
                return;
            }

            enc.EncodeArithBit(0, ref isRep0SingleByte);
            EncodeRepLen(enc, st, curState, len);
            curState = curState < LzhamTables.NumLitStates ? 8 : 11;
            return;
        }

        enc.EncodeArithBit(0, ref isRep0);
        EncodeRepLen(enc, st, curState, len);

        ref ushort isRep1 = ref st.ArithProbs[LzhamEncoderState.ArithIsRep1 + curState];
        enc.EncodeArithBit(repIndex == 1 ? 1 : 0, ref isRep1);

        if (repIndex == 1)
        {
            (h0, h1) = (h1, h0);
        }
        else
        {
            ref ushort isRep2 = ref st.ArithProbs[LzhamEncoderState.ArithIsRep2 + curState];
            enc.EncodeArithBit(repIndex == 2 ? 1 : 0, ref isRep2);

            if (repIndex == 2)
            {
                int dist = h2;
                h2 = h1;
                h1 = h0;
                h0 = dist;
            }
            else
            {
                int dist = h3;
                h3 = h2;
                h2 = h1;
                h1 = h0;
                h0 = dist;
            }
        }

        curState = curState < LzhamTables.NumLitStates ? 8 : 11;
    }

    private static void EncodeRepLen(LzhamBlockEncoder enc, LzhamEncoderState st, int curState, int len)
    {
        HuffmanModel table = curState >= LzhamTables.NumLitStates ? st.RepLenTable1 : st.RepLenTable0;

        if (len > LzhamTables.MaxMatchLen)
        {
            // The all-ones symbol (MaxMatchLen + 1 - MinMatchLen == 256) marks a huge match.
            enc.EncodeHuff(table, LzhamTables.MaxMatchLen + 1 - LzhamTables.MinMatchLen);
            EncodeHugeMatchLen(enc, len);
        }
        else
        {
            enc.EncodeHuff(table, len - LzhamTables.MinMatchLen);
        }
    }

    private static void EncodeFullMatch(
        LzhamBlockEncoder enc,
        LzhamEncoderState st,
        ref int curState,
        ref int h0,
        ref int h1,
        ref int h2,
        ref int h3,
        int dist,
        int len)
    {
        ref ushort isMatch = ref st.ArithProbs[LzhamEncoderState.ArithIsMatch + curState];
        enc.EncodeArithBit(1, ref isMatch);

        ref ushort isRep = ref st.ArithProbs[LzhamEncoderState.ArithIsRep + curState];
        enc.EncodeArithBit(0, ref isRep);

        int slot = ComputeSlot(dist);
        uint extra = (uint)(dist - LzhamTables.LzxPositionBase[slot]);

        int lowSym = len >= 9 ? 7 : len - 2;
        int highSym = slot - LzhamTables.LzxLowestUsableMatchSlot;
        int mainSym = lowSym | (highSym << 3);

        enc.EncodeHuff(st.MainTable, LzhamTables.LzxNumSpecialLengths + mainSym);

        if (len >= 9)
        {
            HuffmanModel largeLenTable = curState >= LzhamTables.NumLitStates ? st.LargeLenTable1 : st.LargeLenTable0;

            if (len > LzhamTables.MaxMatchLen)
            {
                // (MaxMatchLen + 1) - 9 == 249: the secondary-length marker for a huge match.
                enc.EncodeHuff(largeLenTable, LzhamTables.MaxMatchLen + 1 - 9);
                EncodeHugeMatchLen(enc, len);
            }
            else
            {
                enc.EncodeHuff(largeLenTable, len - 9);
            }
        }

        int numExtraBits = LzhamTables.LzxPositionExtraBits[slot];
        if (numExtraBits < 3)
        {
            enc.WriteBits(extra, numExtraBits);
        }
        else
        {
            if (numExtraBits > 4)
                enc.WriteBits(extra >> 4, numExtraBits - 4);

            enc.EncodeHuff(st.DistLsbTable, (int)(extra & 15));
        }

        // Full matches always push their distance onto the rep history.
        h3 = h2;
        h2 = h1;
        h1 = h0;
        h0 = dist;

        curState = curState < LzhamTables.NumLitStates ? 7 : 10;
    }

    /// <summary>
    /// Encodes the length of a match longer than <see cref="LzhamTables.MaxMatchLen"/> (258..65536),
    /// mirroring the decoder's unary-prefix + tail-bit layout (HugeMatchBaseLen / HugeMatchCodeLen).
    /// </summary>
    private static void EncodeHugeMatchLen(LzhamBlockEncoder enc, int len)
    {
        int v = len - (int)LzhamTables.HugeMatchBaseLen[0];

        if (v < 256)
        {
            enc.WriteBits(0, 1);
            enc.WriteBits((uint)v, 8);
        }
        else if (v < 256 + 1024)
        {
            enc.WriteBits(0b10, 2);
            enc.WriteBits((uint)(v - 256), 10);
        }
        else if (v < 256 + 1024 + 4096)
        {
            enc.WriteBits(0b110, 3);
            enc.WriteBits((uint)(v - 1280), 12);
        }
        else
        {
            enc.WriteBits(0b111, 3);
            enc.WriteBits((uint)(v - 5376), 16);
        }
    }
}
