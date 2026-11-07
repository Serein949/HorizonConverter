import sys
sys.path.insert(0, "tmp")
import texture2ddecoder as t
from astc_proto import make_block, weight_levels, rev_b, set_bits, WRANGE, MODE_FOR_RANGE
bw=bh=4; gw=gh=4
ev=[255,0,0, 0,0,255]
for qval in (0,1,2,3):
    q=[qval]*16
    blk=make_block(gw,gh,2,8,ev,q)
    out=t.decode_astc(blk,bw,bh,bw,bh)
    print("qall",qval,"blk",blk.hex(),"px0",tuple(out[0:4]),"px1",tuple(out[4:8]))
# try storing direct (no reversal)
import astc_proto as ap
def make_block_direct(gw,gh,nbits,cem,ev,q):
    case,dims,b1bit0=ap.grid_fields(gw,gh)
    mode,b0bit4,b1bit1=ap.MODE_FOR_RANGE[ap.WRANGE[nbits]]
    b0=mode|(case<<2)|dims|(b0bit4<<4)
    b1=b1bit0|(b1bit1<<1)|((cem&7)<<5)
    b2=(cem>>3)&1
    buf=bytearray(16)
    ap.set_bits(buf,0,b0,8); ap.set_bits(buf,8,b1,8); ap.set_bits(buf,16,b2,1)
    for i,v in enumerate(ev): ap.set_bits(buf,17+8*i,v,8)
    for i,v in enumerate(q): ap.set_bits(buf,128-(i+1)*nbits,v,nbits)
    return bytes(buf)
for qval in (0,1,2,3):
    q=[qval]*16
    blk=make_block_direct(gw,gh,2,8,ev,q)
    out=t.decode_astc(blk,bw,bh,bw,bh)
    print("direct qall",qval,"px0",tuple(out[0:4]))
# void extent
vb=bytearray(16); vb[0]=0xfc; vb[1]=1
vb[9]=10; vb[11]=20; vb[13]=30; vb[15]=40
out=t.decode_astc(bytes(vb),4,4,4,4)
print("void", tuple(out[0:4]), tuple(out[4:8]))
