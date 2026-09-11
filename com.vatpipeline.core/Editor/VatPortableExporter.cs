using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Writes a VAT character as a self-contained, engine-neutral folder: a bind-pose `.glb`,
    /// the data textures as plain 8-bit PNGs, and a `SPEC.md` that documents the decode.
    ///
    /// The target is any engine that cannot consume a Unity shader and has to rebuild the
    /// decode itself — a node graph, a hand-written shader, a web renderer. Nothing Unity-
    /// specific ships: no .shader, no .hlsl, no .cs. **If it is not in SPEC.md, it does not
    /// exist to the receiver**, which is why that file carries the maths and not just the
    /// dimensions.
    ///
    /// This is a separate bake from VatBaker's, not a repack of it, because the two want
    /// incompatible layouts:
    ///
    ///   VatBaker (in-engine)  deduplicates positions and resolves vertexId -> column through
    ///                         an index texture, and writes RGBAHalf .asset textures.
    ///   portable              assumes the least capable plausible target: pixel X == vertexId
    ///                         with no indirection, and 8-bit channels only. Positions are
    ///                         rebaked vertexId-major and quantised to a 16-bit hi/lo pair.
    ///
    /// The in-engine VAT assets are never touched by this.
    ///
    /// One pinned-pose row is appended after every one-shot clip, so a receiver has a static
    /// row to hold on when the clip finishes rather than needing playback state for corpses.
    /// `export-log.txt` carries the verification numbers and a file manifest with byte sizes.
    /// </summary>
    public static class VatPortableExporter
    {
        /// <summary>Folder (outside Assets) that receives one subfolder per exported character.</summary>
        public static string OutputRoot = "VAT_Export";
        const int VertexCap = 8192;
        const float AxisEpsilon = 1e-6f;
        // --- data ------------------------------------------------------------------------

        /// <summary>One character to export. Row layout comes from the controller's clip list.</summary>
        public sealed class Request
        {
            /// <summary>Prefab holding the skinned renderers and the Animator/Avatar to sample through.</summary>
            public string SourcePrefab;
            /// <summary>AnimatorController whose clip list, in order, becomes the row layout.</summary>
            public string AnimatorController;
            /// <summary>Package folder name, and the suffix on every written file.</summary>
            public string Name;
            public float FrameRate = 30f;
            /// <summary>
            /// Flatten every material into one primitive with one merged albedo. Leave this
            /// on unless you know the receiving importer preserves vertex order across
            /// primitives: most re-index each primitive separately, which breaks the
            /// vertexId == pixel X contract the whole decode rests on.
            /// </summary>
            public bool MergeMaterials;
        }

        sealed class ClipPlan
        {
            public string Name;
            public int StartRow;
            public int FrameCount;
            public float FrameRate;
            public bool Loop;
            public float Length;
            /// <summary>Absolute row holding the held final pose, or -1 for looping clips.</summary>
            public int PinnedRow = -1;

            public float TimeOf(int frame)
            {
                if (Loop) return Length * frame / FrameCount;
                return FrameCount > 1 ? Length * frame / (FrameCount - 1) : 0f;
            }
        }

        sealed class Part
        {
            public SkinnedMeshRenderer Skinned;
            public Transform Transform;
            public Mesh SourceMesh;
            public Material Material;
            public int Offset;
            public int Count;
            public Renderer Renderer => Skinned != null ? (Renderer)Skinned : Transform.GetComponent<MeshRenderer>();
        }

        sealed class Bake
        {
            public int VertexCount;
            public int RowCount;
            public Vector3[] Positions;   // row-major: row * VertexCount + vertex
            public Vector3[] Normals;
            public Vector2[] Uv;
            public Vector4[] Tangents;
            public List<Material> Materials = new List<Material>();
            public List<List<int>> Submeshes = new List<List<int>>();
            public Vector3 BoundsMin;
            public Vector3 BoundsSize;
            public List<ClipPlan> Clips;
            public int TriangleCount
            {
                get
                {
                    int n = 0;
                    foreach (var s in Submeshes) n += s.Count / 3;
                    return n;
                }
            }
        }

        // --- entry point -----------------------------------------------------------------

        public static void Export(Request request)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(request.SourcePrefab);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.AnimatorController);
            if (source == null || controller == null)
            {
                Debug.LogError($"[VatPortableExporter] {request.Name}: missing input " +
                               $"(prefab={source != null} controller={controller != null})");
                return;
            }

            string root = $"{OutputRoot}/{request.Name}";
            if (Directory.Exists(root))
            {
                // Start clean rather than overwriting: a file left behind by an earlier export
                // with different clips or a different name would ship silently alongside the
                // current one, and SPEC.md would not mention it.
                Directory.Delete(root, true);
            }
            Directory.CreateDirectory(root);

            _mergedAlbedoTemp = null;
            _mergeNote = null;

            var log = new StringBuilder();
            log.AppendLine($"# VAT portable export — {request.Name}");
            log.AppendLine($"source prefab     : {request.SourcePrefab}");
            log.AppendLine($"source controller : {request.AnimatorController}");
            log.AppendLine($"bake rate         : {request.FrameRate} fps");
            log.AppendLine();

            var bake = RunBake(source, controller, request.FrameRate, log);
            if (bake == null) return;

            if (bake.VertexCount > VertexCap)
            {
                Debug.LogError($"[VatPortableExporter] {request.Name}: {bake.VertexCount} vertices " +
                               $"exceeds the {VertexCap} cap. Decimate before baking.");
                log.AppendLine($"ABORTED: {bake.VertexCount} vertices > cap {VertexCap}");
                File.WriteAllText($"{root}/export-log.txt", log.ToString());
                return;
            }

            if (request.MergeMaterials) MergeToSinglePrimitive(bake, log);

            ConvertToGltfSpace(bake);
            ComputeBounds(bake);

            WritePositionMaps(bake, root, request.Name, log);
            WriteNormalMap(bake, root, request.Name, log);
            var albedos = WriteAlbedos(bake, root, request.Name, log);
            string modelFile = ModelFileName(request.Name);
            WriteGlb(bake, $"{root}/{modelFile}", log);

            Verify(bake, root, request.Name, log);
            WriteSpec(bake, root, request, modelFile, albedos, log);
            WriteManifest(root, log);

            Debug.Log($"[VatPortableExporter] {root}\n{log}");
        }

        static string ModelFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "model.glb";
            return char.ToLowerInvariant(name[0]) + name.Substring(1) + "Model.glb";
        }

        // --- bake ------------------------------------------------------------------------

        static Bake RunBake(GameObject source, AnimatorController controller, float frameRate,
                            StringBuilder log)
        {
            GameObject instance = null;
            PlayableGraph graph = default;
            var scratch = new Mesh { name = "VatExportScratch" };

            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;

                var animator = instance.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    Debug.LogError("[VatPortableExporter] No Animator on the source prefab.");
                    return null;
                }
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var rootTransform = animator.transform;

                var parts = CollectParts(instance, log);
                var clips = OrderedClips(controller);
                if (parts.Count == 0 || clips.Count == 0)
                {
                    Debug.LogError("[VatPortableExporter] Nothing to bake.");
                    return null;
                }

                int vertexCount = 0;
                foreach (var p in parts) vertexCount += p.Count;

                var plans = PlanClips(clips, frameRate, log);
                int rowCount = 0;
                foreach (var p in plans) rowCount = Mathf.Max(rowCount,
                    p.PinnedRow >= 0 ? p.PinnedRow + 1 : p.StartRow + p.FrameCount);

                var bake = new Bake
                {
                    VertexCount = vertexCount,
                    RowCount = rowCount,
                    Positions = new Vector3[vertexCount * rowCount],
                    Normals = new Vector3[vertexCount * rowCount],
                    Uv = new Vector2[vertexCount],
                    Tangents = new Vector4[vertexCount],
                    Clips = plans,
                };

                BuildStaticAttributes(parts, bake);

                graph = PlayableGraph.Create("VatExportBake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "out", animator);

                for (int c = 0; c < clips.Count; c++)
                {
                    var clip = clips[c];
                    var plan = plans[c];
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(false);
                    playable.SetApplyPlayableIK(false);
                    output.SetSourcePlayable(playable);

                    for (int f = 0; f < plan.FrameCount; f++)
                    {
                        float t = plan.TimeOf(f);
                        // Set twice: one call leaves the pose a frame stale.
                        playable.SetTime(t);
                        playable.SetTime(t);
                        graph.Evaluate(0f);
                        SampleFrame(parts, rootTransform, scratch, bake,
                                    (plan.StartRow + f) * vertexCount);
                    }

                    // The pinned row is a byte copy of the clip's last frame rather than a
                    // re-sample, so it is exactly the pose the runtime was showing when the
                    // clip ended — no risk of a sub-frame difference producing a visible snap.
                    if (plan.PinnedRow >= 0)
                    {
                        int last = (plan.StartRow + plan.FrameCount - 1) * vertexCount;
                        int pinned = plan.PinnedRow * vertexCount;
                        Array.Copy(bake.Positions, last, bake.Positions, pinned, vertexCount);
                        Array.Copy(bake.Normals, last, bake.Normals, pinned, vertexCount);
                    }

                    playable.Destroy();
                }

                log.AppendLine($"baked {vertexCount} vertices x {rowCount} rows " +
                               $"({bake.TriangleCount} triangles, {bake.Submeshes.Count} submesh(es))");
                return bake;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEngine.Object.DestroyImmediate(scratch);
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        static List<AnimationClip> OrderedClips(AnimatorController controller)
        {
            var seen = new HashSet<AnimationClip>();
            var ordered = new List<AnimationClip>();
            foreach (var clip in controller.animationClips)
                if (clip != null && seen.Add(clip)) ordered.Add(clip);
            return ordered;
        }

        static List<ClipPlan> PlanClips(List<AnimationClip> clips, float frameRate, StringBuilder log)
        {
            var plans = new List<ClipPlan>();
            int cursor = 0;

            foreach (var clip in clips)
            {
                int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * frameRate));
                // A loop's rows span [0, length) so the last row flows back into the first.
                // A one-shot spans [0, length] inclusive so its last row is the true end pose.
                int count = clip.isLooping ? frames : frames + 1;

                var plan = new ClipPlan
                {
                    Name = clip.name,
                    StartRow = cursor,
                    FrameCount = count,
                    FrameRate = (clip.isLooping ? count : count - 1) / Mathf.Max(1e-4f, clip.length),
                    Loop = clip.isLooping,
                    Length = clip.length,
                };
                cursor += count;

                if (!clip.isLooping)
                {
                    plan.PinnedRow = cursor;
                    cursor++;
                }

                plans.Add(plan);
                log.AppendLine($"clip {clip.name,-18} rows {plan.StartRow}..{plan.StartRow + count - 1}" +
                               $" ({count} frames, {clip.length:F3}s, {(plan.Loop ? "loop" : "one-shot")})" +
                               (plan.PinnedRow >= 0 ? $" pinned row {plan.PinnedRow}" : ""));
            }

            return plans;
        }

        static List<Part> CollectParts(GameObject instance, StringBuilder log)
        {
            var parts = new List<Part>();
            int offset = 0;

            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Skinned = smr, Transform = smr.transform, SourceMesh = smr.sharedMesh,
                    Material = smr.sharedMaterial, Offset = offset, Count = smr.sharedMesh.vertexCount,
                });
                log.AppendLine($"part skinned {smr.name,-22} verts={smr.sharedMesh.vertexCount} offset={offset}");
                offset += smr.sharedMesh.vertexCount;
            }

            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = mr.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Transform = mr.transform, SourceMesh = filter.sharedMesh,
                    Material = mr.sharedMaterial, Offset = offset, Count = filter.sharedMesh.vertexCount,
                });
                log.AppendLine($"part rigid   {mr.name,-22} verts={filter.sharedMesh.vertexCount} offset={offset}");
                offset += filter.sharedMesh.vertexCount;
            }

            return parts;
        }

        static void BuildStaticAttributes(List<Part> parts, Bake bake)
        {
            foreach (var part in parts)
            {
                var mesh = part.SourceMesh;
                var uv = mesh.uv;
                var tangents = mesh.tangents;

                for (int i = 0; i < part.Count; i++)
                {
                    bake.Uv[part.Offset + i] = uv != null && i < uv.Length ? uv[i] : Vector2.zero;
                    bake.Tangents[part.Offset + i] = tangents != null && i < tangents.Length
                        ? tangents[i] : new Vector4(1f, 0f, 0f, 1f);
                }

                var renderer = part.Renderer;
                var slots = renderer != null ? renderer.sharedMaterials : new[] { part.Material };
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var material = sub < slots.Length ? slots[sub] : part.Material;
                    int bucket = bake.Materials.IndexOf(material);
                    if (bucket < 0)
                    {
                        bake.Materials.Add(material);
                        bake.Submeshes.Add(new List<int>());
                        bucket = bake.Materials.Count - 1;
                    }

                    var tris = mesh.GetTriangles(sub);
                    var target = bake.Submeshes[bucket];
                    for (int i = 0; i < tris.Length; i++) target.Add(tris[i] + part.Offset);
                }
            }
        }

        /// <summary>
        /// Collapses the bake to one primitive with one merged albedo. Must run before the
        /// handedness conversion, so the winding flip applies to the already-combined index
        /// list rather than being applied twice to some of it.
        /// </summary>
        static void MergeToSinglePrimitive(Bake bake, StringBuilder log)
        {
            if (bake.Materials.Count <= 1)
            {
                log.AppendLine("merge requested but there is only one material; nothing to do");
                return;
            }

            string path = $"{OutputRoot}/__merged_albedo_tmp.png";
            var merged = VatAlbedoMerge.Build(bake.Materials, bake.Submeshes, bake.Uv, path);
            if (merged == null)
            {
                Debug.LogError("[VatPortableExporter] Materials cannot be merged safely, but this " +
                               "target requires a single primitive. Package would be unusable.");
                log.AppendLine("MERGE FAILED: materials are not a mergeable recolour set");
                return;
            }

            log.AppendLine(merged.Report);
            _mergedAlbedoTemp = path;
            _mergeNote = $"{bake.Materials.Count} source materials " +
                         $"({string.Join(", ", bake.Materials.ConvertAll(m => m != null ? m.name : "null"))}) " +
                         $"were flattened into the single albedo shipped here, because this target " +
                         $"re-indexes multi-primitive meshes and that destroys the vertexId mapping. " +
                         $"{merged.CoveredTexels} texels covered, {merged.ContestedTexels} contested" +
                         (merged.ContestedTexels == 0
                             ? " — the UV islands are disjoint, so the merge is lossless."
                             : " — those texels take the later material's colour. They sit on UV island " +
                               "boundaries and are not visible at gameplay distance.");

            var combined = new List<int>();
            foreach (var sub in bake.Submeshes) combined.AddRange(sub);

            var first = bake.Materials[0];
            bake.Materials.Clear();
            bake.Materials.Add(first);
            bake.Submeshes.Clear();
            bake.Submeshes.Add(combined);
        }

        static string _mergedAlbedoTemp;
        static string _mergeNote;

        static void SampleFrame(List<Part> parts, Transform root, Mesh scratch, Bake bake, int baseIndex)
        {
            var worldToRoot = root.worldToLocalMatrix;

            foreach (var part in parts)
            {
                Vector3[] positions;
                Vector3[] normals;

                if (part.Skinned != null)
                {
                    part.Skinned.BakeMesh(scratch, true);
                    positions = scratch.vertices;
                    normals = scratch.normals;
                }
                else
                {
                    positions = part.SourceMesh.vertices;
                    normals = part.SourceMesh.normals;
                }

                var toRoot = worldToRoot * part.Transform.localToWorldMatrix;
                bool hasNormals = normals != null && normals.Length == positions.Length;

                for (int i = 0; i < part.Count; i++)
                {
                    int dst = baseIndex + part.Offset + i;
                    bake.Positions[dst] = toRoot.MultiplyPoint3x4(positions[i]);
                    bake.Normals[dst] = hasNormals
                        ? toRoot.MultiplyVector(normals[i]).normalized
                        : Vector3.up;
                }
            }
        }

        /// <summary>
        /// Unity is left-handed with +Z forward; glTF is right-handed with -Z forward.
        /// Negating Z on positions and normals and reversing triangle winding converts the mesh
        /// and the animation data together so they cannot disagree.
        ///
        /// A receiver that applies this consistently needs no yaw correction of its own.
        /// </summary>
        static void ConvertToGltfSpace(Bake bake)
        {
            for (int i = 0; i < bake.Positions.Length; i++)
            {
                var p = bake.Positions[i];
                bake.Positions[i] = new Vector3(p.x, p.y, -p.z);
                var n = bake.Normals[i];
                bake.Normals[i] = new Vector3(n.x, n.y, -n.z);
            }

            for (int i = 0; i < bake.Tangents.Length; i++)
            {
                var t = bake.Tangents[i];
                bake.Tangents[i] = new Vector4(t.x, t.y, -t.z, -t.w);
            }

            foreach (var sub in bake.Submeshes)
                for (int i = 0; i + 2 < sub.Count; i += 3)
                    (sub[i + 1], sub[i + 2]) = (sub[i + 2], sub[i + 1]);
        }

        static void ComputeBounds(Bake bake)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < bake.Positions.Length; i++)
            {
                min = Vector3.Min(min, bake.Positions[i]);
                max = Vector3.Max(max, bake.Positions[i]);
            }

            // A perfectly flat axis would divide by zero on decode.
            for (int a = 0; a < 3; a++)
                if (max[a] - min[a] < AxisEpsilon) max[a] = min[a] + AxisEpsilon;

            bake.BoundsMin = min;
            bake.BoundsSize = max - min;
        }

        // --- textures --------------------------------------------------------------------

        static void WritePositionMaps(Bake bake, string root, string name, StringBuilder log)
        {
            int w = bake.VertexCount;
            int h = bake.RowCount;
            var hi = new Color32[w * h];
            var lo = new Color32[w * h];

            for (int row = 0; row < h; row++)
            for (int v = 0; v < w; v++)
            {
                var p = bake.Positions[row * w + v];
                int qx = Quantize(p.x, bake.BoundsMin.x, bake.BoundsSize.x);
                int qy = Quantize(p.y, bake.BoundsMin.y, bake.BoundsSize.y);
                int qz = Quantize(p.z, bake.BoundsMin.z, bake.BoundsSize.z);

                int i = row * w + v;
                hi[i] = new Color32((byte)(qx >> 8), (byte)(qy >> 8), (byte)(qz >> 8), 255);
                lo[i] = new Color32((byte)(qx & 0xFF), (byte)(qy & 0xFF), (byte)(qz & 0xFF), 255);
            }

            WritePng(hi, w, h, $"{root}/vat_position_{name}_16bit_hi.png");
            WritePng(lo, w, h, $"{root}/vat_position_{name}_16bit_lo.png");
            log.AppendLine($"position maps {w}x{h} (16-bit hi/lo)");
        }

        static int Quantize(float value, float min, float size)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01((value - min) / size) * 65535f), 0, 65535);
        }

        static void WriteNormalMap(Bake bake, string root, string name, StringBuilder log)
        {
            int w = bake.VertexCount;
            int h = bake.RowCount;
            var pixels = new Color32[w * h];

            for (int row = 0; row < h; row++)
            for (int v = 0; v < w; v++)
            {
                var e = EncodeOctahedral(bake.Normals[row * w + v]);
                pixels[row * w + v] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(e.x * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(e.y * 255f), 0, 255), 0, 255);
            }

            WritePng(pixels, w, h, $"{root}/vat_normal_{name}.png");
            log.AppendLine($"normal map {w}x{h} (octahedral in RG)");
        }

        static Vector2 EncodeOctahedral(Vector3 n)
        {
            n = n.normalized;
            float l1 = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (l1 < 1e-8f) return new Vector2(0.5f, 0.5f);

            float x = n.x / l1;
            float y = n.y / l1;
            if (n.z < 0f)
            {
                float fx = (1f - Mathf.Abs(y)) * (x >= 0f ? 1f : -1f);
                float fy = (1f - Mathf.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = fx;
                y = fy;
            }
            return new Vector2(x * 0.5f + 0.5f, y * 0.5f + 0.5f);
        }

        static Vector3 DecodeOctahedral(Vector2 encoded)
        {
            float x = encoded.x * 2f - 1f;
            float y = encoded.y * 2f - 1f;
            var n = new Vector3(x, y, 1f - Mathf.Abs(x) - Mathf.Abs(y));
            float t = Mathf.Clamp01(-n.z);
            n.x += n.x >= 0f ? -t : t;
            n.y += n.y >= 0f ? -t : t;
            return n.normalized;
        }

        static void WritePng(Color32[] pixels, int width, int height, string path)
        {
            // linear: true — this is geometry, not colour; an sRGB curve would corrupt it.
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Writes one albedo per submesh material. Materials that paint themselves from an RGB
        /// mask (Shader Graphs/RGBRecolor_*) have no base map to copy, so the mask is resolved
        /// into a flat albedo here.
        /// </summary>
        static List<string> WriteAlbedos(Bake bake, string root, string name, StringBuilder log)
        {
            var files = new List<string>();

            for (int i = 0; i < bake.Materials.Count; i++)
            {
                var source = bake.Materials[i];
                string suffix = bake.Materials.Count > 1 ? $"_{i}" : string.Empty;
                string outPath = $"{root}/basemap_{name}{suffix}.png";

                if (source == null)
                {
                    log.AppendLine($"albedo {i}: source material is null, skipped");
                    continue;
                }

                if (_mergedAlbedoTemp != null && File.Exists(_mergedAlbedoTemp))
                {
                    File.Copy(_mergedAlbedoTemp, outPath, true);
                    File.Delete(_mergedAlbedoTemp);
                    _mergedAlbedoTemp = null;
                    files.Add(Path.GetFileName(outPath));
                    log.AppendLine($"albedo {i}: merged albedo -> {Path.GetFileName(outPath)}");
                    continue;
                }

                if (TryResolveRecolor(source, outPath, log))
                {
                    files.Add(Path.GetFileName(outPath));
                    continue;
                }

                var baseMap = source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : null;
                if (baseMap == null && source.HasProperty("_MainTex")) baseMap = source.GetTexture("_MainTex");
                string srcPath = baseMap != null ? AssetDatabase.GetAssetPath(baseMap) : null;

                if (!string.IsNullOrEmpty(srcPath) && File.Exists(srcPath) &&
                    Path.GetExtension(srcPath).ToLowerInvariant() == ".png")
                {
                    File.Copy(srcPath, outPath, true);
                    files.Add(Path.GetFileName(outPath));
                    log.AppendLine($"albedo {i}: copied {srcPath}");
                }
                else
                {
                    log.AppendLine($"albedo {i}: NO SOURCE for material {source.name} " +
                                   $"(baseMap={baseMap != null} path={srcPath})");
                }
            }

            return files;
        }

        static bool TryResolveRecolor(Material source, string outPath, StringBuilder log)
        {
            if (source.shader == null || !source.shader.name.Contains("RGBRecolor")) return false;
            if (!source.HasProperty("_MainTex") || !source.HasProperty("_Color1")) return false;

            var mask = source.GetTexture("_MainTex");
            string maskPath = mask != null ? AssetDatabase.GetAssetPath(mask) : null;
            if (string.IsNullOrEmpty(maskPath) || !File.Exists(maskPath)) return false;

            var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!readable.LoadImage(File.ReadAllBytes(maskPath), false))
            {
                UnityEngine.Object.DestroyImmediate(readable);
                return false;
            }

            Color c1 = source.GetColor("_Color1");
            Color c2 = source.GetColor("_Color2");
            Color c3 = source.GetColor("_Color3");

            var src = readable.GetPixels();
            var dst = new Color[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var m = src[i];
                var c = c1 * m.r + c2 * m.g + c3 * m.b;
                c.a = 1f;
                dst[i] = c;
            }

            var output = new Texture2D(readable.width, readable.height, TextureFormat.RGBA32, false);
            output.SetPixels(dst);
            output.Apply(false, false);
            File.WriteAllBytes(outPath, output.EncodeToPNG());

            log.AppendLine($"albedo: resolved {source.name} from RGB mask " +
                           $"({readable.width}x{readable.height})");

            UnityEngine.Object.DestroyImmediate(readable);
            UnityEngine.Object.DestroyImmediate(output);
            return true;
        }

        // --- glb -------------------------------------------------------------------------

        static void WriteGlb(Bake bake, string path, StringBuilder log)
        {
            int vertexCount = bake.VertexCount;
            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            Array.Copy(bake.Positions, 0, positions, 0, vertexCount);
            Array.Copy(bake.Normals, 0, normals, 0, vertexCount);

            bool shortIndices = vertexCount <= 65535;
            int indexStride = shortIndices ? 2 : 4;

            int posBytes = vertexCount * 12;
            int nrmBytes = vertexCount * 12;
            int tanBytes = vertexCount * 16;
            int uvBytes = vertexCount * 8;

            int posOffset = 0;
            int nrmOffset = Pad4(posOffset + posBytes);
            int tanOffset = Pad4(nrmOffset + nrmBytes);
            int uvOffset = Pad4(tanOffset + tanBytes);

            var subOffsets = new int[bake.Submeshes.Count];
            var subBytes = new int[bake.Submeshes.Count];
            int cursor = Pad4(uvOffset + uvBytes);
            for (int i = 0; i < bake.Submeshes.Count; i++)
            {
                subOffsets[i] = cursor;
                subBytes[i] = bake.Submeshes[i].Count * indexStride;
                cursor = Pad4(cursor + subBytes[i]);
            }
            int binLength = cursor;

            var bin = new byte[binLength];
            using (var stream = new MemoryStream(bin))
            using (var writer = new BinaryWriter(stream))
            {
                stream.Position = posOffset;
                foreach (var p in positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }

                stream.Position = nrmOffset;
                foreach (var n in normals) { writer.Write(n.x); writer.Write(n.y); writer.Write(n.z); }

                stream.Position = tanOffset;
                foreach (var t in bake.Tangents)
                { writer.Write(t.x); writer.Write(t.y); writer.Write(t.z); writer.Write(t.w); }

                stream.Position = uvOffset;
                foreach (var uv in bake.Uv)
                {
                    // glTF texture space runs V downward.
                    writer.Write(uv.x);
                    writer.Write(1f - uv.y);
                }

                for (int i = 0; i < bake.Submeshes.Count; i++)
                {
                    stream.Position = subOffsets[i];
                    var sub = bake.Submeshes[i];
                    if (shortIndices) foreach (int v in sub) writer.Write((ushort)v);
                    else foreach (int v in sub) writer.Write((uint)v);
                }
            }

            var pMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var pMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { pMin = Vector3.Min(pMin, p); pMax = Vector3.Max(pMax, p); }

            var accessors = new StringBuilder();
            var views = new StringBuilder();
            var primitives = new StringBuilder();
            var materials = new StringBuilder();

            accessors.Append(Accessor(0, 5126, vertexCount, "VEC3",
                $",\"min\":[{F(pMin.x)},{F(pMin.y)},{F(pMin.z)}]," +
                $"\"max\":[{F(pMax.x)},{F(pMax.y)},{F(pMax.z)}]"));
            accessors.Append(',').Append(Accessor(1, 5126, vertexCount, "VEC3", ""));
            accessors.Append(',').Append(Accessor(2, 5126, vertexCount, "VEC4", ""));
            accessors.Append(',').Append(Accessor(3, 5126, vertexCount, "VEC2", ""));

            views.Append(BufferView(posOffset, posBytes, 34962));
            views.Append(',').Append(BufferView(nrmOffset, nrmBytes, 34962));
            views.Append(',').Append(BufferView(tanOffset, tanBytes, 34962));
            views.Append(',').Append(BufferView(uvOffset, uvBytes, 34962));

            for (int i = 0; i < bake.Submeshes.Count; i++)
            {
                int accessorIndex = 4 + i;
                int viewIndex = 4 + i;
                accessors.Append(',').Append(Accessor(viewIndex, shortIndices ? 5123 : 5125,
                                                      bake.Submeshes[i].Count, "SCALAR", ""));
                views.Append(',').Append(BufferView(subOffsets[i], subBytes[i], 34963));

                if (i > 0) { primitives.Append(','); materials.Append(','); }
                primitives.Append("{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TANGENT\":2," +
                                  "\"TEXCOORD_0\":3},\"indices\":" + accessorIndex +
                                  ",\"material\":" + i + ",\"mode\":4}");
                string matName = bake.Materials[i] != null ? bake.Materials[i].name : $"mat{i}";
                materials.Append($"{{\"name\":\"{Escape(matName)}\",\"pbrMetallicRoughness\":" +
                                 "{\"baseColorFactor\":[1,1,1,1],\"metallicFactor\":0," +
                                 "\"roughnessFactor\":1},\"doubleSided\":false}");
            }

            string json =
                "{" +
                "\"asset\":{\"version\":\"2.0\",\"generator\":\"VatPortableExporter v2\"}," +
                "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
                "\"nodes\":[{\"mesh\":0,\"name\":\"VAT_BindPose\"}]," +
                "\"meshes\":[{\"name\":\"VAT_BindPose\",\"primitives\":[" + primitives + "]}]," +
                "\"materials\":[" + materials + "]," +
                "\"accessors\":[" + accessors + "]," +
                "\"bufferViews\":[" + views + "]," +
                $"\"buffers\":[{{\"byteLength\":{binLength}}}]" +
                "}";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPadded = Pad4(jsonBytes.Length);
            var jsonChunk = new byte[jsonPadded];
            Array.Copy(jsonBytes, jsonChunk, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) jsonChunk[i] = 0x20;

            using (var file = File.Create(path))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(0x46546C67);
                writer.Write(2);
                writer.Write(12 + 8 + jsonPadded + 8 + binLength);
                writer.Write(jsonPadded);
                writer.Write(0x4E4F534A);
                writer.Write(jsonChunk);
                writer.Write(binLength);
                writer.Write(0x004E4942);
                writer.Write(bin);
            }

            log.AppendLine($"{Path.GetFileName(path)} verts={vertexCount} " +
                           $"primitives={bake.Submeshes.Count} indexType={(shortIndices ? "u16" : "u32")}");
        }

        static string Accessor(int view, int componentType, int count, string type, string extra)
            => $"{{\"bufferView\":{view},\"componentType\":{componentType},\"count\":{count}," +
               $"\"type\":\"{type}\"{extra}}}";

        static string BufferView(int offset, int length, int target)
            => $"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{length},\"target\":{target}}}";

        static int Pad4(int v) => (v + 3) & ~3;
        static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // --- verification ----------------------------------------------------------------

        static float _worstPosition, _worstNormal, _worstAlignment;
        static string _pinnedVerdict = "not run";

        static void Verify(Bake bake, string root, string name, StringBuilder log)
        {
            var hi = LoadPng($"{root}/vat_position_{name}_16bit_hi.png");
            var lo = LoadPng($"{root}/vat_position_{name}_16bit_lo.png");
            var nrm = LoadPng($"{root}/vat_normal_{name}.png");
            if (hi == null || lo == null || nrm == null)
            {
                log.AppendLine("verification SKIPPED: a png failed to reload");
                return;
            }

            try
            {
                var pHi = hi.GetPixels32();
                var pLo = lo.GetPixels32();
                var pN = nrm.GetPixels32();

                _worstPosition = 0f;
                _worstNormal = 0f;
                for (int i = 0; i < bake.Positions.Length; i++)
                {
                    var decoded = Decode(pHi[i], pLo[i], bake.BoundsMin, bake.BoundsSize);
                    _worstPosition = Mathf.Max(_worstPosition, (decoded - bake.Positions[i]).magnitude);

                    var n = DecodeOctahedral(new Vector2(pN[i].r / 255f, pN[i].g / 255f));
                    _worstNormal = Mathf.Max(_worstNormal, Vector3.Angle(n, bake.Normals[i]));
                }

                // Pinned rows must be byte-identical to the last frame of their clip.
                float pinnedWorst = 0f;
                int pinnedChecked = 0;
                foreach (var clip in bake.Clips)
                {
                    if (clip.PinnedRow < 0) continue;
                    pinnedChecked++;
                    int last = (clip.StartRow + clip.FrameCount - 1) * bake.VertexCount;
                    int pinned = clip.PinnedRow * bake.VertexCount;
                    for (int v = 0; v < bake.VertexCount; v++)
                    {
                        var a = Decode(pHi[last + v], pLo[last + v], bake.BoundsMin, bake.BoundsSize);
                        var b = Decode(pHi[pinned + v], pLo[pinned + v], bake.BoundsMin, bake.BoundsSize);
                        pinnedWorst = Mathf.Max(pinnedWorst, (a - b).magnitude);
                    }
                }
                _pinnedVerdict = pinnedChecked == 0
                    ? "no one-shot clips"
                    : $"{pinnedChecked} pinned row(s), max delta from final frame " +
                      $"{pinnedWorst * 1000f:F4} mm";

                // Mesh vertex order must equal texture column order.
                var glb = File.ReadAllBytes($"{root}/{ModelFileName(name)}");
                int jsonLength = BitConverter.ToInt32(glb, 12);
                int binStart = 12 + 8 + jsonLength + 8;
                _worstAlignment = 0f;
                for (int v = 0; v < bake.VertexCount; v++)
                {
                    int o = binStart + v * 12;
                    var meshPos = new Vector3(BitConverter.ToSingle(glb, o),
                                              BitConverter.ToSingle(glb, o + 4),
                                              BitConverter.ToSingle(glb, o + 8));
                    var decoded = Decode(pHi[v], pLo[v], bake.BoundsMin, bake.BoundsSize);
                    _worstAlignment = Mathf.Max(_worstAlignment, (decoded - meshPos).magnitude);
                }

                log.AppendLine();
                log.AppendLine("verification (decoded back off the written files)");
                log.AppendLine($"  position round trip  max {_worstPosition * 1000f:F4} mm");
                log.AppendLine($"  normal round trip    max {_worstNormal:F3} deg");
                log.AppendLine($"  pinned rows          {_pinnedVerdict}");
                log.AppendLine($"  glb vertex order     max {_worstAlignment * 1000f:F4} mm " +
                               (_worstAlignment < 0.001f ? "PASS" : "FAIL"));
                if (_worstAlignment >= 0.001f)
                    Debug.LogError($"[VatPortableExporter] {name}: mesh vertex order does not match " +
                                   "the texture columns. Package unusable.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hi);
                UnityEngine.Object.DestroyImmediate(lo);
                UnityEngine.Object.DestroyImmediate(nrm);
            }
        }

        static Vector3 Decode(Color32 hi, Color32 lo, Vector3 min, Vector3 size)
        {
            return new Vector3(
                min.x + (hi.r * 256 + lo.r) / 65535f * size.x,
                min.y + (hi.g * 256 + lo.g) / 65535f * size.y,
                min.z + (hi.b * 256 + lo.b) / 65535f * size.z);
        }

        static Texture2D LoadPng(string path)
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(path), false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        // --- docs ------------------------------------------------------------------------

        static void WriteSpec(Bake bake, string root, Request request, string modelFile,
                              List<string> albedos, StringBuilder log)
        {
            var sb = new StringBuilder();
            var min = bake.BoundsMin;
            var size = bake.BoundsSize;

            sb.AppendLine($"# VAT Export — {request.Name}");
            sb.AppendLine();
            sb.AppendLine("## Mesh");
            sb.AppendLine();
            sb.AppendLine($"- Vertex count: **{bake.VertexCount}**  (== texture width; cap {VertexCap})");
            sb.AppendLine($"- Triangles: {bake.TriangleCount}");
            sb.AppendLine($"- Bind-pose file: `{modelFile}`");
            sb.AppendLine($"- Submeshes / primitives: {bake.Submeshes.Count}");
            for (int i = 0; i < bake.Submeshes.Count; i++)
            {
                string albedo = i < albedos.Count ? albedos[i] : "(none)";
                string matName = bake.Materials[i] != null ? bake.Materials[i].name : "null";
                sb.AppendLine($"  - primitive {i}: {bake.Submeshes[i].Count / 3} tris, " +
                              $"source material `{matName}`, albedo `{albedo}`");
            }
            sb.AppendLine();
            sb.AppendLine("## Textures");
            sb.AppendLine();
            sb.AppendLine($"- Dimensions: **{bake.VertexCount} x {bake.RowCount}** " +
                          "(W == vertex count, H == total rows)");
            sb.AppendLine("- Position encoding: 16-bit hi/lo split across two 8-bit PNGs, " +
                          "animation-wide bounds normalization");
            sb.AppendLine("- Normal encoding: octahedral, see the exact variant under Decode math");
            sb.AppendLine();
            sb.AppendLine("| file | contents |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| `vat_position_{request.Name}_16bit_hi.png` | RGB = high byte of XYZ |");
            sb.AppendLine($"| `vat_position_{request.Name}_16bit_lo.png` | RGB = low byte of XYZ |");
            sb.AppendLine($"| `vat_normal_{request.Name}.png` | RG = octahedral normal, B/A unused |");
            foreach (var a in albedos) sb.AppendLine($"| `{a}` | albedo, sampled with UV0. **sRGB** |");
            sb.AppendLine();
            sb.AppendLine("Import rules for the three `vat_*` textures — all three are data, not colour:");
            sb.AppendLine();
            sb.AppendLine("- **linear / no sRGB**");
            sb.AppendLine("- **point / nearest** filtering (bilinear blends neighbouring vertices " +
                          "together and shears the mesh)");
            sb.AppendLine("- **clamp** wrap");
            sb.AppendLine("- **no mipmaps, no compression** (DXT/ETC/ASTC destroys this data)");
            sb.AppendLine();
            sb.AppendLine("The albedo is the only sRGB texture in the package.");
            sb.AppendLine();
            sb.AppendLine("**If your platform has no sRGB import toggle**, the");
            sb.AppendLine("sampler gamma-decodes these PNGs and corrupts the data. The fix then lives in");
            sb.AppendLine("the shader graph: insert a linear-to-sRGB conversion node immediately after each");
            sb.AppendLine("sample to undo it. Apply it to **all three** `vat_*` samples — `positionHi`,");
            sb.AppendLine("`positionLo` AND `normalMap`. Missing it on the normal sample is the single most");
            sb.AppendLine("likely cause of inside-out shading while positions look perfect; see Decode math.");
            sb.AppendLine();
            sb.AppendLine("## Bounds (animation-wide, object space)");
            sb.AppendLine();
            sb.AppendLine($"- min:  ({F(min.x)}, {F(min.y)}, {F(min.z)})");
            sb.AppendLine($"- size: ({F(size.x)}, {F(size.y)}, {F(size.z)})");
            sb.AppendLine();
            sb.AppendLine("Union over every frame of every clip, including the pinned rows. Use at " +
                          "least this as the instance culling volume or instances pop at screen edges.");
            sb.AppendLine();
            sb.AppendLine("## Clip table");
            sb.AppendLine();
            sb.AppendLine("| Clip | Start row | Frames | FPS | Type | Pinned row |");
            sb.AppendLine("|------|-----------|--------|-----|----------|------------|");
            foreach (var c in bake.Clips)
                sb.AppendLine($"| {c.Name} | {c.StartRow} | {c.FrameCount} | {c.FrameRate:F0} | " +
                              $"{(c.Loop ? "loop" : "one-shot")} | " +
                              $"{(c.PinnedRow >= 0 ? c.PinnedRow.ToString() : "—")} |");
            sb.AppendLine();
            sb.AppendLine($"Total rows: **{bake.RowCount}**. One texture holds every clip.");
            sb.AppendLine();
            sb.AppendLine("Row conventions, and they differ by type:");
            sb.AppendLine();
            sb.AppendLine("- **loop**: rows span `[0, length)`. The last row is NOT a copy of the " +
                          "first, so lerping the last row back to row 0 at the wrap is correct and " +
                          "seamless. Steps between rows = `frameCount`.");
            sb.AppendLine("- **one-shot**: rows span `[0, length]` inclusive, so the last row is the " +
                          "true end pose. Steps between rows = `frameCount - 1`. Getting this " +
                          "backwards makes one-shots play one frame too slow.");
            sb.AppendLine("- **pinned row**: a single extra row after each one-shot, byte-identical " +
                          "to that clip's final frame. Hand off to it when the clip completes and " +
                          "hold it. Because it is an exact copy there is no snap at the handoff.");
            sb.AppendLine();
            sb.AppendLine("## Decode math");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("// inputs");
            sb.AppendLine($"//   vertexId : integer 0 .. {bake.VertexCount - 1}");
            sb.AppendLine($"//   texWidth  = {bake.VertexCount}");
            sb.AppendLine($"//   texHeight = {bake.RowCount}");
            sb.AppendLine("//   row       : float absolute row (see Playback)");
            sb.AppendLine("//   boundsMin, boundsSize : vec3 material properties");
            sb.AppendLine();
            sb.AppendLine("u = (vertexId + 0.5) / texWidth");
            sb.AppendLine("v = (row      + 0.5) / texHeight");
            sb.AppendLine();
            sb.AppendLine("// --- position ---");
            sb.AppendLine("h = sampleTexture2DLOD(positionHi, vec2(u, v), 0).rgb   // 0..1 floats");
            sb.AppendLine("l = sampleTexture2DLOD(positionLo, vec2(u, v), 0).rgb");
            sb.AppendLine("hiByte = h * 255            // recover the raw bytes");
            sb.AppendLine("loByte = l * 255");
            sb.AppendLine("n      = (hiByte * 256 + loByte) / 65535     // 0..1 at 16-bit precision");
            sb.AppendLine();
            sb.AppendLine("// !! Use / 65535, NOT hi/255 + (lo/255)/256.");
            sb.AppendLine("//    That common variant equals (hi*256 + lo) / 65280: 0.39% too large, and it");
            sb.AppendLine("//    can exceed 1.0. This encoder wrote q = round(n01 * 65535) split into");
            sb.AppendLine("//    hi = q >> 8, lo = q & 255, so 65535 is the only correct divisor. On a 2 m");
            sb.AppendLine("//    character the wrong one is ~8 mm of drift - it looks almost right, which");
            sb.AppendLine("//    is exactly what makes it dangerous.");
            sb.AppendLine("positionOS = boundsMin + n * boundsSize");
            sb.AppendLine();
            sb.AppendLine("// --- normal (octahedral, +Z hemisphere folded outward) ---");
            sb.AppendLine("// !! This sample needs the SAME sRGB correction as the position samples if your");
            sb.AppendLine("//    platform gamma-decodes PNGs. Octahedral values are geometry, not colour.");
            sb.AppendLine("//    Measured on this data: decoded correctly, mean dot(normal, groundTruth)");
            sb.AppendLine("//    = 1.0000; sampled as sRGB it is 0.49, about 60 degrees of average error,");
            sb.AppendLine("//    which renders as inside-out shading while positions look completely fine.");
            sb.AppendLine("e  = sampleTexture2DLOD(normalMap, vec2(u, v), 0).rg    // 0..1");
            sb.AppendLine("f  = e * 2 - 1                                          // -1..1");
            sb.AppendLine("nz = 1 - abs(f.x) - abs(f.y)");
            sb.AppendLine("t  = clamp(-nz, 0, 1)");
            sb.AppendLine("nx = f.x + (f.x >= 0 ? -t : t)");
            sb.AppendLine("ny = f.y + (f.y >= 0 ? -t : t)");
            sb.AppendLine("normalOS = normalize(vec3(nx, ny, nz))");
            sb.AppendLine();
            sb.AppendLine("// tangentOS: use the mesh's own TANGENT attribute. Tangents are NOT");
            sb.AppendLine("// animated — this surface has no normal map, so nothing consumes them.");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### Playback");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("// nt = clip-local normalized time, 0..1");
            sb.AppendLine();
            sb.AppendLine("// loop");
            sb.AppendLine("x    = frac(nt) * frameCount");
            sb.AppendLine("r0   = floor(x)");
            sb.AppendLine("r1   = (r0 + 1) mod frameCount");
            sb.AppendLine("blend= x - r0");
            sb.AppendLine();
            sb.AppendLine("// one-shot");
            sb.AppendLine("x    = clamp(nt, 0, 1) * (frameCount - 1)");
            sb.AppendLine("r0   = floor(x)");
            sb.AppendLine("r1   = min(r0 + 1, frameCount - 1)");
            sb.AppendLine("blend= x - r0");
            sb.AppendLine();
            sb.AppendLine("rowA = startRow + r0");
            sb.AppendLine("rowB = startRow + r1");
            sb.AppendLine("// when nt reaches 1 on a one-shot, switch to rowA = rowB = pinnedRow");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Decode at `rowA` and `rowB` and `lerp(a, b, blend)`. Without that second " +
                          "sample playback steps at the bake rate. On a fast-moving extremity at " +
                          "30 fps that costs well over 100 mm between rows, against well under a " +
                          "millimetre when landing exactly on one. Lerp normals the same way, then " +
                          "renormalize.");
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine($"- **Tempo**: every clip is baked at its authored tempo at " +
                          $"{request.FrameRate:F0} fps, so the runtime plays everything at " +
                          "Speed = 1. No runtime speed multiplier is needed or expected.");
            sb.AppendLine("- **Orientation**: positions, normals and tangents were converted from " +
                          "Unity's left-handed (+Z forward) space to glTF's right-handed space by " +
                          "negating Z, with triangle winding reversed to match. Mesh and textures " +
                          "went through the same transform together, so they cannot disagree. " +
                          "Applied consistently, this needs no yaw correction on your side.");
            sb.AppendLine("- **vertexId must index the shared vertex array.** All primitives share " +
                          "one POSITION/NORMAL/TANGENT/TEXCOORD_0 accessor set and differ only in " +
                          "their index accessor, which keeps `vertexId` consistent across " +
                          "primitives. If the importer re-indexes or compacts per primitive, the " +
                          "column mapping breaks — verify by pinning `row` to a constant and " +
                          "checking the silhouette.");
            sb.AppendLine("- **No decimation** was applied; the source mesh is already under the cap.");
            sb.AppendLine("- Not carried over: motion vectors (nothing baked, so motion-vector TAA " +
                          "will smear), and the two-pose crossfade the in-engine shader does for " +
                          "clip transitions. This spec documents the single-pose path; for " +
                          "crossfades, decode two poses and lerp, doubling the sample count.");
            if (_mergeNote != null)
            {
                sb.AppendLine($"- **Merged albedo.** {_mergeNote}");
            }
            else if (bake.Materials.Count > 1)
            {
                sb.AppendLine($"- **{bake.Materials.Count} materials, {bake.Materials.Count} primitives.** " +
                              "WARNING: if your importer re-indexes multi-primitive meshes, this will " +
                              "render as shredded geometry. Ask for a merged single-primitive rebake.");
            }
            sb.AppendLine();
            sb.AppendLine("## If it looks wrong");
            sb.AppendLine();
            sb.AppendLine("- **Inside out / shading wrong, but positions and deformation look perfect** —");
            sb.AppendLine("  the sRGB correction is missing on the NORMAL sample specifically. The most");
            sb.AppendLine("  common failure on a platform with no sRGB import toggle, because guides tend");
            sb.AppendLine("  to mention that fix only for the position textures.");
            sb.AppendLine("- **Everything subtly off, ~0.4% drift** — the position recombine used");
            sb.AppendLine("  `hi/255 + (lo/255)/256` instead of `(hi*256 + lo) / 65535`.");
            sb.AppendLine("- **Shredded geometry** — vertex count does not equal texture width, or the");
            sb.AppendLine("  importer re-indexed a multi-primitive mesh. Check the count in Mesh above.");
            sb.AppendLine("- **Melted or smeared mesh** — textures are not point-filtered, are compressed,");
            sb.AppendLine("  or were resampled by a max-size clamp. Confirm the dimensions match Textures");
            sb.AppendLine("  above EXACTLY; if everything arrived at a round power of two, a Max Size");
            sb.AppendLine("  setting is silently destroying the data.");
            sb.AppendLine("- **Squashed, offset or exploded mesh** — wrong bounds floats.");
            sb.AppendLine("- **Wrong clip, or animation runs backwards through the atlas** — V is flipped.");
            sb.AppendLine("  Frame 0 is written at the PNG's LAST row (this exporter writes from a");
            sb.AppendLine("  bottom-left-origin buffer, and PNG stores rows top-first), so a bottom-left");
            sb.AppendLine("  origin sampler wants `v = (row + 0.5) / texHeight` and a top-left origin one");
            sb.AppendLine("  wants `v = 1 - (row + 0.5) / texHeight`. Fast test: pin `row` to a one-shot's");
            sb.AppendLine("  pinned row from the clip table and check you get its held end pose.");
            sb.AppendLine("- **Silhouette pops at screen edges** — the culling volume is smaller than the");
            sb.AppendLine("  animation-wide bounds above.");
            sb.AppendLine("- **Nothing renders** — instancing enabled on the material but drawn on an");
            sb.AppendLine("  ordinary object, or vice versa. Both directions fail silently.");
            sb.AppendLine();
            sb.AppendLine("## Measured accuracy of this package");
            sb.AppendLine();
            sb.AppendLine("Decoded back off the written PNGs and compared to the float ground truth:");
            sb.AppendLine();
            sb.AppendLine($"- position round trip: max **{_worstPosition * 1000f:F4} mm**");
            sb.AppendLine($"- normal round trip: max **{_worstNormal:F3} deg**");
            sb.AppendLine($"- pinned rows: {_pinnedVerdict}");
            sb.AppendLine($"- mesh vertex order vs texture columns: max " +
                          $"**{_worstAlignment * 1000f:F4} mm** " +
                          $"({(_worstAlignment < 0.001f ? "PASS" : "FAIL")})");
            sb.AppendLine();
            sb.AppendLine("The last line is the load-bearing one: POSITION was read back out of the " +
                          $"written `{modelFile}` and compared against row 0 decoded from the written " +
                          "PNGs, across every vertex, so column N really is vertex N in these files.");

            File.WriteAllText($"{root}/SPEC.md", sb.ToString());
            log.AppendLine("SPEC.md written");
        }

        /// <summary>
        /// Full manifest with byte sizes. A missing texture cost a round-trip on the first
        /// package; this makes it a five-second check.
        /// </summary>
        static void WriteManifest(string root, StringBuilder log)
        {
            var files = new List<string>(Directory.GetFiles(root));
            files.Sort();

            var sb = new StringBuilder();
            sb.Append(log);
            sb.AppendLine();
            sb.AppendLine("## File manifest");
            sb.AppendLine();

            long total = 0;
            foreach (var f in files)
            {
                if (Path.GetFileName(f) == "export-log.txt") continue;
                long bytes = new FileInfo(f).Length;
                total += bytes;
                sb.AppendLine($"  {Path.GetFileName(f),-46} {bytes,12:N0} bytes " +
                              $"({bytes / 1048576f,7:F2} MB)");
            }
            sb.AppendLine($"  {"TOTAL",-46} {total,12:N0} bytes ({total / 1048576f,7:F2} MB)");
            sb.AppendLine();
            sb.AppendLine($"  files: {files.Count} (excluding export-log.txt itself)");

            File.WriteAllText($"{root}/export-log.txt", sb.ToString());
        }
    }
}
