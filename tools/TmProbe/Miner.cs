using System.Text;
using System.Text.Json;
using AeroflyConv.Core.Tm;

namespace TmProbe;

/// <summary>
/// Mines the object schema out of a corpus of official <c>.tmd</c> files: for every simulation
/// object (a child of a <c>pointer_list_tm*</c> list) it records which properties that object type
/// carries, their value type, and a sample value. This is what lets the converter synthesise a
/// dynamics file instead of cloning a template.
/// </summary>
internal static class Miner
{
    private sealed record PropertyInfo(string Type, int Count, string Sample);

    public static int Run(string root, string outPath)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"not a directory: {root}");
            return 1;
        }

        var types = new SortedDictionary<string, Dictionary<string, PropertyInfo>>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(root, "*.tmd", SearchOption.AllDirectories).ToList();
        int parsed = 0, failed = 0, objects = 0;

        foreach (string file in files)
        {
            List<TmTextNode> roots;
            try
            {
                roots = TmTextTree.ParseFile(file);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            parsed++;
            foreach (TmTextNode node in roots)
                Walk(node, types, ref objects);
        }

        var payload = new
        {
            meta = new
            {
                corpus = root,
                files = parsed,
                failed,
                objects,
                types = types.Count,
                built = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            },
            types = types.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        p => p.Key,
                        p => new { type = p.Value.Type, count = p.Value.Count, sample = p.Value.Sample })),
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, options), new UTF8Encoding(false));

        Console.WriteLine($"files      : {parsed} parsed, {failed} failed");
        Console.WriteLine($"objects    : {objects}");
        Console.WriteLine($"types      : {types.Count}");
        Console.WriteLine($"written    : {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
        return 0;
    }

    private static void Walk(TmTextNode node, IDictionary<string, Dictionary<string, PropertyInfo>> types,
        ref int objects)
    {
        if (IsObjectContainer(node))
        {
            objects++;
            if (!types.TryGetValue(node.Type, out Dictionary<string, PropertyInfo>? props))
                types[node.Type] = props = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

            foreach (TmTextNode child in node.Children)
            {
                string sample = Trim(child.Value);
                if (props.TryGetValue(child.Name, out PropertyInfo? existing))
                    props[child.Name] = existing with { Count = existing.Count + 1 };
                else
                    props[child.Name] = new PropertyInfo(child.Type, 1, sample);
            }
        }

        foreach (TmTextNode child in node.Children)
            Walk(child, types, ref objects);
    }

    /// <summary>Objects live directly under a <c>pointer_list_tm&lt;universe&gt;</c> section.</summary>
    private static bool IsObjectContainer(TmTextNode node)
    {
        if (node.Children.Count == 0) return false;
        string parentType = node.Parent?.Type ?? "";
        if (parentType.StartsWith("pointer_list_tm", StringComparison.Ordinal)) return true;
        // controls.tmd nests objects one level deeper inside typed groups
        return parentType.StartsWith("pointer_list_", StringComparison.Ordinal) == false
               && parentType.Length == 0;
    }

    private static string Trim(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length > 60) trimmed = trimmed[..60] + "…";
        return trimmed.Replace('\n', ' ').Replace('\r', ' ');
    }
}
