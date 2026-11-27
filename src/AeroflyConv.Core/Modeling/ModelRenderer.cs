using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AeroflyConv.Core.Aerofly;

namespace AeroflyConv.Core.Modeling;

/// <summary>A decoded texture handed to the renderer: 8-bit RGBA, top-down rows.</summary>
public sealed record RenderTexture(byte[] Rgba, int Width, int Height);

/// <summary>Camera, lighting and output settings for a livery preview render.</summary>
public sealed class RenderOptions
{
    /// <summary>Output edge length in pixels (the shipped previews are 2048).</summary>
    public int Size = 2048;

    /// <summary>Rotation of the aircraft around its vertical axis, in degrees.</summary>
    public float Yaw = -118f;

    /// <summary>Camera elevation above the model, in degrees (negative looks up at the belly).</summary>
    public float Pitch = -10f;

    /// <summary>Camera roll, in degrees.</summary>
    public float Roll;

    /// <summary>Vertical field of view in degrees; smaller is a longer lens.</summary>
    public float FieldOfView = 26f;

    /// <summary>Fraction of the frame left empty around the aircraft.</summary>
    public float Margin = 0.04f;

    /// <summary>Render at N×N and box-filter down: cheap anti-aliasing (1 = off).</summary>
    public int SuperSample = 2;

    /// <summary>Background colour is only used when <see cref="OpaqueBackground"/> is set.</summary>
    public (byte R, byte G, byte B) Background = (8, 10, 14);

    public bool OpaqueBackground;

    /// <summary>Two stacked copies (the lower one mirrored), like the stock previews.</summary>
    public bool StockPair = true;

    /// <summary>Mirror U when sampling (some liveries paint only one side).</summary>
    public bool FlipU;

    /// <summary>Vertical offset of each copy, as a fraction of the canvas.</summary>
    public float StockOffset = 0.185f;

    /// <summary>Strength of the sky/key light coming from above and slightly behind the camera.</summary>
    public float KeyLight = 0.85f;

    /// <summary>Strength of the bounce/fill light coming from the opposite side.</summary>
    public float FillLight = 0.32f;

    /// <summary>Ambient term so shadowed parts stay readable.</summary>
    public float Ambient = 0.42f;

    /// <summary>Overall exposure multiplier.</summary>
    public float Exposure = 1f;
}

/// <summary>
/// Software rasteriser that turns a TMB scene into a livery preview bitmap.
/// It only needs the diffuse textures; normals come from the mesh, and the shading
/// is a two-light diffuse model which is close enough to the in-game look.
/// </summary>
public static class ModelRenderer
{
    private readonly struct Triangle
    {
        public readonly double X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2;
        public readonly double U0, V0, U1, V1, U2, V2;
        public readonly double Nx0, Ny0, Nz0, Nx1, Ny1, Nz1, Nx2, Ny2, Nz2;
        public readonly int Material;

        /// <summary>True when the mesh carried normals (used for backface culling).</summary>
        public readonly bool HasNormals;

        public Triangle(
            double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2,
            double u0, double v0, double u1, double v1, double u2, double v2,
            double nx0, double ny0, double nz0, double nx1, double ny1, double nz1, double nx2, double ny2, double nz2,
            int material, bool hasNormals = false)
        {
            HasNormals = hasNormals;
            X0 = x0; Y0 = y0; Z0 = z0; X1 = x1; Y1 = y1; Z1 = z1; X2 = x2; Y2 = y2; Z2 = z2;
            U0 = u0; V0 = v0; U1 = u1; V1 = v1; U2 = u2; V2 = v2;
            Nx0 = nx0; Ny0 = ny0; Nz0 = nz0; Nx1 = nx1; Ny1 = ny1; Nz1 = nz1; Nx2 = nx2; Ny2 = ny2; Nz2 = nz2;
            Material = material;
        }
    }

    /// <summary>Renders <paramref name="scene"/> and returns a <c>size × size</c> RGBA buffer (top-down rows).</summary>
    public static byte[] Render(
        TmbScene scene,
        Func<string, RenderTexture?> resolveTexture,
        RenderOptions options,
        Action<string>? log = null)
    {
        List<Triangle> triangles = BuildTriangles(scene, log);
        if (triangles.Count == 0)
            throw new InvalidOperationException("no geometry in this file (is it a .tmb scene?)");

        int size = Math.Max(64, Math.Min(8192, options.Size));
        int ss = Math.Max(1, Math.Min(3, options.SuperSample));
        int width = size * ss, height = size * ss;

        (double cx, double cy, double cz, double radius) = Bounds(triangles);
        double[,] view = LookAt(triangles, cx, cy, cz, radius, options, width, height);
        byte[] color = new byte[width * height * 4];
        float[] depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);

        var textures = new Dictionary<int, RenderTexture?>();
        int drawn = Rasterize(triangles, view, width, height, color, depth, options, scene, resolveTexture, textures, log);

        if (options.OpaqueBackground)
            ComposeBackground(color, width, height, options.Background);

        if (ss > 1)
            color = Downsample(color, width, height, ss);

        // The basis used above puts +X on the left; mirror the finished frame so the
        // projection matches how Blender / the game show the aircraft (text reads correctly).
        MirrorX(color, size);

        return options.StockPair ? MakeStockPair(color, size, options.StockOffset) : color;
    }

    // ---------------------------------------------------------------- geometry

    private static List<Triangle> BuildTriangles(TmbScene scene, Action<string>? log)
    {
        var list = new List<Triangle>(scene.TriangleCount + 64);
        foreach (TmbGeometry geometry in scene.Geometries)
        {
            double[] m = geometry.Matrix.Length == 16 ? geometry.Matrix : Identity;
            foreach (TmbPatch patch in geometry.Patches)
            {
                int stride = patch.VertexSize > 0 ? patch.VertexSize : 8;
                int vertexCount = patch.Vertices.Length / stride;
                if (vertexCount == 0 || patch.Indices.Length < 3) continue;

                (int posOffset, int normalOffset, int uvOffset) = AttributeOffsets(patch);
                bool hasNormal = normalOffset >= 0;
                bool hasUv = uvOffset >= 0;

                // Some files store 1-based indices; detect it per patch.
                uint maxIndex = 0;
                foreach (uint raw in patch.Indices) if (raw > maxIndex) maxIndex = raw;
                int shift = maxIndex >= (uint)vertexCount ? 1 : 0;

                var px = new double[vertexCount];
                var py = new double[vertexCount];
                var pz = new double[vertexCount];
                var nx = new double[vertexCount];
                var ny = new double[vertexCount];
                var nz = new double[vertexCount];
                var uu = new double[vertexCount];
                var vv = new double[vertexCount];

                for (int i = 0; i < vertexCount; i++)
                {
                    int b = i * stride;
                    if (b + posOffset + 2 >= patch.Vertices.Length) { vertexCount = i; break; }
                    double x = patch.Vertices[b + posOffset];
                    double y = patch.Vertices[b + posOffset + 1];
                    double z = patch.Vertices[b + posOffset + 2];
                    (px[i], py[i], pz[i]) = Transform(m, x, y, z);

                    if (hasNormal && b + normalOffset + 2 < patch.Vertices.Length)
                        (nx[i], ny[i], nz[i]) = TransformDirection(m,
                            patch.Vertices[b + normalOffset],
                            patch.Vertices[b + normalOffset + 1],
                            patch.Vertices[b + normalOffset + 2]);
                    else
                    {
                        nx[i] = 0; ny[i] = 0; nz[i] = 0;
                    }

                    if (hasUv && b + uvOffset + 1 < patch.Vertices.Length)
                    {
                        uu[i] = patch.Vertices[b + uvOffset];
                        vv[i] = patch.Vertices[b + uvOffset + 1];
                    }
                }

                for (int f = 0; f + 2 < patch.Indices.Length; f += 3)
                {
                    int i0 = (int)patch.Indices[f] - shift;
                    int i1 = (int)patch.Indices[f + 1] - shift;
                    int i2 = (int)patch.Indices[f + 2] - shift;
                    if ((uint)i0 >= (uint)vertexCount || (uint)i1 >= (uint)vertexCount || (uint)i2 >= (uint)vertexCount) continue;

                    list.Add(new Triangle(
                        px[i0], py[i0], pz[i0], px[i1], py[i1], pz[i1], px[i2], py[i2], pz[i2],
                        uu[i0], vv[i0], uu[i1], vv[i1], uu[i2], vv[i2],
                        nx[i0], ny[i0], nz[i0], nx[i1], ny[i1], nz[i1], nx[i2], ny[i2], nz[i2],
                        patch.MaterialId, hasNormal));
                }
            }
        }

        log?.Invoke($"render: {list.Count:N0} triangles from {scene.Geometries.Count:N0} parts");
        return list;
    }

    private static (int Pos, int Normal, int Uv) AttributeOffsets(TmbPatch patch)
    {
        int stride = patch.VertexSize > 0 ? patch.VertexSize : 8;
        int[] sizes = patch.AttribSize;
        int[] offsets = patch.AttribOffset;
        int pos = -1, normal = -1, uv = -1;
        for (int i = 0; i < offsets.Length && i < sizes.Length; i++)
        {
            int offset = offsets[i];
            int size = sizes[i];
            if (offset < 0 || size <= 0 || offset + size > stride) continue;
            if (size == 3 && pos < 0) { pos = offset; continue; }
            if (size == 3 && normal < 0) { normal = offset; continue; }
            if (size == 2 && uv < 0) uv = offset;
        }
        if (pos < 0) pos = 0;
        return (pos, normal, uv);
    }

    private static (double, double, double, double) Bounds(List<Triangle> triangles)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (Triangle t in triangles)
        {
            minX = Math.Min(minX, Math.Min(t.X0, Math.Min(t.X1, t.X2)));
            minY = Math.Min(minY, Math.Min(t.Y0, Math.Min(t.Y1, t.Y2)));
            minZ = Math.Min(minZ, Math.Min(t.Z0, Math.Min(t.Z1, t.Z2)));
            maxX = Math.Max(maxX, Math.Max(t.X0, Math.Max(t.X1, t.X2)));
            maxY = Math.Max(maxY, Math.Max(t.Y0, Math.Max(t.Y1, t.Y2)));
            maxZ = Math.Max(maxZ, Math.Max(t.Z0, Math.Max(t.Z1, t.Z2)));
        }

        double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5, cz = (minZ + maxZ) * 0.5;
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        double radius = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (radius < 1e-6) radius = 1;
        return (cx, cy, cz, radius);
    }

    /// <summary>Builds a world→view matrix for a turntable camera that frames the whole model.</summary>
    private static double[,] LookAt(
        List<Triangle> triangles,
        double cx, double cy, double cz, double radius,
        RenderOptions options, int width, int height)
    {
        double yaw = options.Yaw * Math.PI / 180.0;
        double pitch = options.Pitch * Math.PI / 180.0;
        double roll = options.Roll * Math.PI / 180.0;

        double fov = Math.Max(4.0, Math.Min(120.0, options.FieldOfView)) * Math.PI / 180.0;
        double halfFov = fov * 0.5;
        double focal = 1.0 / Math.Tan(halfFov);
        double target = 1.0 - Math.Max(0.0, Math.Min(0.4, options.Margin));

        // Camera direction: positive pitch moves the eye up, so the default gives
        // a slightly low 3/4 view (the way the stock previews look).
        // Z is up in Aerofly models: yaw spins around the vertical axis, pitch lifts the eye.
        double dirX = Math.Cos(pitch) * Math.Sin(yaw);
        double dirY = Math.Cos(pitch) * Math.Cos(yaw);
        double dirZ = Math.Sin(pitch);

        double distance = radius / Math.Sin(halfFov) * 1.6;
        double tx = cx, ty = cy, tz = cz;

        for (int pass = 0; pass < 4; pass++)
        {
            double ex = tx + dirX * distance;
            double ey = ty + dirY * distance;
            double ez = tz + dirZ * distance;

            double fx = tx - ex, fy = ty - ey, fz = tz - ez;
            double flen = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            fx /= flen; fy /= flen; fz /= flen;

            double upx = 0, upy = 0, upz = 1;
            double rx = fy * upz - fz * upy, ry = fz * upx - fx * upz, rz = fx * upy - fy * upx;
            double rlen = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rlen < 1e-9) { rx = 1; ry = 0; rz = 0; rlen = 1; }
            rx /= rlen; ry /= rlen; rz /= rlen;
            double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;

            // Fit the projected silhouette: walk a sample of vertices, measure the
            // normalised extents and nudge distance / target until they fill the frame.
            double maxAbs = 0, minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            int step = Math.Max(1, triangles.Count / 2048);
            for (int i = 0; i < triangles.Count; i += step)
            {
                ref readonly Triangle t = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(triangles)[i];
                FitPoint(t.X0, t.Y0, t.Z0);
                FitPoint(t.X1, t.Y1, t.Z1);
                FitPoint(t.X2, t.Y2, t.Z2);
            }

            void FitPoint(double px, double py, double pz)
            {
                double vx = px - ex, vy = py - ey, vz = pz - ez;
                double depth = fx * vx + fy * vy + fz * vz;
                if (depth <= 1e-6) return;
                double sx = focal * (rx * vx + ry * vy + rz * vz) / depth;
                double sy = focal * (ux * vx + uy * vy + uz * vz) / depth;
                maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(sx), Math.Abs(sy)));
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }

            if (maxAbs <= 1e-6 || double.IsInfinity(maxAbs)) break;

            if (pass < 3)
            {
                distance *= maxAbs / target;
                double mvx = (minX + maxX) * 0.5, mvy = (minY + maxY) * 0.5;
                double scale = distance * Math.Tan(halfFov);
                double shiftX = mvx * scale, shiftY = mvy * scale;
                tx += rx * shiftX + ux * shiftY;
                ty += ry * shiftX + uy * shiftY;
                tz += rz * shiftX + uz * shiftY;
                continue;
            }

            if (Math.Abs(roll) > 1e-6)
            {
                double c = Math.Cos(roll), s = Math.Sin(roll);
                double nrx = rx * c + ux * s, nry = ry * c + uy * s, nrz = rz * c + uz * s;
                double nux = ux * c - rx * s, nuy = uy * c - ry * s, nuz = uz * c - rz * s;
                rx = nrx; ry = nry; rz = nrz;
                ux = nux; uy = nuy; uz = nuz;
            }

            return new double[4, 4]
            {
                { rx, ry, rz, -(rx * ex + ry * ey + rz * ez) },
                { ux, uy, uz, -(ux * ex + uy * ey + uz * ez) },
                { -fx, -fy, -fz, fx * ex + fy * ey + fz * ez },
                { 0, 0, 0, 1 },
            };
        }

        throw new InvalidOperationException("camera could not be fitted to the model bounds");
    }

    // -------------------------------------------------------------- rasteriser

    private static int Rasterize(
        List<Triangle> triangles,
        double[,] view,
        int width,
        int height,
        byte[] color,
        float[] depth,
        RenderOptions options,
        TmbScene scene,
        Func<string, RenderTexture?> resolve,
        Dictionary<int, RenderTexture?> cache,
        Action<string>? log)
    {
        double fov = Math.Max(4.0, Math.Min(120.0, options.FieldOfView)) * Math.PI / 180.0;
        double f = 1.0 / Math.Tan(fov * 0.5);
        double cxp = width * 0.5, cyp = height * 0.5;
        int drawn = 0;

        // Light directions in world space: key from above/behind-left, fill from the front-right.
        var key = Normalize(-0.42, -0.55, -0.72);
        var fill = Normalize(0.55, 0.35, -0.55);
        // Camera position in world space: view = [R | -R*eye]  =>  eye = -R^T * t.
        double eyeX = -(view[0, 0] * view[0, 3] + view[1, 0] * view[1, 3] + view[2, 0] * view[2, 3]);
        double eyeY = -(view[0, 1] * view[0, 3] + view[1, 1] * view[1, 3] + view[2, 1] * view[2, 3]);
        double eyeZ = -(view[0, 2] * view[0, 3] + view[1, 2] * view[1, 3] + view[2, 2] * view[2, 3]);

        var rim = Normalize(0.0, 0.9, 0.2);

        object gate = new();
        Parallel.For(0, triangles.Count, i =>
        {
            Triangle t = triangles[i];
            double vx0 = t.X0, vy0 = t.Y0, vz0 = t.Z0;
            double vx1 = t.X1, vy1 = t.Y1, vz1 = t.Z1;
            double vx2 = t.X2, vy2 = t.Y2, vz2 = t.Z2;

            ToView(view, ref vx0, ref vy0, ref vz0);
            ToView(view, ref vx1, ref vy1, ref vz1);
            ToView(view, ref vx2, ref vy2, ref vz2);

            // Inside the frustum only (z is negative in front of the camera).
            if (vz0 > -1e-6 || vz1 > -1e-6 || vz2 > -1e-6) return;

            double sx0 = cxp + f * (vx0 / -vz0) * (height * 0.5);
            double sy0 = cyp - f * (vy0 / -vz0) * (height * 0.5);
            double sx1 = cxp + f * (vx1 / -vz1) * (height * 0.5);
            double sy1 = cyp - f * (vy1 / -vz1) * (height * 0.5);
            double sx2 = cxp + f * (vx2 / -vz2) * (height * 0.5);
            double sy2 = cyp - f * (vy2 / -vz2) * (height * 0.5);

            int minX = (int)Math.Floor(Math.Min(sx0, Math.Min(sx1, sx2)));
            int maxX = (int)Math.Ceiling(Math.Max(sx0, Math.Max(sx1, sx2)));
            int minY = (int)Math.Floor(Math.Min(sy0, Math.Min(sy1, sy2)));
            int maxY = (int)Math.Ceiling(Math.Max(sy0, Math.Max(sy1, sy2)));
            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX > width - 1) maxX = width - 1;
            if (maxY > height - 1) maxY = height - 1;
            if (minX > maxX || minY > maxY) return;

            double area = (sx1 - sx0) * (sy2 - sy0) - (sx2 - sx0) * (sy1 - sy0);
            if (Math.Abs(area) < 1e-9) return;

            // Geometric face normal (world space), oriented towards the camera.
            double e1x = t.X1 - t.X0, e1y = t.Y1 - t.Y0, e1z = t.Z1 - t.Z0;
            double e2x = t.X2 - t.X0, e2y = t.Y2 - t.Y0, e2z = t.Z2 - t.Z0;
            double fnx = e1y * e2z - e1z * e2y;
            double fny = e1z * e2x - e1x * e2z;
            double fnz = e1x * e2y - e1y * e2x;
            double flen = Math.Sqrt(fnx * fnx + fny * fny + fnz * fnz);
            if (flen > 1e-12) { fnx /= flen; fny /= flen; fnz /= flen; }
            if (fnx * (eyeX - t.X0) + fny * (eyeY - t.Y0) + fnz * (eyeZ - t.Z0) < 0)
            {
                fnx = -fnx; fny = -fny; fnz = -fnz;
            }

            // Normal-based backface culling: interior shells of Aerofly models have
            // inconsistent winding but their normals still point inwards, so this is
            // what keeps the cabin from poking through the fuselage.
            if (t.HasNormals)
            {
                double nx = (t.Nx0 + t.Nx1 + t.Nx2) / 3.0;
                double ny = (t.Ny0 + t.Ny1 + t.Ny2) / 3.0;
                double nz = (t.Nz0 + t.Nz1 + t.Nz2) / 3.0;
                // View direction (world space) from the surface towards the camera.
                double vxDir = eyeX - t.X0, vyDir = eyeY - t.Y0, vzDir = eyeZ - t.Z0;
                if (nx * vxDir + ny * vyDir + nz * vzDir <= 0) return;
            }
            else if (area > 0) return;

            double invArea = 1.0 / area;

            // Backface culling: keep whichever winding faces the camera.
            if (area > 0) return;

            TmbMaterial? material = t.Material >= 0 && t.Material < scene.Materials.Count ? scene.Materials[t.Material] : null;
            RenderTexture? albedo = null;
            if (material is not null)
            {
                lock (gate)
                {
                    if (!cache.TryGetValue(t.Material, out albedo))
                    {
                        albedo = LoadDiffuse(material, resolve, log);
                        cache[t.Material] = albedo;
                    }
                }
            }

            double nx0 = t.Nx0, ny0 = t.Ny0, nz0 = t.Nz0;
            double nx1 = t.Nx1, ny1 = t.Ny1, nz1 = t.Nz1;
            double nx2 = t.Nx2, ny2 = t.Ny2, nz2 = t.Nz2;

            for (int y = minY; y <= maxY; y++)
            {
                double py = y + 0.5;
                for (int x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5;
                    double w0 = ((sx1 - px) * (sy2 - py) - (sx2 - px) * (sy1 - py)) * invArea;
                    double w1 = ((sx2 - px) * (sy0 - py) - (sx0 - px) * (sy2 - py)) * invArea;
                    double w2 = 1.0 - w0 - w1;
                    if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;

                    double invZ = w0 / -vz0 + w1 / -vz1 + w2 / -vz2;
                    if (invZ <= 0) continue;
                    float zValue = (float)(1.0 / invZ);
                    int index = y * width + x;
                    // 1e-6 relative bias: Aerofly meshes have duplicate skins that
                    // would otherwise z-fight into white speckles.
                    if (zValue <= depth[index] * (1.0 + 1e-6)) continue;

                    double u = (w0 * t.U0 / -vz0 + w1 * t.U1 / -vz1 + w2 * t.U2 / -vz2) / invZ;
                    double v = (w0 * t.V0 / -vz0 + w1 * t.V1 / -vz1 + w2 * t.V2 / -vz2) / invZ;

                    double nxx = w0 * nx0 + w1 * nx1 + w2 * nx2;
                    double nyy = w0 * ny0 + w1 * ny1 + w2 * ny2;
                    double nzz = w0 * nz0 + w1 * nz1 + w2 * nz2;
                    double nlen = Math.Sqrt(nxx * nxx + nyy * nyy + nzz * nzz);
                    if (nlen > 1e-9) { nxx /= nlen; nyy /= nlen; nzz /= nlen; }
                    else
                    {
                        // Degenerate vertex normals: fall back to the geometric face normal,
                        // otherwise those triangles light up as if facing the key light.
                        nxx = fnx; nyy = fny; nzz = fnz;
                    }

                    double r = 1, g = 1, b = 1, a = 1;
                    if (albedo is not null)
                        (r, g, b, a) = Sample(albedo, options.FlipU ? 1.0 - u : u, v);

                    double lambertKey = Math.Max(0, nxx * key.X + nyy * key.Y + nzz * key.Z);
                    double lambertFill = Math.Max(0, nxx * fill.X + nyy * fill.Y + nzz * fill.Z);
                    double lambertRim = Math.Max(0, nxx * rim.X + nyy * rim.Y + nzz * rim.Z);
                    double light = options.Ambient
                                   + options.KeyLight * lambertKey
                                   + options.FillLight * lambertFill
                                   + 0.10 * lambertRim;

                    double exposure = options.Exposure;
                    byte cr = ToByte(r * light * exposure);
                    byte cg = ToByte(g * light * exposure);
                    byte cb = ToByte(b * light * exposure);

                    depth[index] = zValue;
                    int o = index * 4;
                    color[o] = cr;
                    color[o + 1] = cg;
                    color[o + 2] = cb;
                    color[o + 3] = ToByte(a);

                    if (a < 1.0 && color[o + 3] < 250)
                        color[o + 3] = 0;
                }
            }

            lock (gate) drawn++;
        });

        log?.Invoke($"render: {drawn:N0} triangles visible");
        return drawn;
    }

    private static RenderTexture? LoadDiffuse(TmbMaterial material, Func<string, RenderTexture?> resolve, Action<string>? log)
    {
        TmbTextureRef? reference = material.Texture("diffuse") ?? material.Texture("ambient");
        if (reference is null) return null;
        RenderTexture? texture = resolve(reference.Name);
        if (texture is null)
            log?.Invoke($"render: texture '{reference.Name}' for material '{material.Name}' not found");
        return texture;
    }

    private static (double R, double G, double B, double A) Sample(RenderTexture texture, double u, double v)
    {
        // Wrap, then bilinear sample. TTX v grows downwards in file order.
        double x = (u - Math.Floor(u)) * (texture.Width - 1);
        double y = (v - Math.Floor(v)) * (texture.Height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, texture.Width - 1);
        int y1 = Math.Min(y0 + 1, texture.Height - 1);
        double fx = x - x0, fy = y - y0;

        int o00 = (y0 * texture.Width + x0) * 4;
        int o10 = (y0 * texture.Width + x1) * 4;
        int o01 = (y1 * texture.Width + x0) * 4;
        int o11 = (y1 * texture.Width + x1) * 4;
        byte[] d = texture.Rgba;

        double r = Lerp(d[o00], d[o10], d[o01], d[o11], fx, fy) / 255.0;
        double g = Lerp(d[o00 + 1], d[o10 + 1], d[o01 + 1], d[o11 + 1], fx, fy) / 255.0;
        double b = Lerp(d[o00 + 2], d[o10 + 2], d[o01 + 2], d[o11 + 2], fx, fy) / 255.0;
        double a = Lerp(d[o00 + 3], d[o10 + 3], d[o01 + 3], d[o11 + 3], fx, fy) / 255.0;
        return (r, g, b, a);
    }

    private static double Lerp(byte c00, byte c10, byte c01, byte c11, double fx, double fy)
    {
        double top = c00 + (c10 - c00) * fx;
        double bottom = c01 + (c11 - c01) * fx;
        return top + (bottom - top) * fy;
    }

    private static void MirrorX(byte[] image, int size)
    {
        byte[] row = new byte[size * 4];
        for (int y = 0; y < size; y++)
        {
            int off = y * size * 4;
            Array.Copy(image, off, row, 0, size * 4);
            for (int x = 0; x < size; x++)
            {
                int src = x * 4, dst = off + (size - 1 - x) * 4;
                image[dst] = row[src];
                image[dst + 1] = row[src + 1];
                image[dst + 2] = row[src + 2];
                image[dst + 3] = row[src + 3];
            }
        }
    }

    /// <summary>
    /// Builds the stock look: the rendered aircraft above, its vertical mirror below,
    /// overlapping slightly in the middle, everything on a transparent background.
    /// </summary>
    private static byte[] MakeStockPair(byte[] image, int size, float offset)
    {
        byte[] output = new byte[size * size * 4];
        int shift = (int)Math.Round(Math.Clamp(offset, 0.0f, 0.45f) * size);

        for (int y = 0; y < size; y++)
        {
            int src = y + shift;
            if (src < 0 || src >= size) continue;
            Array.Copy(image, src * size * 4, output, y * size * 4, size * 4);
        }

        for (int y = 0; y < size; y++)
        {
            int src = size - 1 - (y - shift);
            if (src < 0 || src >= size) continue;
            int sOff = src * size * 4, dOff = y * size * 4;
            for (int x = 0; x < size; x++)
            {
                int si = sOff + x * 4, di = dOff + x * 4;
                byte a = image[si + 3];
                if (a == 0) continue;
                double alpha = a / 255.0;
                int da = output[di + 3];
                double dstAlpha = da / 255.0;
                double outAlpha = alpha + dstAlpha * (1.0 - alpha);
                if (outAlpha <= 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    double srcColor = image[si + c] / 255.0;
                    double dstColor = output[di + c] / 255.0;
                    double blended = (srcColor * alpha + dstColor * dstAlpha * (1.0 - alpha)) / outAlpha;
                    output[di + c] = (byte)Math.Clamp(Math.Round(blended * 255.0), 0, 255);
                }
                output[di + 3] = (byte)Math.Clamp(Math.Round(outAlpha * 255.0), 0, 255);
            }
        }

        return output;
    }

    private static void ComposeBackground(byte[] color, int width, int height, (byte R, byte G, byte B) background)
    {
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            if (color[o + 3] != 0) continue;
            color[o] = background.R;
            color[o + 1] = background.G;
            color[o + 2] = background.B;
            color[o + 3] = 255;
        }
    }

    private static byte[] Downsample(byte[] source, int width, int height, int factor)
    {
        int outWidth = width / factor, outHeight = height / factor;
        byte[] result = new byte[outWidth * outHeight * 4];
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int sy = y * factor + dy;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int sx = x * factor + dx;
                        int o = (sy * width + sx) * 4;
                        int alpha = source[o + 3];
                        r += source[o] * alpha;
                        g += source[o + 1] * alpha;
                        b += source[o + 2] * alpha;
                        a += alpha;
                        n++;
                    }
                }

                int dst = (y * outWidth + x) * 4;
                if (a > 0)
                {
                    result[dst] = (byte)Math.Min(255, r / a);
                    result[dst + 1] = (byte)Math.Min(255, g / a);
                    result[dst + 2] = (byte)Math.Min(255, b / a);
                    result[dst + 3] = (byte)Math.Min(255, a / Math.Max(1, n));
                }
            }
        }
        return result;
    }

    // ----------------------------------------------------------------- helpers

    private static void ToView(double[,] m, ref double x, ref double y, ref double z)
    {
        double vx = m[0, 0] * x + m[0, 1] * y + m[0, 2] * z + m[0, 3];
        double vy = m[1, 0] * x + m[1, 1] * y + m[1, 2] * z + m[1, 3];
        double vz = m[2, 0] * x + m[2, 1] * y + m[2, 2] * z + m[2, 3];
        x = vx; y = vy; z = vz;
    }

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (length < 1e-9) return (0, 0, 1);
        return (x / length, y / length, z / length);
    }

    private static byte ToByte(double value)
    {
        double scaled = value * 255.0;
        if (scaled <= 0) return 0;
        if (scaled >= 255) return 255;
        return (byte)(scaled + 0.5);
    }

    private static (double, double, double) Transform(double[] m, double x, double y, double z)
    {
        double rx = m[0] * x + m[1] * y + m[2] * z + m[3];
        double ry = m[4] * x + m[5] * y + m[6] * z + m[7];
        double rz = m[8] * x + m[9] * y + m[10] * z + m[11];
        return (rx, ry, rz);
    }

    private static (double, double, double) TransformDirection(double[] m, double x, double y, double z)
    {
        double rx = m[0] * x + m[1] * y + m[2] * z;
        double ry = m[4] * x + m[5] * y + m[6] * z;
        double rz = m[8] * x + m[9] * y + m[10] * z;
        double length = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        return length > 1e-12 ? (rx / length, ry / length, rz / length) : (0, 0, 1);
    }

    private static readonly double[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    /// <summary>Human readable camera description, used in logs and release notes.</summary>
    public static string Describe(RenderOptions options) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{options.Size}px, yaw {options.Yaw:0}°, pitch {options.Pitch:0}°, fov {options.FieldOfView:0}°");
}
