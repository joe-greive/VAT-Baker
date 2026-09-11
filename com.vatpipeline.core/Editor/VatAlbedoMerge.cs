using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Flattens several "Shader Graphs/RGBRecolor_*" materials into ONE albedo texture so a
    /// character can ship as a single-material, single-primitive mesh.
    ///
    /// Why this exists: most glb importers re-index each primitive separately, which destroys
    /// vertex order — and vertex order IS the VAT's column mapping, so a two-primitive character
    /// renders as shredded geometry. For a portable export, single primitive is a hard
    /// requirement rather than a preference.
    ///
    /// It works because every one of these materials samples the same shared RGB mask atlas and
    /// differs only in its three tint colours. Each material's own UV region is resolved with
    /// its own colours and written into a shared texture. Where two materials' UV islands
    /// overlap, the later one wins and those texels are wrong for the earlier — so the overlap
    /// count is reported rather than assumed harmless.
    /// </summary>
    public static class VatAlbedoMerge
    {
        public sealed class Result
        {
            public Texture2D Texture;
            public int Resolution;
            public int ContestedTexels;
            public int CoveredTexels;
            public string Report;
        }

        /// <summary>
        /// Materials and submeshes must be index-aligned. Returns null when the materials are
        /// not all recolour materials over one shared mask, in which case merging is unsafe and
        /// the caller should keep them separate.
        /// </summary>
        public static Result Build(IList<Material> materials, IList<List<int>> submeshes,
                                   Vector2[] uv, string outputPath)
        {
            if (materials == null || materials.Count == 0) return null;

            Texture sharedMask = null;
            foreach (var m in materials)
            {
                if (m == null || m.shader == null || !m.shader.name.Contains("RGBRecolor")) return null;
                if (!m.HasProperty("_MainTex") || !m.HasProperty("_Color1")) return null;
                var mask = m.GetTexture("_MainTex");
                if (mask == null) return null;
                if (sharedMask == null) sharedMask = mask;
                else if (sharedMask != mask) return null;   // different atlases cannot merge
            }

            string maskPath = AssetDatabase.GetAssetPath(sharedMask);
            if (string.IsNullOrEmpty(maskPath) || !File.Exists(maskPath)) return null;

            var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!readable.LoadImage(File.ReadAllBytes(maskPath), false))
            {
                Object.DestroyImmediate(readable);
                return null;
            }

            int res = readable.width;
            var mask3 = readable.GetPixels();

            // Material 0 fills the whole texture first. That leaves no holes, so bilinear
            // sampling and mip generation at UV island edges cannot pull in black.
            var output = new Color[mask3.Length];
            Resolve(mask3, output, materials[0]);

            var coverage = new List<bool[]>(materials.Count);
            for (int i = 0; i < materials.Count; i++)
            {
                var buf = new bool[res * res];
                Rasterize(buf, res, uv, submeshes[i]);
                coverage.Add(buf);
            }

            // Later materials overwrite inside their own UV coverage only.
            var scratch = new Color[mask3.Length];
            for (int i = 1; i < materials.Count; i++)
            {
                Resolve(mask3, scratch, materials[i]);
                var cover = coverage[i];
                for (int p = 0; p < output.Length; p++)
                    if (cover[p]) output[p] = scratch[p];
            }

            int contested = 0, covered = 0;
            for (int p = 0; p < output.Length; p++)
            {
                int hits = 0;
                for (int i = 0; i < coverage.Count; i++) if (coverage[i][p]) hits++;
                if (hits > 0) covered++;
                if (hits > 1) contested++;
            }

            var texture = new Texture2D(res, readable.height, TextureFormat.RGBA32, false);
            texture.SetPixels(output);
            texture.Apply(false, false);
            File.WriteAllBytes(outputPath, texture.EncodeToPNG());

            Object.DestroyImmediate(readable);
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport);

            var names = new List<string>();
            foreach (var m in materials) names.Add(m.name);

            return new Result
            {
                Texture = outputPath.StartsWith("Assets/")
                    ? AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath)
                    : null,
                Resolution = res,
                ContestedTexels = contested,
                CoveredTexels = covered,
                Report = $"merged {materials.Count} materials [{string.Join(", ", names)}] " +
                         $"into one {res}x{res} albedo: {covered} covered texels, " +
                         $"{contested} contested " +
                         (contested == 0 ? "(lossless)" : "(later material wins on those)"),
            };
        }

        static void Resolve(Color[] mask, Color[] destination, Material material)
        {
            Color c1 = material.GetColor("_Color1");
            Color c2 = material.GetColor("_Color2");
            Color c3 = material.GetColor("_Color3");

            for (int i = 0; i < mask.Length; i++)
            {
                var m = mask[i];
                var c = c1 * m.r + c2 * m.g + c3 * m.b;
                c.a = 1f;
                destination[i] = c;
            }
        }

        static void Rasterize(bool[] buffer, int res, Vector2[] uv, List<int> triangles)
        {
            for (int t = 0; t + 2 < triangles.Count; t += 3)
                Triangle(buffer, res, uv[triangles[t]], uv[triangles[t + 1]], uv[triangles[t + 2]]);
        }

        static void Triangle(bool[] buffer, int res, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 pa = a * res, pb = b * res, pc = c * res;
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))), 0, res - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x))), 0, res - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))), 0, res - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y))), 0, res - 1);

            float area = Edge(pa, pb, pc);
            if (Mathf.Abs(area) < 1e-9f)
            {
                // Degenerate in UV space: fill the (tiny) bounding box rather than drop it.
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++) buffer[y * res + x] = true;
                return;
            }

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Edge(pb, pc, p) / area;
                float w1 = Edge(pc, pa, p) / area;
                float w2 = Edge(pa, pb, p) / area;
                // Slight negative tolerance so edge texels count as covered.
                if (w0 >= -0.001f && w1 >= -0.001f && w2 >= -0.001f) buffer[y * res + x] = true;
            }
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
    }
}
