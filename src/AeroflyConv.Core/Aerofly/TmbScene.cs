using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Aerofly;

/// <summary>One texture slot of an Aerofly material.</summary>
public sealed class TmbTextureRef
{
    /// <summary>Channel: diffuse, normal, specular, reflection, ambient, illumination, alpha.</summary>
    public string Channel = "diffuse";
    /// <summary>Texture base name (the .ttx file name without extension).</summary>
    public string Name = "";
    public bool RepeatS = true;
    public bool RepeatT = true;
    public float UvScaling = 1f;
}

/// <summary>An Aerofly material: shader hint plus texture slots.</summary>
public sealed class TmbMaterial
{
    public string Name = "";
    public string ShaderHint = "pbr exterior";
    public readonly List<TmbTextureRef> Textures = [];

    public TmbTextureRef? Texture(string channel) => Textures.FirstOrDefault(t => t.Channel == channel);
}

/// <summary>A single patch: a triangle list sharing one material.</summary>
public sealed class TmbPatch
{
    /// <summary>Number of triangles.</summary>
    public int NumFaces;
    public int MaterialId;
    /// <summary>Interleaved vertex data: position (3), normal (3), uv (2).</summary>
    public float[] Vertices = [];
    /// <summary>Triangle list: three indices per face.</summary>
    public uint[] Indices = [];
    /// <summary>Vertex float stride; the exporter always writes 8.</summary>
    public int VertexSize = 8;
}

/// <summary>A drawable object: one transform, one collision mesh, one or more patches.</summary>
public sealed class TmbGeometry
{
    public int Id;
    public string Name = "";
    public string Tag = "";
    /// <summary>Row-major 4x4 transform, usually the identity.</summary>
    public double[] Matrix =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];
    public readonly List<TmbPatch> Patches = [];
    /// <summary>Collision hull points (x,y,z triples).</summary>
    public readonly List<float> CollisionPoints = [];
    /// <summary>Collision hull triangle indices.</summary>
    public readonly List<uint> CollisionIndices = [];
}

/// <summary>An Aerofly .tmb scene: materials plus geometry list.</summary>
public sealed class TmbScene
{
    public readonly List<TmbMaterial> Materials = [];
    public readonly List<TmbGeometry> Geometries = [];

    public int AddMaterial(TmbMaterial material)
    {
        int index = Materials.IndexOf(material);
        if (index >= 0) return index;
        Materials.Add(material);
        return Materials.Count - 1;
    }

    public int TriangleCount => Geometries.Sum(g => g.Patches.Sum(p => p.NumFaces));
    public int VertexCount => Geometries.Sum(g => g.Patches.Sum(p => p.Vertices.Length / Math.Max(1, p.VertexSize)));

    /// <summary>Converts the scene into the TM node tree that is stored inside a .tmb.</summary>
    public List<TmNode> ToNodes()
    {
        TmNode scene = TmTree.Container("tmxglscene", "");

        TmNode materialList = TmTree.Container("list_tmxglmaterial", "material_list");
        foreach (TmbMaterial material in Materials)
        {
            TmNode m = TmTree.Container("list_tmxglmaterial", "material_list");
            m.Add(TmTree.StringLeaf("string8u", "name", material.Name));
            m.Add(TmTree.StringLeaf("string8u", "shader_hint", material.ShaderHint));

            TmNode textureList = TmTree.Container("list_tm_tmtexture_index_pair", "texture_list");
            foreach (TmbTextureRef texture in material.Textures)
            {
                TmNode t = TmTree.Container("list_tm_tmtexture_index_pair", "texture_list");
                t.Add(TmTree.StringLeaf("string8u", "channel", texture.Channel));
                t.Add(TmTree.StringLeaf("string8", "name", texture.Name));
                t.Add(TmTree.Leaf("bool", "repeat_s", TmTree.BoolPayload(texture.RepeatS)));
                t.Add(TmTree.Leaf("bool", "repeat_t", TmTree.BoolPayload(texture.RepeatT)));
                t.Add(TmTree.Leaf("float32", "uvscaling", TmTree.Float32Payload(texture.UvScaling)));
                textureList.Add(t);
            }
            m.Add(textureList);
            m.Add(TmTree.Container("list_tm_shader_fixed_uniform", "uniform_list"));
            materialList.Add(m);
        }
        scene.Add(materialList);

        TmNode geometryList = TmTree.Container("pointer_list_tmxglgeometry", "geometry_list");
        foreach (TmbGeometry geometry in Geometries)
        {
            TmNode g = TmTree.Container("pointer_list_tmxglgeometry", "geometry_list");
            g.Add(TmTree.Leaf("int32", "id", TmTree.Int32Payload(geometry.Id)));
            g.Add(TmTree.StringLeaf("string8u", "name", geometry.Name));
            g.Add(TmTree.StringLeaf("string8u", "tag", geometry.Tag));
            g.Add(TmTree.Leaf("matrix4_float64", "matrix", TmTree.DoublesPayload(geometry.Matrix)));

            TmNode collision = TmTree.Container("tmxglmesh", "mesh_collision");
            collision.Add(ValueNode("list_vector3_float32", "point_list", TmTree.Vector3sPayload(geometry.CollisionPoints.ToArray())));
            collision.Add(ValueNode("uint32", "face_indices_list", TmTree.UInt32sPayload(geometry.CollisionIndices.ToArray())));
            g.Add(collision);

            TmNode patchList = TmTree.Container("list_tmxglpatch", "patch_list");
            foreach (TmbPatch patch in geometry.Patches)
            {
                TmNode p = TmTree.Container("list_tmxglpatch", "patch_list");
                p.Add(TmTree.Leaf("uint32", "num_faces", TmTree.UInt32Payload((uint)patch.NumFaces)));
                p.Add(TmTree.Leaf("int32", "material_id", TmTree.Int32Payload(patch.MaterialId)));
                p.Add(TmTree.Leaf("int32", "vf_size", TmTree.Int32Payload(patch.VertexSize)));
                p.Add(TmTree.Leaf("int32", "vertex_attrib_size", TmTree.Int32sPayload(VertexAttribSize)));
                p.Add(TmTree.Leaf("int32", "vertex_attrib_offset", TmTree.Int32sPayload(VertexAttribOffset)));
                p.Add(ValueNode("float32", "vertices", TmTree.Floats32Payload(patch.Vertices)));
                p.Add(ValueNode("uint32", "index_list", TmTree.UInt32sPayload(patch.Indices)));
                p.Add(ValueNode("uint32", "strip_index_list", TmTree.UInt32sPayload([])));
                patchList.Add(p);
            }
            g.Add(patchList);
            geometryList.Add(g);
        }
        scene.Add(geometryList);

        scene.Add(TmTree.Container("list_tmxglgeometry_reference", "reference_geometry_list"));
        scene.Add(TmTree.Container("list_tmxglgeometry_virtual", "virtual_geometry_list"));
        return [scene];
    }

    /// <summary>Attribute layout used by every shipped patch: position, normal, uv.</summary>
    public static readonly int[] VertexAttribSize = [3, 3, 0, 0, 2, 0, 0, 0];
    public static readonly int[] VertexAttribOffset = [0, 3, -1, -1, 6, -1, -1, -1];

    /// <summary>Serialises the scene as a complete (LZHAM compressed) .tmb file.</summary>
    public byte[] ToTmb(bool compress = true) => TmTree.WriteContainer(ToNodes().ToArray(), compress);

    /// <summary>
    /// Empty lists are written as empty containers, exactly like the shipped assets; a zero length
    /// leaf makes the engine look up a property that does not exist in that node's context.
    /// </summary>
    private static TmNode ValueNode(string type, string name, byte[] payload) =>
        payload.Length == 0 ? TmTree.Container(type, name) : TmTree.Leaf(type, name, payload);
}
