import sys; sys.path.insert(0,"tmp")
import texture2ddecoder as t
from astc_proto import make_block
ev1=[255,0,0,0,0,255]
ev2=[255,0,0,0,0,0]
for ev,name in ((ev1,"blue-endpoint"),(ev2,"black-endpoint")):
    for qv in (0,3):
        blk=make_block(4,4,2,8,ev,[qv]*16)
        out=t.decode_astc(blk,4,4,4,4)
        print(name,"q",qv,"hex",blk.hex(),"px0",tuple(out[0:4]))
vb=bytearray(16); vb[0]=0xfc; vb[1]=1
vb[9]=10; vb[11]=20; vb[13]=30; vb[15]=40
print("void",tuple(t.decode_astc(bytes(vb),4,4,4,4)[0:4]))
