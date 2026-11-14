using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Textures;

if (args.Length == 0)
{
    Console.WriteLine("usage: AstcDump <file.ttx|payload.bin> [--encode] [--blocks N]");
    return 1;
}

string path = args[0];
bool reencode = args.Contains("--encode");
int showBlocks = 8;
string? pngPath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--blocks" && i + 1 < args.Length)
        showBlocks = int.Parse(args[i + 1]);
    else if (args[i] == "--png" && i + 1 < args.Length)
        pngPath = args[++i];
}

byte[] payload;
int width, height, blockWidth, blockHeight;
string label;

if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
{
    // payload.bin + sidecar meta.txt ("width height block")
    payload = File.ReadAllBytes(path);
    string metaPath = Path.ChangeExtension(path, ".txt");
    string[] meta = File.ReadAllText(metaPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    width = int.Parse(meta[0]);
    height = int.Parse(meta[1]);
    blockWidth = int.Parse(meta[2]);
    blockHeight = blockWidth;
    label = "raw";
}
else
{
    byte[] raw = File.ReadAllBytes(path);
    byte[] plain = TtxFile.Inflate(raw);
    TtxInfo info = TtxFile.Parse(plain);
    (int bw, int bh) = TextureFormats.AstcBlockSize(info.Format);
    if (bw <= 0)
    {
        Console.WriteLine($"not an ASTC texture: {info.Format}");
        return 1;
    }

    width = info.Width;
    height = info.Height;
    blockWidth = bw;
    blockHeight = bh;
    payload = info.Pixels.ToArray();
    label = info.Format;
    Console.WriteLine($"{Path.GetFileName(path)}: {info.Format} {width}x{height} mips={info.Mips} payload={payload.Length}");
}

int blocksX = (width + blockWidth - 1) / blockWidth;
int blocksY = (height + blockHeight - 1) / blockHeight;
int blockCount = blocksX * blocksY;
Console.WriteLine($"blocks={blockCount} ({blocksX}x{blocksY}) blockSize={blockWidth}x{blockHeight}");

Dump("file", payload, blockCount, showBlocks);

if (pngPath is not null)
{
    byte[] decoded = new byte[width * height * 4];
    AstcCodec.Decode(payload, width, height, blockWidth, blockHeight, decoded);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
    AeroflyConv.Core.Media.ImageIo.SavePng(pngPath, decoded, width, height, true);
    Console.WriteLine($"decoded -> {pngPath}");
}

if (reencode)
{
    byte[] rgba = new byte[width * height * 4];
    AstcCodec.Decode(payload, width, height, blockWidth, blockHeight, rgba);
    byte[] mine = new byte[blockCount * 16];
    AstcEncoder.Encode(rgba, width, height, blockWidth, blockHeight, mine, AstcQuality.Fast);
    Dump("ours", mine, blockCount, showBlocks);
}

return 0;

void Dump(string name, byte[] data, int count, int show)
{
    var lowBits = new Dictionary<int, int>();
    var weightRange = new Dictionary<int, int>();
    var dualPlane = new Dictionary<int, int>();
    var partNum = new Dictionary<int, int>();
    for (int i = 0; i < count; i++)
    {
        int o = i * 16;
        int b0 = data[o];
        int b1 = data[o + 1];
        Bump(lowBits, b0 & 3);
        Bump(dualPlane, (b1 >> 2) & 1);
        Bump(weightRange, ((b0 >> 4) & 1) | ((b1 << 2) & 8));
        Bump(partNum, ((b1 >> 3) & 3) + 1);
    }

    Console.WriteLine($"--- {name}: b0&3 = {Fmt(lowBits)}");
    Console.WriteLine($"    dualPlane = {Fmt(dualPlane)}");
    Console.WriteLine($"    weightRange = {Fmt(weightRange)}");
    Console.WriteLine($"    partNum = {Fmt(partNum)}");

    Console.Write("    first blocks: ");
    for (int i = 0; i < Math.Min(show, count); i++)
    {
        int o = i * 16;
        Console.Write($"{data[o]:x2}{data[o + 1]:x2}{data[o + 2]:x2}{data[o + 3]:x2} ");
    }

    Console.WriteLine();
}

static void Bump(Dictionary<int, int> map, int key)
{
    map[key] = map.TryGetValue(key, out int value) ? value + 1 : 1;
}

static string Fmt(Dictionary<int, int> map) =>
    string.Join(" ", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
