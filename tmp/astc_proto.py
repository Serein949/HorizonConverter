"""Prototype ASTC LDR encoder bit layout, validated against texture2ddecoder."""
import texture2ddecoder

BITREV = bytes(int(f"{i:08b}"[::-1], 2) for i in range(256))


def rev_b(x, b):
    return BITREV[x] >> (8 - b)


def set_bits(buf, pos, val, n):
    for i in range(n):
        bit = (val >> i) & 1
        if bit:
            buf[(pos + i) >> 3] |= 1 << ((pos + i) & 7)
        else:
            buf[(pos + i) >> 3] &= ~(1 << ((pos + i) & 7))


# weight range -> (precA, precB)
WPREC = [(0,0),(0,0),(0,1),(3,0),(0,2),(5,0),(3,1),(0,3),
         (0,0),(0,0),(5,1),(3,2),(0,4),(5,2),(3,3),(0,5)]
WRANGE = {1: 2, 2: 4, 3: 7, 4: 12, 5: 15}


def grid_fields(gw, gh):
    """Return (case_bits, b0_dims, b1_bit0) i.e. b0 bits2..7 and b1 bit0."""
    if 4 <= gw <= 7 and 2 <= gh <= 5:
        gx = gw - 4
        gy = gh - 2
        return 0, (gy << 5) | ((gx & 1) << 7), (gx >> 1) & 1
    if 8 <= gw <= 11 and 2 <= gh <= 5:
        gx = gw - 8
        gy = gh - 2
        return 4, (gy << 5) | ((gx & 1) << 7), (gx >> 1) & 1
    if 2 <= gw <= 5 and 8 <= gh <= 11:
        gx = gw - 2
        gy = gh - 8
        return 8, (gx << 5) | ((gy & 1) << 7), (gy >> 1) & 1
    if 2 <= gw <= 3 and 2 <= gh <= 5:
        return 12, ((gh - 2) << 5) | ((gw - 2) << 7), 1
    if 2 <= gw <= 5 and 6 <= gh <= 7:
        return 12, ((gw - 2) << 5) | ((gh - 6) << 7), 0
    raise ValueError(f"grid {gw}x{gh} not encodable")


MODE_FOR_RANGE = {2: (1, 0, 0), 4: (2, 0, 0), 7: (3, 1, 0), 12: (2, 0, 1), 15: (3, 1, 1)}
# range -> (block mode, b0 bit4, b1 bit1)


def make_block(gw, gh, nbits, cem, ev, q):
    case, dims, b1bit0 = grid_fields(gw, gh)
    mode, b0bit4, b1bit1 = MODE_FOR_RANGE[WRANGE[nbits]]
    b0 = mode | (case << 2) | dims | (b0bit4 << 4)
    b1 = b1bit0 | (b1bit1 << 1)
    b2 = (cem >> 3) & 1
    b1 |= (cem & 7) << 5
    buf = bytearray(16)
    set_bits(buf, 0, b0, 8)
    set_bits(buf, 8, b1, 8)
    set_bits(buf, 16, b2, 1)
    for i, v in enumerate(ev):
        set_bits(buf, 17 + 8 * i, v, 8)
    for i, v in enumerate(q):
        set_bits(buf, 128 - (i + 1) * nbits, rev_b(v, nbits), nbits)
    return bytes(buf)


def weight_levels(nbits):
    vals = []
    for b in range(1 << nbits):
        if nbits == 1:
            wv = 63 if b else 0
        elif nbits == 2:
            wv = (b << 4) | (b << 2) | b
        elif nbits == 3:
            wv = (b << 3) | b
        elif nbits == 4:
            wv = (b << 2) | (b >> 2)
        else:
            wv = (b << 1) | (b >> 4)
        if wv > 32:
            wv += 1
        vals.append(wv)
    return vals


if __name__ == "__main__":
    bw = bh = 4
    gw = gh = 4
    for nbits in (2, 4):
        ev = [255, 0, 0, 0, 0, 255]  # CEM8: R0,G0,B0,R1,G1,B1
        q = [(x + y * gw) % (1 << nbits) for y in range(gh) for x in range(gw)]
        blk = make_block(gw, gh, nbits, 8, ev, q)
        out = texture2ddecoder.decode_astc(blk, bw, bh, bw, bh)
        print(f"nbits={nbits} levels={weight_levels(nbits)}")
        print("  block", blk.hex())
        for y in range(bh):
            print("  ", [tuple(out[(y * bw + x) * 4:(y * bw + x) * 4 + 3]) for x in range(bw)])
