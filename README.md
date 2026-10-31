# AeroflyConv â€” å¿«å¾—å¤šçš„ Aerofly FS è½¬æ¢å™¨ï¼ˆC# / .NET 10ï¼‰

[`RishiK-06/AeroflyFS-Native-Converter`](https://github.com/RishiK-06/AeroflyFS-Native-Converter) çš„ C# é‡å†™ç‰ˆï¼š
åŠŸèƒ½ä¸€ä¸€å¯¹ç…§ï¼Œä½†æ•´æ¡å¤„ç†ç®¡çº¿éƒ½æ˜¯åŽŸç”Ÿä»£ç  â€”â€” çº¯æ‰˜ç®¡ C# çš„ LZHAM ç¼–è§£ç å™¨ã€SIMD çš„
BC1/BC3/ETC2 çº¹ç†ç¼–è§£ç ã€è‡ªå†™çš„é«˜åžå PNG ç¼–ç å™¨ã€å¤šæ–‡ä»¶å¹¶è¡Œæ‰¹å¤„ç†ã€‚

![GUI](docs/screenshot.png)

## ä¸ºä»€ä¹ˆå¿«

| çŽ¯èŠ‚ | åŽŸç‰ˆï¼ˆPython + PySide6ï¼‰ | æœ¬ç‰ˆï¼ˆC# / .NET 10ï¼‰ |
|---|---|---|
| LZHAM | `wasmtime` è·‘ `tmcompress.wasm`ï¼ŒåŽ‹ç¼©é  `pylzham` | çº¯ C# ç§»æ¤çš„è§£ç å™¨ + raw-block ç¼–ç å™¨ï¼ˆæ— åŽŸç”Ÿä¾èµ–ï¼‰ |
| çº¹ç†è§£ç  | `texture2ddecoder` + é€åƒç´  Python å¾ªçŽ¯è¡¥ RGBA | æ‰˜ç®¡ SIMD BC1/BC2/BC3/BC4/BC5/ETC1/ETC2 |
| çº¹ç†ç¼–ç  | é€å—çº¯ Python BC1/BC3 | SIMD å—ç¼–ç å™¨ï¼ˆè´¨é‡é«˜äºŽåŽŸç‰ˆæœ´ç´ å®žçŽ°ï¼šBC1 40.9 dB vs 38.0 dBï¼‰ |
| PNG å†™å‡º | Pillowï¼ˆGeneric ç®¡çº¿ï¼‰ | è‡ªå†™ PNG ç¼–ç å™¨ + é€è¡Œè‡ªé€‚åº”æ»¤æ³¢ + .NET zlibï¼Œå¿« 4â€“5 å€ |
| æ‰¹å¤„ç† | å•çº¿ç¨‹ | `Parallel.ForEach`ï¼Œé»˜è®¤ = CPU çº¿ç¨‹æ•° |
| è¿è¡ŒçŽ¯å¢ƒ | Python 3 + 6 ä¸ªåŽŸç”Ÿä¾èµ– | å•ä¸ªè‡ªåŒ…å« exeï¼Œç›®æ ‡æœºå™¨æ— éœ€è£…è¿è¡Œæ—¶ |

### å®žæµ‹ï¼ˆåŒä¸€æ‰¹ 24 ä¸ªçœŸå®žæ¸¸æˆ `.ttx`ï¼Œå…± 66.4 MBï¼Œ4096Â² / 2048Â² æ··åˆï¼‰

| åœºæ™¯ | Python åŽŸç‰ˆ | C# å•çº¿ç¨‹ | C# 8 çº¿ç¨‹ |
|---|---|---|---|
| TTX â†’ PNGï¼ˆ24 ä¸ªæ–‡ä»¶ï¼‰ | **183.2 s** (0.13 æ–‡ä»¶/s) | **14.6 s**ï¼ˆ12.5Ã—ï¼‰ | **4.7 s**ï¼ˆ39Ã—ï¼‰ |
| å•ä¸ª 4096Ã—4096 DXT1 TTX â†’ PNG | 12â€“16 s | â‰ˆ 1.0 s | â€” |
| PNG â†’ TTXï¼ˆ1024Â² DXT1ï¼Œå« mip é“¾ + LZHAMï¼‰ | 11.1 s | 0.37 sï¼ˆ30Ã—ï¼‰ | â€” |

LZHAM æœ¬ä½“ï¼šè§£ç  **162 MB/s**ã€ç¼–ç  **728 MB/s**ï¼ˆä¸Žå®˜æ–¹ C++ ç‰ˆæœ¬äº§å‡ºçš„å­—èŠ‚æµé€å­—èŠ‚ä¸€è‡´ï¼‰ã€‚

---

## 1. æ€Žä¹ˆè·‘

### å…å®‰è£…ï¼ˆæŽ¨èï¼‰

```
dist\gui\AeroflyConv.exe          # å›¾å½¢ç•Œé¢
dist\cli\AeroflyConv.exe --help   # å‘½ä»¤è¡Œ
```

è‡ªåŒ…å«å•æ–‡ä»¶ï¼ˆGUI â‰ˆ 50 MB / CLI â‰ˆ 37 MBï¼‰ï¼Œç›®æ ‡æœºå™¨ä¸éœ€è¦ .NETã€‚

### è‡ªå·±ç¼–è¯‘

```powershell
cd AeroflyConv
.\build.ps1                       # è¾“å‡º .\dist\cli ä¸Ž .\dist\guiï¼ˆwin-x64 è‡ªåŒ…å«å•æ–‡ä»¶ï¼‰
# è‹¥è¢« PowerShell æ‰§è¡Œç­–ç•¥æ‹¦ä½ï¼š
powershell -ExecutionPolicy Bypass -File .\build.ps1

dotnet run --project src/AeroflyConv.Gui
dotnet run --project src/AeroflyConv.Cli -- "D:\liveries" -m png2ttx --flip
```

è¦æ±‚ .NET 10 SDKã€‚

---

## 2. å‘½ä»¤è¡Œ

```powershell
# æŒ‰æ‰©å±•åè‡ªåŠ¨è¯†åˆ«ï¼ˆåŒåŽŸç‰ˆï¼‰
AeroflyConv "C:\Aerofly FS 4\aircraft" -j 8

# çº¹ç†
AeroflyConv livery.ttx                        # -> livery.png
AeroflyConv livery.png -m png2ttx --flip      # -> livery.ttxï¼ˆdxt_autoï¼šæœ‰é€æ˜Žç”¨ DXT5ï¼Œå¦åˆ™ DXT1ï¼‰
AeroflyConv livery.png -m png2ttx -f dxt1     # dxt1 / dxt5 / type_rgba / type_r / etc2 / type_rgba_etc2

# åœºæ™¯è´´å›¾
AeroflyConv tile.ttc                          # -> tile.pngï¼ˆé»˜è®¤ä¸Šä¸‹ç¿»è½¬ï¼‰
AeroflyConv tile.png -m png2ttc --ttc-format dxt1

# å£°éŸ³
AeroflyConv engine.tsb                        # -> engine.wav
AeroflyConv engine.wav                        # -> engine.tsb
AeroflyConv song.flac                         # -> song.wavï¼ˆä¹Ÿæ”¯æŒ .mp3 / .oggï¼‰

# TM å®¹å™¨ -> é€šç”¨æ–‡æœ¬æ ‘
AeroflyConv airport.tmb                       # -> airport.tmb.txt

# åªçœ‹ä¿¡æ¯
AeroflyConv some.ttx --info
```

å‚æ•°ï¼š`-m/--mode`ã€`-o/--output`ã€`-f/--format`ã€`--ttc-format`ã€`--flip/--no-flip`ã€
`--no-mipmaps`ã€`--no-compress`ã€`--png-quality fast|balanced|best`ã€`--zoom N`ã€
`--no-overwrite`ã€`-j/--jobs N`ã€`--info`ã€‚

---

## 3. GUI

ç•Œé¢é£Žæ ¼å‚ç…§åŒæœºçš„ `AirportStudioNative`ï¼ˆDirect2D çŽ»ç’ƒæ‹Ÿæ€ï¼‰ï¼šè¿‘é»‘è—é’åº•è‰²ã€ä¸¤é“å†·ç™½è‰²
å¾„å‘å…‰æ™•ã€1px ç™½è‰² 10% æè¾¹çš„åŠé€æ˜Žå¡ç‰‡ã€é’è‰² `rgb(0,184,255)` å¼ºè°ƒè‰²ä¸Žç»¿è‰²æˆåŠŸè‰²ï¼Œ
å·¦ä¾§åˆ†ç±»ä¾§è¾¹æ  + é€‰ä¸­é¡¹é’è‰²å…‰æ¡ã€‚å…¨éƒ¨æŽ§ä»¶è‡ªç»˜ï¼ˆ`GlassCard` / `GlassButton` / `GlassSelect` /
`GlowProgressBar` / `NavList`ï¼‰ï¼Œå¹¶å¯ç”¨ WinForms åŽŸç”Ÿæ·±è‰²æ¨¡å¼ + Windows 11 æ·±è‰²æ ‡é¢˜æ /åœ†è§’ã€‚

åŠŸèƒ½ï¼šæ‹–æ”¾æ–‡ä»¶æˆ–æ–‡ä»¶å¤¹ã€æŒ‰ç±»åˆ«åˆ†ç»„çš„æ¨¡å¼åˆ—è¡¨ã€æ ¼å¼ä¸‹æ‹‰æ¡†ã€çº¿ç¨‹æ•°ã€ç¿»è½¬/å¤šçº§ mip/åŽ‹ç¼©å¼€å…³ã€
è¿›åº¦æ¡ã€å®žæ—¶æ—¥å¿—ã€"æ‰“å¼€è¾“å‡ºç›®å½•"ã€‚

---

## 4. å®žçŽ°è¯´æ˜Ž

* **LZHAM**ï¼ˆ`src/AeroflyConv.Core/Lzham`ï¼‰ï¼šä»Žå®˜æ–¹ `lzham_codec` C++ æºç ç§»æ¤çš„è‡ªé€‚åº”ç®—æœ¯
  ç¼–ç è§£ç å™¨ï¼ˆsync/raw/comp/EOF å››ç§ blockã€å…¨éƒ¨ç¬¦å·æ¨¡åž‹ã€Huffman å‰ç¼€è¡¨ï¼‰ã€‚ç¼–ç æ–¹å‘åªå†™
  raw blockï¼Œä½†å—å¤´ã€å¯¹é½ä¸Žç»“å°¾ adler32 å®Œå…¨æŒ‰è§„èŒƒç¼–ç ã€‚
* **çº¹ç†ç¼–è§£ç **ï¼ˆ`Codecs/`ï¼‰ï¼šBC1/BC2/BC3/BC4/BC5 ä¸Ž ETC1/ETC2/EAC è§£ç ã€BC1/BC3 ä¸Ž
  ETC2 RGB/RGBA ç¼–ç ï¼Œå…¨éƒ¨ SIMD æ‰‹å†™ï¼›è§£ç ä¸Ž `texture2ddecoder` é€å­—èŠ‚ä¸€è‡´ã€‚
* **å®¹å™¨**ï¼ˆ`Tm/`ã€`Textures/`ï¼‰ï¼šTTX/TTC/TSB/TM çš„ chunk æ ‘ã€mip é“¾ã€`compress_file`
  åŒ…è£…ã€CRC32ã€tmcompress å¤´ï¼ŒæŒ‰åŽŸç‰ˆè¯­ä¹‰é‡å†™ï¼ˆå¹¶å…¼å®¹ IPACS çš„ `string8u` å˜ä½“ï¼‰ã€‚
* **PNG**ï¼šè¯»ç”¨ ImageSharpï¼Œå†™ç”¨è‡ªå†™ç¼–ç å™¨ï¼ˆRGB/RGBAã€é€è¡Œ MSAD è‡ªé€‚åº”æ»¤æ³¢ï¼‰ã€‚
* **éŸ³é¢‘**ï¼šNLayerï¼ˆMP3ï¼‰ã€NVorbisï¼ˆOGGï¼‰ã€FlacBoxï¼ˆFLACï¼‰ï¼Œå…¨éƒ¨çº¯æ‰˜ç®¡ã€‚
* **å¹¶è¡Œ**ï¼šæ–‡ä»¶çº§å¹¶è¡Œï¼›å•æ–‡ä»¶å†…éƒ¨æŒ‰é¡ºåºå¤„ç†ã€‚

---

## 5. éªŒè¯

`tests/vectors` é‡Œæ˜¯ä»Ž**çœŸå®žæ¸¸æˆæ–‡ä»¶**ç”Ÿæˆå¹¶ä¸ŽåŽŸç‰ˆ Python å·¥å…·å¯¹ç…§è¿‡çš„å‚è€ƒæ•°æ®ï¼š

* 24 ä¸ªçœŸå®žå®¹å™¨ï¼ˆ.ttx / .toc / .tmb / .tsc / .tsl / .wad / .tsbï¼‰çš„ LZHAM è§£åŽ‹ç»“æžœå“ˆå¸Œ
* çº¹ç†è§£ç åŽçš„ RGBA å“ˆå¸Œï¼ˆå« DXT1/DXT5/BC5/ETC2ï¼‰
* TMâ†’æ–‡æœ¬æ ‘çš„å®Œæ•´æ–‡æœ¬å“ˆå¸Œï¼ˆå« 55 MB çš„å·¨å¤§ .tmbï¼‰
* TSBâ†’WAV å­—èŠ‚å“ˆå¸Œ
* åŽŸç‰ˆå®žçŽ°ï¼ˆçœŸ `pylzham` åŽ‹ç¼©æµï¼‰ç¼–ç å‡ºçš„ TTXï¼Œç”¨äºŽéªŒè¯ C# è§£ç å™¨èƒ½åƒ"çœŸåŽ‹ç¼©å—"
* C# ç¼–ç å‡ºçš„ TTX å·²ç”¨åŽŸç‰ˆè§£ç å™¨ + å®˜æ–¹ LZHAM wasm åå‘éªŒè¯å¯è¯»

```powershell
dotnet run --project tools/Verify       # 63 é¡¹ç«¯åˆ°ç«¯å¯¹ç…§ï¼ˆå½“å‰å…¨éƒ¨é€šè¿‡ï¼‰
dotnet run --project tools/LzhamCheck   # LZHAM 24 ä¸ªè§£ç å‘é‡ + ç¼–ç å›žçŽ¯ + åžå
dotnet run --project tools/CodecCheck   # å—åŽ‹ç¼©ç¼–è§£ç  vs texture2ddecoder / etcpak
```

---

## 6. å·²çŸ¥é™åˆ¶ï¼ˆä¸ŽåŽŸç‰ˆçš„å·®å¼‚ï¼‰

* **ASTC**ï¼ˆ`type_rgb_astc_*` / `type_rgba_astc_*`ï¼Œä»… Aerofly ç§»åŠ¨ç‰ˆç”¨ï¼‰ä¸è§£ç ï¼Œä¼šç»™å‡ºæ˜Žç¡®æŠ¥é”™ã€‚
* æ¸¸æˆè‡ªå¸¦çš„ `scenery/images/map_*.ttc` åˆ‡ç‰‡ï¼ˆå¤´éƒ¨æ ¼å¼æ ‡è®° `0x1234567x`ï¼‰æ˜¯æœªå…¬å¼€çš„å¦ä¸€ç§
  å˜ä½“ï¼ŒåŽŸç‰ˆ Python å·¥å…·åŒæ ·æ— æ³•è¯»å–ï¼›æœ¬ç‰ˆç»™å‡ºæ˜Žç¡®æç¤ºã€‚è½¬æ¢å™¨è‡ªå·±ç”Ÿæˆçš„ TTC æ­£å¸¸å·¥ä½œã€‚
* **BC5 æ³•çº¿è´´å›¾**ï¼šæœ¬ç‰ˆæŒ‰æ³•çº¿è´´å›¾è§„èŒƒé‡å»º Z é€šé“ï¼ˆæ­£ç¡®ï¼‰ï¼›åŽŸç‰ˆæ€»æ˜¯ç”¨é›¶é€šé“æŽ¨å¯¼ Zï¼Œ
  æ‰€ä»¥ä¸¤è€…è“è‰²é€šé“ä¸åŒï¼ˆçº¢/ç»¿é€šé“é€å­—èŠ‚ä¸€è‡´ï¼‰ã€‚è¿™æ˜¯æœ‰æ„ä¿®æ­£ã€‚
* **TTX åŽ‹ç¼©å†™æ³•**ï¼šç”¨ LZHAM *raw block*ï¼ˆåˆæ³•ã€æ¸¸æˆå¯è¯»ã€å·²ç”¨å®˜æ–¹è§£ç å™¨éªŒè¯ï¼‰ï¼Œä½†æ²¡æœ‰ LZ
  åŒ¹é…ï¼Œæ–‡ä»¶ä½“ç§¯æŽ¥è¿‘æœªåŽ‹ç¼©å¤§å°ã€‚éœ€è¦åŽŸç‰ˆé‚£ç§å°ä½“ç§¯æ—¶ç”¨ `--no-compress` å†™éžåŽ‹ç¼©å®¹å™¨ï¼Œ
  æˆ–ä»ç”¨åŽŸç‰ˆ Python çš„ `pylzham` åŽ‹ç¼©ã€‚
* è¾“å…¥å›¾ç‰‡æ”¯æŒ PNG/JPG/BMP/GIF/TGA/WebPï¼ˆImageSharpï¼‰ã€‚

---

## 7. ç›®å½•ç»“æž„

```
src/AeroflyConv.Core/      æ ¼å¼å®žçŽ°ï¼šTm/ Textures/ Codecs/ Lzham/ Media/ Conversion/
src/AeroflyConv.Cli/       å‘½ä»¤è¡Œå…¥å£ï¼ˆè¾“å‡º AeroflyConv.exeï¼‰
src/AeroflyConv.Gui/       WinForms ç•Œé¢ï¼ˆUi/ è‡ªç»˜æŽ§ä»¶ï¼‰
tools/LzhamCheck/          LZHAM è‡ªæ£€
tools/CodecCheck/          å—åŽ‹ç¼©è‡ªæ£€
tools/Verify/              ç«¯åˆ°ç«¯å¯¹ç…§éªŒè¯
tests/vectors/             å‚è€ƒæ•°æ®ï¼ˆçº¦ 14 MBï¼‰
refharness/                ç”Ÿæˆå‚è€ƒæ•°æ®çš„ Python è„šæœ¬ï¼ˆéœ€è¦åŽŸç‰ˆä»“åº“ï¼‰
docs/screenshot.png        GUI æˆªå›¾
```

---

## 8. ³£¼ûÎÊÌâ£ºÔËÐÐÒ»»á¶ùºóÉÁÍË£¨NahimicOSD.dll£©

Windows ÊÂ¼þÈÕÖ¾ÀïÈç¹û¿´µ½£º

```
Faulting application name: AeroflyConv.exe
Faulting module name: NahimicOSD.dll, version: 2.2.25.0
Exception code: 0xc0000005
```

Õâ²»ÊÇ±¾³ÌÐòµÄÎÊÌâ£ºDell / Alienware »úÆ÷ÉÏµÄ **Alienware Sound Center£¨Nahimic£©** »á°Ñ
`NahimicOSD.dll` ×¢Èëµ½Ã¿¸ö½ø³Ì£¬¶øÕâ¸öÄ£¿éÓÐÒÑÖªµÄ±ÀÀ£È±ÏÝ£¨Ô­Éú 0xC0000005£©¡£

´¦Àí°ì·¨£¨ÈÎÑ¡ÆäÒ»£©£º

1. Ë«»÷³ÌÐòÄ¿Â¼ÀïµÄ `NahimicOSD-Í£Ö¹.cmd`£¨»áÇëÇó¹ÜÀíÔ±È¨ÏÞ£©£¬ÔÙÆô¶¯ AeroflyConv£»
   ÐèÒª»¹Ô­Ê±ÔËÐÐ `NahimicOSD-»Ö¸´.cmd`¡£
2. `services.msc` ¡ú ÕÒµ½ `NahimicService` ¡ú Í£Ö¹£¬²¢°ÑÆô¶¯ÀàÐÍ¸Ä³É¡¸½ûÓÃ¡¹£¨ÓÀ¾ÃÉúÐ§£©¡£
3. ÔÚ Alienware Sound Center / Nahimic Àï¹Ø±Õ OSD / ÉùÒô×·×ÙÀàµÄµþ²ã¹¦ÄÜ¡£
4. Ö»ÓÃÃüÁîÐÐ°æ£¨`dist\cli\AeroflyConv.exe`£©Åú´¦Àí£¬Ëü²»¿ª´°¿Ú£¬¸ü²»ÈÝÒ×±» OSD Åöµ½¡£

³ÌÐòÆô¶¯Ê±Èç¹û¼ì²âµ½¸Ã DLL ÒÑ×¢Èë£¬»áÔÚÈÕÖ¾Àï¸ø³öºìÉ«¾¯¸æ²¢µ¯´°ÌáÊ¾¡£

---

## 9. ³£¼ûÎÊÌâ£º`Access to the path ... is denied`

×ª»»½á¹ûÄ¬ÈÏÐ´ÔÚÔ´ÎÄ¼þÅÔ±ß¡£ÓÎÏ·°²×°Ä¿Â¼£¨`...\SteamLibrary\steamapps\common\Aerofly FS 4 ...`£©
Ò»°ã²»ÔÊÐíÆÕÍ¨ÓÃ»§Ð´Èë£¬ËùÒÔ»á±¨È¨ÏÞ´íÎó¡£ÈýÖÖ½â¾ö°ì·¨£º

1. **GUI**£º`Options ¡ú Output` Ñ¡Ò»¸ö¿ÉÐ´Ä¿Â¼£¨¿ÉÐ´Ä¿Â¼ÀïµÄÎÄ¼þ¼Ð½á¹¹»á°´ÊäÈë¾µÏñ£©£¬
   ÓÒ²à `?` »Ö¸´"Ð´ÔÚÔ´ÎÄ¼þÅÔ±ß"¡£
2. **CLI**£º`-d/--outdir`

   ```powershell
   AeroflyConv "E:\SteamLibrary\steamapps\common\Aerofly FS 4 Flight Simulator\aircraft\a350_1000" -d D:\AeroflyPNG -j 16
   ```

3. **Ê²Ã´¶¼²»×ö**£º³ÌÐò»á×Ô¶¯¸ÄÐ´µ½ `Documents\AeroflyConv-out`£¨ÈôÒ²²»¿ÉÐ´ÔòÒÀ´Î³¢ÊÔ
   `%LOCALAPPDATA%\AeroflyConv\out`¡¢ÁÙÊ±Ä¿Â¼¡¢exe Í¬Ä¿Â¼ `out\`£©£¬ÈÕÖ¾Àï»á×¢Ã÷Êµ¼ÊÎ»ÖÃ¡£

ÁíÍâ "auto" Ä£Ê½ÔÚÉ¨ÃèÎÄ¼þ¼ÐÊ±Ö»´¦ÀíÒÑÖªµÄÊäÈëÀ©Õ¹Ãû£¨`.ttx .tsb .wav .mp3 .flac .ogg .ttc
.toc .tsc .wad .tmb .tsl .zip .tme` ºÍÍ¼Æ¬£©£¬²»»áÔÙ¶Ô `.json`¡¢`.cfg` Ö®ÀàµÄÎÄ¼þ±¨"ÎÞ·¨Ê¶±ð"¡£

---

## 10. ÌùÍ¼·½ÏòÓë"ºÚÉ«ÇøÓò"

* **ÉÏÏÂµßµ¹**£ºAerofly µÄ TTX ÌùÍ¼°´×ÔÏÂ¶øÉÏµÄË³Ðò´æ´¢¡£µ¼³ö PNG Ê±Ä¬ÈÏ**ÒÑ¾­·­×ª**£¬
  ËùÒÔ´ò¿ª¾ÍÊÇÕýÁ¢µÄ£¨GUI Àï `Flip vertically (Aerofly stores textures bottom-up)` Ä¬ÈÏ¹´Ñ¡£¬
  ÃüÁîÐÐ¿ÉÓÃ `--no-flip` ¹Ø±Õ£©¡£Ð´»Ø TTX Ê±Ò²Ä¬ÈÏ·­×ª£¬Òò´Ë"µ¼³ö¡ú±à¼­¡úµ¼Èë"ÄÜ¶ÔÉÏ¡£
* **Ä³¸öÇøÓòÏÔÊ¾Îª´¿ºÚ**£ºÄÇÊÇ **alpha = 0 µÄÍ¸Ã÷ÏñËØ**£¬ÕÕÆ¬²é¿´Æ÷»á°ÑÍ¸Ã÷»­³ÉºÚÉ«
  £¨A350 µÄ 276 ÕÅ RGBA Êä³öÀïÓÐ 183 ÕÅµ×²¿´øÍêÈ«Í¸Ã÷£¬ÊôÓÚÌùÍ¼±¾ÉíµÄÄÚÈÝ£©¡£
  ÓÃ Photoshop / GIMP / Ö§³ÖÍ¸Ã÷Í¨µÀµÄ²é¿´Æ÷´ò¿ª¾ÍÊÇÍ¸Ã÷µÄ¡£×ª»»ÈÕÖ¾ÀïÏÖÔÚ»áÌáÊ¾
  "x% of the pixels are transparent (alpha=0)"¡£

## 11. ÓÃ»§Ä¿Â¼ÀïµÄÍ¿×° TTX£¨Ô­°æ¶Á²»ÁËµÄÄÇÖÖ£©

`Documents\Aerofly FS 4\aircraft\...` ÀïµÄ×ÔÖÆÍ¿×° `.ttx` ÓÃµÄÊÇÁíÒ»ÖÖÍâ²ãÍ·£º
Ç° `0x40` ×Ö½ÚÓë `0x5A` Òì»ò£¬Íâ²ã magic Îª `a5 a4 24 c7 b7 3c eb b7`¡£
Ô­°æ Python ¹¤¾ß»áÖ±½Ó±¨ "not a TTX texture container"£¬±¾°æÒÑÊ¶±ð²¢½â³ö
£¨½âÍê¾ÍÊÇÆÕÍ¨µÄ LZHAM `compress_file` ÈÝÆ÷£©¡£Êµ²â°ÑÕû¸ö `Documents\Aerofly FS 4\aircraft`
Ä¿Â¼£¨º¬ÄÏº½ A330 µÈÍ¿×°£©25 ¸öÎÄ¼þ 3.8 Ãë×ª»»Íê³É¡£

---

## 12. ÌùÍ¼³ß´ç / ¸²¸ÇÔ­ÌùÍ¼£¨ÖØÒª£©

ÓÎÏ·ÀïµÄÃ¿¸öÌùÍ¼"²ÛÎ»"¶¼ÓÐ¹Ì¶¨³ß´çºÍ¸ñÊ½£¨A350 »úÉí `ext01_fuselage_color.ttx` ÊÇ
**2048¡Á2048 / DXT1 / 12 ¼¶ mip**£©¡£Èç¹ûÐ´½øÈ¥µÄ TTX ³ß´ç²»¶Ô£¨ÀýÈç 6136¡Á6132¡¢
·Ç 2 ´ÎÃÝ¡¢13 ¼¶ mip£©£¬ÓÎÏ·²ÉÑù¾Í»á´íÎ»/¼ÓÔØÊ§°Ü¡ª¡ª±íÏÖÎª»úÉí´óÃæ»ý·¢ºÚ¡¢³öÏÖ²¹¶¡¡¢
¿´ÆðÀ´"ÉÏÏÂµßµ¹"¡£

v1.1 ×Ô¶¯´¦Àí£º

* **¸²¸ÇÒÑ´æÔÚµÄ .ttx Ê±**£º¶ÁÈ¡¸ÃÌùÍ¼Ô­ÓÐµÄ¿í¸ßºÍ GPU ¸ñÊ½£¬°ÑÔ´Í¼Ëõ·Å/×ª³ÉÒ»ÖÂµÄ
  £¨`--no-fit` ¿É¹Ø±Õ£©¡£
* **Ä¿±ê²»´æÔÚÊ±**£º·Ç 2 ´ÎÃÝµÄÍ¼Æ¬×Ô¶¯Ëõµ½×î½üµÄ 2 ´ÎÃÝ£¨Èç 6136¡Á6132 ¡ú 4096¡Á4096£©¡£
* Ëõ·ÅÓÃÃæ»ýÆ½¾ù£¨ËõÐ¡£©/ Ë«ÏßÐÔ£¨·Å´ó£©£¬GIMP/PS Ö®Íâ²»ÐèÒª×Ô¼ºÏÈ¸Ä³ß´ç¡£

Êµ²â£º°ÑÔ­ÌùÍ¼µ¼³ö³É PNG ÔÙÔ­ÑùÐ´»Ø£¬³ß´ç/¸ñÊ½/mip ÊýÓëÔ­ÎÄ¼þÒ»ÖÂ£¬Íù·µ PSNR 46.8 dB¡£
£¨ÎÄ¼þÌå»ý»á±È IPACS Ô­°æ´ó£¬ÒòÎª LZHAM Ð´µÄÊÇ raw block£¬²»Ó°Ïì¶ÁÈ¡¡££©

### Êä³ö³ß´çÔõÃ´Ñ¡

¸²¸ÇÒÑÓÐÌùÍ¼Ê±Ä¬ÈÏ **Match original**£¨ºÍÔ­²ÛÎ»Ò»ÖÂ£¬×î°²È«£©¡£ÏëÒª¸üÇåÎú¿ÉÒÔÑ¡¸ü´ó³ß´ç£º

| Ñ¡Ïî | ½á¹û£¨Ô´Í¼ 6136¡Á6132£© | ËµÃ÷ |
|---|---|---|
| Match original | 2048¡Á2048 / 12 mips£¬2.7 MB | ÓëÔ­°æ²ÛÎ»ÍêÈ«Ò»ÖÂ |
| 4096 px | 4096¡Á4096 / 13 mips£¬10.7 MB | ±È 2048 ±£Áô¸ü¶àÏ¸½Ú£¬ÍÆ¼ö |
| 2048 px | 2048¡Á2048 | Ç¿ÖÆ½µ²ÉÑù |
| 8192 px | 8192¡Á8192 / 14 mips£¬42.7 MB | ÒÑ³¬¹ýÔ´Í¼·Ö±æÂÊ£¬Ö»ÊÇ·Å´ó£¬²»Ôö¼ÓÏ¸½Ú |
| Source (max PO2) | 4096¡Á4096 | È¡²»³¬¹ýÔ´Í¼µÄ 2 ´ÎÃÝ |

ÃüÁîÐÐ£º`--size 4096`¡£ÏÔ´æ/¼ÓÔØÊ±¼äËæÃæ»ýÏßÐÔÔö³¤£º40962 Ô¼Îª 20482 µÄ 4 ±¶£¬
81922 ÓÖÊÇ 40962 µÄ 4 ±¶£¨DXT1 µ¥ÕÅÔ¼ 11 MB ¡ú 43 MB£¬º¬ mip£©¡£

---

## 13. È¥µô¡¸Windows ÒÑ±£»¤ÄãµÄµçÄÔ / ÈÔÒªÔËÐÐ¡¹

ÕâÊÇ SmartScreen ¶Ô**Î´Ç©Ãû³ÌÐò**µÄÌáÊ¾¡£±¾²Ö¿âÌá¹©Ò»¸öÒ»¼ü´¦Àí½Å±¾£¨µ±Ç°ÓÃ»§Ö¤Êé£¬²»ÐèÒª¹ÜÀíÔ±£©£º

```powershell
# ÏÈ¹Ø±Õ AeroflyConv£¨Ç©ÃûÐèÒªÐ´ÎÄ¼þ£©
powershell -NoProfile -ExecutionPolicy Bypass -File tools/sign-app.ps1
# »òÔÚ·¢²¼Ä¿Â¼ÀïË«»÷£ºÇ©Ãû-È¥³ý°²È«ÌáÊ¾.cmd
```

Ëü»á£º

1. ´´½¨×ÔÇ©´úÂëÇ©ÃûÖ¤Êé `CN=AeroflyConv Local Publishing`£¨µ±Ç°ÓÃ»§£¬5 Äê£©£»
2. °Ñ¸ÃÖ¤Êéµ¼Èë `µ±Ç°ÓÃ»§\ÊÜÐÅÈÎµÄ¸ùÖ¤Êé°ä·¢»ú¹¹` Óë `ÊÜÐÅÈÎµÄ·¢²¼Õß`£»
3. ¸øËùÓÐ `AeroflyConv.exe` Ç©Ãû£¨SHA-256£©¡£

Ö®ºóË«»÷ exe ²»ÔÙµ¯"ÈÔÒªÔËÐÐ"¡£`build.ps1` ·¢²¼»á×Ô¶¯µ÷ÓÃËü£¨Ç©ÃûÖ¤Êé´æÔÚÊ±£©¡£

³·Ïú£¨É¾³ýÐÅÈÎÓëÖ¤Êé£©£º

```powershell
certutil -delstore -user Root 583EEC3674E239898741C9809B2EF06E96F7E748
certutil -delstore -user TrustedPublisher 583EEC3674E239898741C9809B2EF06E96F7E748
Remove-Item Cert:\CurrentUser\My\583EEC3674E239898741C9809B2EF06E96F7E748
```

---

## 14. °²×¿ / ÆäËûÆ½Ì¨

**ºËÐÄ¿âÊÇ¿ÉÒÆÖ²µÄ**£º`AeroflyConv.Core` 6501 ÐÐ¡¢ÁãÆ½Ì¨ API£¨Ã»ÓÐ System.Drawing / DllImport /
Win32£©£¬ÒÀÀµµÄ ImageSharp / NLayer / NVorbis Ò²¶¼ÊÇ¿çÆ½Ì¨µÄ¡£

ÒÑ¾­ÑéÖ¤µÄÂäµØ·½Ê½£º

| ·½Ê½ | ×´Ì¬ | ÃüÁî |
|---|---|---|
| Linux / Android(Termux) ÃüÁîÐÐ | ? ÒÑ¹¹½¨¿ÉÓÃ | `dotnet publish -r linux-arm64 --self-contained` ¡ú `dist/AeroflyConv-android-termux-arm64.zip` |
| .NET for Android Ô­ÉúÐ¡ App | ´ý×ö£¨Ðè Android SDK£© | ¸´ÓÃ Core£¬SAF ÎÄ¼þÑ¡Ôñ + ½ø¶È + ±£´æµ½ Download |
| MAUI ¿çÆ½Ì¨ GUI | ´ý×ö£¨Ðè `maui-android` workload£© | ½çÃæÐèÖØÐ´ |

ÔÚ Android ÉÏÓÃÖ®Ç°ÒªÖªµÀµÄÁ½¼þÊÂ£º

1. **´æ´¢ÏÞÖÆ**£ºAndroid 11+ Àï `Android/data/<°üÃû>/` ¶ÔÆäËû App£¨Termux Ò²Ëã£©²»¿É¶Á¡£
   Èç¹û Aerofly FS Global µÄÄÚÈÝÔÚÕâ¸öÄ¿Â¼£¬±ØÐëÏÈ root / Shizuku£¬»ò°ÑÎÄ¼þ¸´ÖÆµ½
   `Download` ÔÙÓÃ±¾¹¤¾ß´¦Àí¡£
2. **ASTC**£º²¿·ÖÒÆ¶¯Éè±¸ÓÃ ASTC ÌùÍ¼£¬±¾ÏîÄ¿Ö»Ö§³Ö ETC2/ETC1£¨½âÂë£©+ ETC2£¨±àÂë£©£¬
   Óöµ½ ASTC »áÃ÷È·±¨´í¡£ÒªÍêÕûÖ§³ÖÒÆ¶¯¶ËÐèÒª²¹Ò»¸ö ASTC ½âÂëÆ÷¡£
