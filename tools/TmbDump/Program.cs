using AeroflyConv.Core.Tm;

string path = args.Length > 0 ? args[0] : throw new ArgumentException("usage: TmbDump <file.tmb> [depth]");
int maxDepth = args.Length > 1 ? int.Parse(args[1]) : 3;

List<TmNode> nodes = TmTree.ParseFile(path);
Console.WriteLine($"{Path.GetFileName(path)}: {nodes.Count} root node(s)");

void Walk(TmNode node, int depth)
{
    string pad = new string(' ', depth * 2);
    string type = TmTree.TypeName(node.TypeId);
    string name = TmTree.NameName(node.NameId);
    if (node.IsContainer)
    {
        Console.WriteLine($"{pad}{type}/{name}  [{node.Children.Count} children, size={node.RawSize}]");
        if (depth >= maxDepth) return;
        foreach (TmNode child in node.Children) Walk(child, depth + 1);
    }
    else
    {
        string extra = "";
        string? s = node.StringValue();
        if (s is not null && s.Length > 0 && s.All(c => c >= 32 && c < 127)) extra = $" = \"{s}\"";
        else if (node.Payload.Length is 4 or 8 or 16) extra = " = " + Convert.ToHexString(node.Payload);
        Console.WriteLine($"{pad}{type}/{name}  [{node.Payload.Length} bytes]{extra}");
    }
}

foreach (TmNode node in nodes) Walk(node, 0);
