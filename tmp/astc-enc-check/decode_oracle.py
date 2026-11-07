import sys
import texture2ddecoder as t

payload = open(sys.argv[1], "rb").read()
width, height, bw, bh = (int(v) for v in sys.argv[2:6])
out = t.decode_astc(payload, width, height, bw, bh)
open(sys.argv[6], "wb").write(out)