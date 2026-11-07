import sys; sys.path.insert(0,"tmp")
import texture2ddecoder as t
from astc_proto import make_block
for name,ev in (("red-black",[255,0,0, 0,0,0]), ("red-blue",[255,0,0, 0,0,255])):
  for qv in (0,1,2,3):
    blk=make_block(4,4,2,8,ev,[qv]*16)
    out=t.decode_astc(blk,4,4,4,4)
    print(name,"q",qv,[tuple(out[(y*4+x)*4:(y*4+x)*4+4]) for y in range(2) for x in range(2)])
