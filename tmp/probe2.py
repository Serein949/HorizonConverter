import sys, json, hashlib, struct
sys.path.insert(0, "tools")
import texture2ddecoder as t
# Minimal TTX parse in Python (plain fixtures are compressed? use manifest + check magic)
path = r"C:\Users\17353\Documents\deepseek-harness\default-workspace\tests\vectors\astc\a350_1000__cabin__attendantseat00_color.ttx"
raw = open(path,"rb").read()
print("len",len(raw),"head",raw[:16].hex())
