using System.Text;

namespace AeroflyConv.Core.Tm;

/// <summary>
/// Node of a text form TM tree, the layout used by the plain text <c>.tmd</c> / <c>.tmc</c>
/// aircraft definition files:
/// <code>
/// &lt;[rigidbody][Fuselage][]
///     &lt;[float64][Mass][1200]&gt;
/// &gt;
/// </code>
/// A node either has children (block) or holds an inline value (leaf).
/// </summary>
public sealed class TmTextNode
{
    public string Type = "";
    public string Name = "";
    public string Value = "";
    public List<TmTextNode> Children = [];
    public TmTextNode? Parent;

    public TmTextNode() { }

    public TmTextNode(string type, string name, string value = "")
    {
        Type = type;
        Name = name;
        Value = value;
    }

    public bool IsBlock => Children.Count > 0 || EndsWithoutValue;

    /// <summary>Original file had a block form even though it may be empty.</summary>
    public bool EndsWithoutValue;

    public TmTextNode Add(TmTextNode child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public TmTextNode Add(string type, string name, string value = "") =>
        Add(new TmTextNode(type, name, value));

    /// <summary>Detaches this node from its parent.</summary>
    public void Remove() => Parent?.Children.Remove(this);

    public TmTextNode? Child(string name)
    {
        foreach (TmTextNode c in Children)
            if (c.Name == name) return c;
        return null;
    }

    public IEnumerable<TmTextNode> All(string type)
    {
        foreach (TmTextNode c in Children)
            if (c.Type == type) yield return c;
    }

    public IEnumerable<TmTextNode> Descendants()
    {
        foreach (TmTextNode child in Children)
        {
            yield return child;
            foreach (TmTextNode nested in child.Descendants()) yield return nested;
        }
    }

    public IEnumerable<TmTextNode> Descendants(string type)
    {
        foreach (TmTextNode c in Children)
        {
            if (c.Type == type) yield return c;
            foreach (TmTextNode d in c.Descendants(type)) yield return d;
        }
    }

    /// <summary>All nodes (any type) whose name matches.</summary>
    public IEnumerable<TmTextNode> ByName(string name) =>
        Children.Where(c => c.Name == name);

    public string? Get(string childName) => Child(childName)?.Value;

    public double GetDouble(string childName, double fallback = 0)
    {
        string? v = Get(childName);
        if (v is null) return fallback;
        return double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;
    }

    /// <summary>Parses a bracketed array value like <c>[ 1.0 2.0 3.0 ]</c> into numbers.</summary>
    public static double[] ParseArray(string value)
    {
        string inner = value.Trim();
        if (inner.StartsWith('[')) inner = inner[1..];
        if (inner.EndsWith(']')) inner = inner[..^1];
        string[] parts = inner.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result[i]);
        return result;
    }

    public string[] ParseWords()
    {
        string inner = Value.Trim();
        return inner.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public override string ToString() => $"<[{Type}][{Name}][{Value}]>";
}

/// <summary>Reads and writes the text form of TM trees (.tmd / .tmc aircraft files).</summary>
public static class TmTextTree
{
    /// <summary>
    /// Parses a text TM file into its (possibly several) root nodes.
    /// Scans characters rather than lines, because shipped .tmd files contain values that
    /// span many lines (long GeometryList blocks) and alignment padding inside the header.
    /// </summary>
    public static List<TmTextNode> Parse(string text)
    {
        var roots = new List<TmTextNode>();
        var stack = new Stack<TmTextNode>();
        int pos = 0;
        int n = text.Length;

        while (pos < n)
        {
            char c = text[pos];
            if (c == '\uFEFF') { pos++; continue; }
            if (c == '/' && pos + 1 < n && text[pos + 1] == '/') { pos = SkipLine(text, pos); continue; }
            if (c is ' ' or '\t' or '\r' or '\n') { pos++; continue; }

            if (c == '>')
            {
                if (stack.Count > 0) stack.Pop();
                pos++;
                continue;
            }

            if (c == '<')
            {
                TmTextNode? node = ReadNode(text, pos, out int next);
                if (node is null)
                {
                    pos = Math.Max(next, pos + 1);
                    continue;
                }

                if (stack.Count > 0) stack.Peek().Add(node);
                else roots.Add(node);

                // EndsWithoutValue marks a block header, i.e. a node that owns children
                if (node.EndsWithoutValue) stack.Push(node);
                pos = next;
                continue;
            }

            pos = SkipLine(text, pos);
        }

        return roots;
    }

    private static int SkipLine(string text, int pos)
    {
        int nl = text.IndexOf('\n', pos);
        return nl < 0 ? text.Length : nl + 1;
    }

    /// <summary>Reads one <c>&lt;[type][name][value]</c> header; the value may span lines.</summary>
    private static TmTextNode? ReadNode(string text, int pos, out int next)
    {
        int n = text.Length;
        next = pos + 1;
        if (next >= n || text[next] != '[') return null;

        int p = next;
        if (!ReadField(text, ref p, out string type)) return null;
        if (!ReadField(text, ref p, out string name)) return null;

        var node = new TmTextNode(type, name);
        int trailer = p;
        if (ReadField(text, ref p, out string value))
        {
            node.Value = value;
            trailer = p;
        }

        // Values may also be partly or not at all bracketed, e.g.
        //   <[string8][InputLuminance]Main[MainBatteryChargeLowLight.Output]>
        // Everything up to the next '<', '>' or comment tail belongs to the value.
        int q = trailer;
        while (q < n && text[q] is not ('<' or '>'))
        {
            if (text[q] == '/' && q + 1 < n && text[q + 1] == '/') break;
            q++;
        }

        string raw = text[trailer..q].Trim();
        if (raw.Length > 0)
            node.Value = node.Value.Trim().Length > 0 ? (node.Value + raw).Trim() : raw;

        // decide leaf vs container by the next significant character
        int s = q;
        while (s < n)
        {
            if (text[s] is ' ' or '\t' or '\r' or '\n') { s++; continue; }
            if (text[s] == '/' && s + 1 < n && text[s + 1] == '/') { s = SkipLine(text, s); continue; }
            break;
        }

        if (s < n && text[s] == '>')
        {
            node.EndsWithoutValue = false;
            next = s + 1;
        }
        else
        {
            node.EndsWithoutValue = true;
            next = s;
        }

        return node;
    }

    private static bool ReadField(string text, ref int p, out string content)
    {
        int n = text.Length;
        while (p < n && text[p] is ' ' or '\t' or '\r' or '\n') p++;
        if (p >= n || text[p] != '[')
        {
            content = "";
            return false;
        }

        int end = text.IndexOf(']', p + 1);
        if (end < 0)
        {
            content = "";
            return false;
        }

        content = text[(p + 1)..end];
        p = end + 1;
        return true;
    }

    public static List<TmTextNode> ParseFile(string path) => Parse(File.ReadAllText(path));

    public static void SaveFile(string path, IEnumerable<TmTextNode> roots)
    {
        var sb = new StringBuilder(1 << 16);
        foreach (TmTextNode root in roots) Emit(sb, root, 0);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static string ToText(IEnumerable<TmTextNode> roots)
    {
        var sb = new StringBuilder(1 << 16);
        foreach (TmTextNode root in roots) Emit(sb, root, 0);
        return sb.ToString();
    }

    public static void Emit(StringBuilder sb, TmTextNode node, int indent)
    {
        string pad = new(' ', indent);
        if (node.Children.Count == 0 && !node.EndsWithoutValue)
        {
            sb.Append(pad).Append("<[").Append(node.Type).Append("][").Append(node.Name)
              .Append("][").Append(node.Value).Append("]>\n");
            return;
        }
        sb.Append(pad).Append("<[").Append(node.Type).Append("][").Append(node.Name)
          .Append("][").Append(node.Value).Append("]\n");
        foreach (TmTextNode child in node.Children) Emit(sb, child, indent + 4);
        sb.Append(pad).Append(">\n");
    }

    /// <summary>Depth first search for the first node with the given type and name.</summary>
    public static TmTextNode? Find(IEnumerable<TmTextNode> roots, string type, string name)
    {
        foreach (TmTextNode root in roots)
        {
            if (root.Type == type && root.Name == name) return root;
            TmTextNode? hit = Find(root.Children, type, name);
            if (hit is not null) return hit;
        }
        return null;
    }
}
