import texture2ddecoder as t
# BC1 red block: color0=0xF800 (red), color1=0, all indices 0
blk=bytes([0x00,0xF8,0x00,0x00,0,0,0,0])
out=t.decode_bc1(blk,4,4)
print("bc1", tuple(out[0:4]))
# ASTC void extent with pure red: b9=255, others 0
vb=bytearray(16); vb[0]=0xfc; vb[1]=1; vb[9]=255
print("void red", tuple(t.decode_astc(bytes(vb),4,4,4,4)[0:4]))
# ASTC CEM8 block red (q=0)
import sys; sys.path.insert(0,"tmp")
from astc_proto import make_block
blk=make_block(4,4,2,8,[255,0,0,0,0,0],[0]*16)
print("astc red", tuple(t.decode_astc(blk,4,4,4,4)[0:4]))
