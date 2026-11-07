import sys; sys.path.insert(0,"tmp")
import texture2ddecoder as t
from astc_proto import make_block
ev=[255,0,0, 0,255,0]
for qv in (0,1,2,3):
    blk=make_block(4,4,2,8,ev,[qv]*16)
    out=t.decode_astc(blk,4,4,4,4)
    print("q",qv,"px",tuple(out[0:4]))
# also dump the 6 ev bytes as written for q=0
print("hex q0",make_block(4,4,2,8,ev,[0]*16).hex())
