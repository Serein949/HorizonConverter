using System.Buffers.Binary;
using System.Globalization;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Aerofly;

/// <summary>Reads a shipped .tmb back into a <see cref="TmbScene"/>.</summary>
public static class TmbReader
{
    public static TmbScene Read(string path) => FromNodes(TmTree.ParseFile(path));

    public static TmbScene FromNodes(IReadOnlyList<TmNode> nodes)
    {
        var scene = new TmbScene();
        TmNode? root = nodes.Count > 0 ? nodes[0] : null;
        if (root is null) return scene;

        TmNode? materials = Child(root, "material_list");
        if (materials is not null)
        {
            foreach (TmNode node in materials.Children)
                scene.Materials.Add(ReadMaterial(node));
        }

        TmNode? geometries = Child(root, "geometry_list");
        if (geometries is not null)
        {
            foreach (TmNode node in geometries.Children)
            {
                TmbGeometry? geometry = ReadGeometry(node);
                if (geometry is not null) scene.Geometries.Add(geometry);
            }
        }

        return scene;
    }

    private static TmbMaterial ReadMaterial(TmNode node)
    {
        var material = new TmbMaterial
        {
            Name = String(node, "name") ?? "",
            ShaderHint = String(node, "shader_hint") ?? "",
        };

        TmNode? list = Child(node, "texture_list");
        if (list is not null)
        {
            foreach (TmNode t in list.Children)
            {
                var texture = new TmbTextureRef
                {
                    Channel = String(t, "channel") ?? "diffuse",
                    Name = String(t, "name") ?? "",
                };
                TmNode? repeatS = Child(t, "repeat_s");
                if (repeatS is not null && repeatS.Payload.Length > 0) texture.RepeatS = repeatS.Payload[0] != 0;
                TmNode? repeatT = Child(t, "repeat_t");
                if (repeatT is not null && repeatT.Payload.Length > 0) texture.RepeatT = repeatT.Payload[0] != 0;
                TmNode? scale = Child(t, "uvscaling");
                if (scale is not null && scale.Payload.Length >= 4)
                    texture.UvScaling = BinaryPrimitives.ReadSingleLittleEndian(scale.Payload);
                material.Textures.Add(texture);
            }
        }

        return material;
    }

    private static TmbGeometry? ReadGeometry(TmNode node)
    {
        var geometry = new TmbGeometry
        {
            Name = String(node, "name") ?? "",
            Tag = String(node, "tag") ?? "",
        };

        TmNode? id = Child(node, "id");
        if (id is not null && id.Payload.Length >= 4)
            geometry.Id = BinaryPrimitives.ReadInt32LittleEndian(id.Payload);

        TmNode? matrix = Child(node, "matrix");
        if (matrix is not null && matrix.Payload.Length >= 128)
        {
            for (int i = 0; i < 16; i++)
                geometry.Matrix[i] = BinaryPrimitives.ReadDoubleLittleEndian(matrix.Payload.AsSpan(i * 8, 8));
        }

        TmNode? collision = Child(node, "mesh_collision");
        if (collision is not null)
        {
            TmNode? points = Child(collision, "point_list");
            if (points is not null) geometry.CollisionPoints.AddRange(Floats(points.Payload));
            TmNode? faces = Child(collision, "face_indices_list");
            if (faces is not null) geometry.CollisionIndices.AddRange(UInt32s(faces.Payload));
        }

        TmNode? patches = Child(node, "patch_list");
        if (patches is not null)
        {
            foreach (TmNode patch in patches.Children)
            {
                TmbPatch? parsed = ReadPatch(patch);
                if (parsed is not null) geometry.Patches.Add(parsed);
            }
        }

        return geometry;
    }

    private static TmbPatch? ReadPatch(TmNode node)
    {
        var patch = new TmbPatch
        {
            NumFaces = (int)UInt32(Child(node, "num_faces")),
            MaterialId = (int)UInt32(Child(node, "material_id")),
            VertexSize = (int)UInt32(Child(node, "vf_size")),
        };

        TmNode? attribSize = Child(node, "vertex_attrib_size");
        if (attribSize is not null)
        {
            uint[] sizes = UInt32s(attribSize.Payload);
            if (sizes.Length > 0) patch.AttribSize = sizes.Select(x => (int)x).ToArray();
        }

        TmNode? attribOffset = Child(node, "vertex_attrib_offset");
        if (attribOffset is not null)
        {
            int[] offsets = Int32s(attribOffset.Payload);
            if (offsets.Length > 0) patch.AttribOffset = offsets;
        }

        TmNode? vertices = Child(node, "vertices");
        if (vertices is null) return null;
        patch.Vertices = Floats(vertices.Payload);

        TmNode? indices = Child(node, "index_list");
        if (indices is not null) patch.Indices = UInt32s(indices.Payload);

        TmNode? strips = Child(node, "strip_index_list");
        if (patch.Indices.Length == 0 && strips is not null)
            patch.Indices = ExpandStrips(UInt32s(strips.Payload));

        if (patch.VertexSize <= 0) patch.VertexSize = 8;
        if (patch.NumFaces == 0) patch.NumFaces = patch.Indices.Length / 3;
        return patch;
    }

    /// <summary>Expands a triangle strip into a triangle list (degenerate triangles are dropped).</summary>
    private static uint[] ExpandStrips(uint[] strip)
    {
        if (strip.Length < 3) return [];
        var list = new List<uint>((strip.Length - 2) * 3);
        for (int i = 0; i + 2 < strip.Length; i++)
        {
            uint a = strip[i], b = strip[i + 1], c = strip[i + 2];
            if (a == b || b == c || a == c) continue;
            if ((i & 1) == 0) list.AddRange([a, b, c]);
            else list.AddRange([b, a, c]);
        }
        return list.ToArray();
    }

    // ------------------------------------------------------------------ helpers
    public static TmNode? Child(TmNode node, string name)
    {
        ulong id = TmTree.NameId(name) ?? 0;
        foreach (TmNode child in node.Children)
            if (child.NameId == id) return child;
        return null;
    }

    public static string? String(TmNode node, string childName)
    {
        TmNode? child = Child(node, childName);
        return child?.StringValue();
    }

    public static uint UInt32(TmNode? node)
    {
        if (node is null || node.Payload.Length < 4) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(node.Payload);
    }

    public static float[] Floats(byte[] payload)
    {
        int n = payload.Length / 4;
        var values = new float[n];
        for (int i = 0; i < n; i++)
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(i * 4, 4));
        return values;
    }

    public static uint[] UInt32s(byte[] payload)
    {
        int n = payload.Length / 4;
        var values = new uint[n];
        for (int i = 0; i < n; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i * 4, 4));
        return values;
    }

    public static int[] Int32s(byte[] payload)
    {
        int n = payload.Length / 4;
        var values = new int[n];
        for (int i = 0; i < n; i++)
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(i * 4, 4));
        return values;
    }

    public static string FormatStats(TmbScene scene) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{scene.Materials.Count} materials, {scene.Geometries.Count} parts, {scene.TriangleCount:N0} triangles, {scene.VertexCount:N0} vertices");
}
