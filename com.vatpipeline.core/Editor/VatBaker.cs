using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VatPipeline;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Bakes a skinned character into one mesh plus Vertex Animation Textures.
    ///
    /// Collapses every SkinnedMeshRenderer and every rigid bone-parented attachment into a
    /// single boneless mesh, keeping one submesh per distinct source material, and records
    /// the animated position and normal
    /// of each vertex, per frame, into textures the vertex shader reads back. The result
    /// needs no Animator and no skinning: hundreds of characters become one instanced draw
    /// call and a pair of floats each.
    ///
    /// Sampling goes through a PlayableGraph rather than AnimationMode so that humanoid
    /// retargeting is applied exactly the way it is at runtime — these clips come from a
    /// different character pack than the mesh, so retargeting is not optional.
    ///
    /// Safe to re-run; every output overwrites in place.
    /// </summary>
    public static class VatBaker
    {
        /// <summary>Where baked meshes, textures, materials and clip sets are written.</summary>
        public static string OutputFolder = "Assets/VAT/Baked";
        /// <summary>Where the generated preview and gameplay prefabs are written.</summary>
        public static string PrefabFolder = "Assets/VAT/Prefabs";
        const string ShaderName = "VAT/Lit";

        /// <summary>Vertices whose whole trajectory agrees to this are given the same texture column.</summary>
        const float DedupEpsilon = 1e-5f;

        /// <summary>What to bake. One of these per character.</summary>
        public sealed class Request
        {
            public string SourcePrefab;
            public string AnimatorController;
            public string Name;
            public float FrameRate = 30f;

            /// <summary>
            /// Optional. When null the baked prefab keeps VatPlayer's own defaults filtered to
            /// whatever clips the bake contains — fine while every species used the same clip
            /// names, useless the moment one swaps its move clip or adds a second attack.
            /// </summary>
            public PlayerSetup Player;

            /// <summary>
            /// Collapse every material into one submesh with one merged albedo. Required
            /// when the bake is headed for a portable export: glb importers re-index each
            /// primitive separately, destroying the vertex order the column mapping needs.
            /// </summary>
            public bool MergeMaterials;
        }

        /// <summary>Explicit VatPlayer wiring for a bake, so it does not have to be guessed.</summary>
        public sealed class PlayerSetup
        {
            public VatPlayer.LocomotionNode[] Locomotion;
            public VatPlayer.Action[] Actions;
            public string AttackClip;
            public string SecondaryAttackClip;
            public string HitClip;
            public string DeathClip;
        }
        public static VatClipSet Bake(Request request)
        {
            var log = new StringBuilder();
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(request.SourcePrefab);
            if (source == null)
            {
                Debug.LogError($"[VatBaker] Source prefab not found: {request.SourcePrefab}");
                return null;
            }

            var clips = CollectClips(request.AnimatorController, log);
            if (clips.Count == 0)
            {
                Debug.LogError($"[VatBaker] No clips found on {request.AnimatorController}");
                return null;
            }

            VatPaths.EnsureFolder(OutputFolder);
            VatPaths.EnsureFolder(PrefabFolder);

            GameObject instance = null;
            PlayableGraph graph = default;
            var scratch = new Mesh { name = "VatBakeScratch" };

            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                instance.name = "__VatBakeSubject";
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;

                var animator = instance.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    Debug.LogError("[VatBaker] Source prefab has no Animator to sample.");
                    return null;
                }
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Bake into the space of the Animator's transform: that is the object the
                // runtime prefab's root will be, so baked positions land where the skinned
                // version drew them.
                var root = animator.transform;

                var parts = CollectParts(instance, root, log);
                if (parts.Count == 0)
                {
                    Debug.LogError("[VatBaker] Source prefab has no renderers to bake.");
                    return null;
                }

                var layout = BuildLayout(parts, log);
                if (request.MergeMaterials) MergeToSingleMaterial(layout, request.Name, log);
                var rows = PlanRows(clips, request.FrameRate);
                int totalRows = 0;
                foreach (var r in rows) totalRows += r.FrameCount;

                int triangleTotal = 0;
                foreach (var sub in layout.Submeshes) triangleTotal += sub.Count / 3;
                log.AppendLine($"vertices={layout.VertexCount} triangles={triangleTotal} " +
                               $"submeshes={layout.Submeshes.Count} rows={totalRows} @ {request.FrameRate}fps");

                graph = PlayableGraph.Create("VatBake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "VatBakeOutput", animator);

                var positions = new Vector3[totalRows * layout.VertexCount];
                var normals = new Vector3[totalRows * layout.VertexCount];

                int rowCursor = 0;
                for (int c = 0; c < clips.Count; c++)
                {
                    var clip = clips[c];
                    var plan = rows[c];
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(false);
                    playable.SetApplyPlayableIK(false);
                    output.SetSourcePlayable(playable);

                    for (int f = 0; f < plan.FrameCount; f++)
                    {
                        float t = plan.TimeOf(f, clip.length);

                        // Set twice: the first call only primes the playable's previous
                        // time, and a single call leaves the pose one frame stale.
                        playable.SetTime(t);
                        playable.SetTime(t);
                        graph.Evaluate(0f);

                        SampleFrame(parts, root, scratch, positions, normals,
                                    (rowCursor + f) * layout.VertexCount);
                    }

                    playable.Destroy();
                    rowCursor += plan.FrameCount;
                }

                var columns = Deduplicate(positions, layout.VertexCount, totalRows, log);

                var positionMap = BuildPositionMap(positions, columns, layout.VertexCount, totalRows, request.Name);
                var normalMap = BuildNormalMap(normals, layout.VertexCount, totalRows, request.Name);
                var indexMap = BuildIndexMap(columns, request.Name);

                var mesh = BuildMesh(layout, positions, normals, totalRows, request.Name);
                var materials = BuildMaterials(layout, positionMap, normalMap, indexMap, request.Name, log);
                var clipSet = BuildClipSet(request, clips, rows, mesh, materials,
                                           positionMap, normalMap, indexMap,
                                           columns.UniqueCount, layout.VertexCount, totalRows);

                BuildPrefab(request, clipSet, log);
                BuildGameplayPrefab(request, source, clipSet, log);

                log.AppendLine($"position map {positionMap.width}x{positionMap.height} " +
                               $"({SizeOf(positionMap)}), normal map {normalMap.width}x{normalMap.height} " +
                               $"({SizeOf(normalMap)}), index map ({SizeOf(indexMap)})");
                Debug.Log($"[VatBaker] {request.Name}\n{log}");
                return clipSet;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEngine.Object.DestroyImmediate(scratch);
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        // --- clip discovery -------------------------------------------------------------

        static List<AnimationClip> CollectClips(string controllerPath, StringBuilder log)
        {
            var result = new List<AnimationClip>();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) return result;

            var seen = new HashSet<AnimationClip>();
            foreach (var clip in controller.animationClips)
            {
                if (clip == null || !seen.Add(clip)) continue;
                result.Add(clip);
                log.AppendLine($"clip {clip.name} len={clip.length:F3}s loop={clip.isLooping}");
            }
            return result;
        }

        struct RowPlan
        {
            public int FrameCount;
            public bool Loop;
            public float FrameRate;

            /// <summary>
            /// Looping clips are sampled over [0, length) so the last row wraps cleanly back
            /// to the first. One-shots are sampled over [0, length] inclusive so the final
            /// row is the true end pose.
            /// </summary>
            public float TimeOf(int frame, float length)
            {
                if (Loop) return length * frame / FrameCount;
                return FrameCount > 1 ? length * frame / (FrameCount - 1) : 0f;
            }
        }

        static List<RowPlan> PlanRows(List<AnimationClip> clips, float frameRate)
        {
            var plans = new List<RowPlan>(clips.Count);
            foreach (var clip in clips)
            {
                int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * frameRate));
                plans.Add(new RowPlan
                {
                    FrameCount = clip.isLooping ? frames : frames + 1,
                    Loop = clip.isLooping,
                    FrameRate = frameRate,
                });
            }
            return plans;
        }

        // --- geometry collection --------------------------------------------------------

        sealed class Part
        {
            public SkinnedMeshRenderer Skinned;
            public MeshRenderer Static;
            public Transform Transform;
            public Mesh SourceMesh;
            public Material Material;
            public int VertexOffset;
            public int VertexCount;

            /// <summary>Whichever renderer this part came from, for reading its material slots.</summary>
            public Renderer Renderer => Skinned != null ? (Renderer)Skinned : Static;
        }

        static List<Part> CollectParts(GameObject instance, Transform root, StringBuilder log)
        {
            var parts = new List<Part>();
            int offset = 0;

            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Skinned = smr,
                    Transform = smr.transform,
                    SourceMesh = smr.sharedMesh,
                    Material = smr.sharedMaterial,
                    VertexOffset = offset,
                    VertexCount = smr.sharedMesh.vertexCount,
                });
                log.AppendLine($"skinned  {smr.name} verts={smr.sharedMesh.vertexCount}");
                offset += smr.sharedMesh.vertexCount;
            }

            // Rigid attachments (the shortsword on hand.R) are not skinned, so they get
            // baked by following their bone's matrix instead of BakeMesh.
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = mr.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Static = mr,
                    Transform = mr.transform,
                    SourceMesh = filter.sharedMesh,
                    Material = mr.sharedMaterial,
                    VertexOffset = offset,
                    VertexCount = filter.sharedMesh.vertexCount,
                });
                log.AppendLine($"rigid    {mr.name} verts={filter.sharedMesh.vertexCount} " +
                               $"bone={(mr.transform.parent != null ? mr.transform.parent.name : "none")}");
                offset += filter.sharedMesh.vertexCount;
            }

            return parts;
        }

        sealed class Layout
        {
            public int VertexCount;
            public Vector2[] Uv;
            public Vector4[] Tangents;
            /// <summary>One entry per distinct source material, in first-seen order.</summary>
            public List<Material> Materials = new List<Material>();
            /// <summary>Triangle indices per material, parallel to <see cref="Materials"/>.</summary>
            public List<List<int>> Submeshes = new List<List<int>>();
            /// <summary>Set when the materials were flattened into one albedo.</summary>
            public Texture2D MergedAlbedo;
        }

        /// <summary>
        /// Flattens every renderer into one vertex array, but keeps triangles grouped by their
        /// source material as separate submeshes.
        ///
        /// Collapsing to a single material was fine for a character painted from one atlas, but
        /// wrong the moment a species has a body material and a separate one for gear: the gear
        /// would be repainted with the body's material. Note a renderer can itself have several
        /// submeshes with materials in a different slot order than its neighbour, so grouping
        /// has to be per submesh, not per renderer.
        /// </summary>
        static Layout BuildLayout(List<Part> parts, StringBuilder log)
        {
            int vertexCount = 0;
            foreach (var p in parts) vertexCount += p.VertexCount;

            var layout = new Layout
            {
                VertexCount = vertexCount,
                Uv = new Vector2[vertexCount],
                Tangents = new Vector4[vertexCount],
            };

            foreach (var p in parts)
            {
                var mesh = p.SourceMesh;

                var uv = mesh.uv;
                var tangents = mesh.tangents;
                for (int i = 0; i < p.VertexCount; i++)
                {
                    layout.Uv[p.VertexOffset + i] = uv != null && i < uv.Length ? uv[i] : Vector2.zero;
                    layout.Tangents[p.VertexOffset + i] =
                        tangents != null && i < tangents.Length ? tangents[i] : new Vector4(1, 0, 0, 1);
                }

                var slotMaterials = p.Renderer != null ? p.Renderer.sharedMaterials : new[] { p.Material };
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var material = sub < slotMaterials.Length ? slotMaterials[sub] : p.Material;
                    int bucket = layout.Materials.IndexOf(material);
                    if (bucket < 0)
                    {
                        layout.Materials.Add(material);
                        layout.Submeshes.Add(new List<int>());
                        bucket = layout.Materials.Count - 1;
                    }

                    var tris = mesh.GetTriangles(sub);
                    var target = layout.Submeshes[bucket];
                    for (int i = 0; i < tris.Length; i++)
                        target.Add(tris[i] + p.VertexOffset);
                }
            }

            for (int i = 0; i < layout.Materials.Count; i++)
                log.AppendLine($"submesh {i} material={(layout.Materials[i] != null ? layout.Materials[i].name : "null")} " +
                               $"tris={layout.Submeshes[i].Count / 3}");

            if (vertexCount > 65535)
                log.AppendLine($"NOTE {vertexCount} vertices needs a 32-bit index buffer");

            return layout;
        }

        static void SampleFrame(List<Part> parts, Transform root, Mesh scratch,
                                Vector3[] positions, Vector3[] normals, int baseIndex)
        {
            var worldToRoot = root.worldToLocalMatrix;

            foreach (var part in parts)
            {
                Vector3[] sourcePositions;
                Vector3[] sourceNormals;
                Matrix4x4 toRoot;

                if (part.Skinned != null)
                {
                    // BakeMesh returns the deformed mesh in the renderer's own local space,
                    // so we still have to lift it into root space ourselves.
                    part.Skinned.BakeMesh(scratch, true);
                    sourcePositions = scratch.vertices;
                    sourceNormals = scratch.normals;
                    toRoot = worldToRoot * part.Transform.localToWorldMatrix;
                }
                else
                {
                    sourcePositions = part.SourceMesh.vertices;
                    sourceNormals = part.SourceMesh.normals;
                    toRoot = worldToRoot * part.Transform.localToWorldMatrix;
                }

                bool hasNormals = sourceNormals != null && sourceNormals.Length == sourcePositions.Length;

                for (int i = 0; i < part.VertexCount; i++)
                {
                    int dst = baseIndex + part.VertexOffset + i;
                    positions[dst] = i < sourcePositions.Length
                        ? toRoot.MultiplyPoint3x4(sourcePositions[i])
                        : Vector3.zero;
                    normals[dst] = hasNormals
                        ? toRoot.MultiplyVector(sourceNormals[i]).normalized
                        : Vector3.up;
                }
            }
        }

        // --- deduplication --------------------------------------------------------------

        sealed class Columns
        {
            public int[] VertexToColumn;
            public int[] ColumnToVertex;
            public int UniqueCount;
        }

        /// <summary>
        /// Groups vertices whose entire animated trajectory matches. These low-poly meshes
        /// split ~5300 vertices out of ~1300 distinct positions for UV seams and hard edges,
        /// and coincident vertices with identical skin weights move identically forever — so
        /// they can share one column of the position map. Comparing full trajectories rather
        /// than just the bind pose means this can never merge two vertices that later diverge.
        /// </summary>
        static Columns Deduplicate(Vector3[] positions, int vertexCount, int rowCount, StringBuilder log)
        {
            var result = new Columns
            {
                VertexToColumn = new int[vertexCount],
                ColumnToVertex = new int[vertexCount],
            };

            var buckets = new Dictionary<int, List<int>>(vertexCount);
            int unique = 0;

            for (int v = 0; v < vertexCount; v++)
            {
                int hash = TrajectoryHash(positions, v, vertexCount, rowCount);
                if (!buckets.TryGetValue(hash, out var candidates))
                {
                    candidates = new List<int>(1);
                    buckets[hash] = candidates;
                }

                int match = -1;
                foreach (int other in candidates)
                {
                    if (TrajectoriesEqual(positions, v, other, vertexCount, rowCount))
                    {
                        match = other;
                        break;
                    }
                }

                if (match >= 0)
                {
                    result.VertexToColumn[v] = result.VertexToColumn[match];
                }
                else
                {
                    result.VertexToColumn[v] = unique;
                    result.ColumnToVertex[unique] = v;
                    candidates.Add(v);
                    unique++;
                }
            }

            result.UniqueCount = unique;
            log.AppendLine($"dedup {vertexCount} vertices -> {unique} position columns " +
                           $"({(float)vertexCount / Mathf.Max(1, unique):F2}x)");
            return result;
        }

        static int TrajectoryHash(Vector3[] positions, int vertex, int vertexCount, int rowCount)
        {
            unchecked
            {
                int hash = 17;
                for (int r = 0; r < rowCount; r++)
                {
                    var p = positions[r * vertexCount + vertex];
                    hash = hash * 31 + Mathf.RoundToInt(p.x / DedupEpsilon);
                    hash = hash * 31 + Mathf.RoundToInt(p.y / DedupEpsilon);
                    hash = hash * 31 + Mathf.RoundToInt(p.z / DedupEpsilon);
                }
                return hash;
            }
        }

        static bool TrajectoriesEqual(Vector3[] positions, int a, int b, int vertexCount, int rowCount)
        {
            for (int r = 0; r < rowCount; r++)
            {
                int offset = r * vertexCount;
                if ((positions[offset + a] - positions[offset + b]).sqrMagnitude >
                    DedupEpsilon * DedupEpsilon)
                    return false;
            }
            return true;
        }

        // --- texture construction -------------------------------------------------------

        static Texture2D BuildPositionMap(Vector3[] positions, Columns columns,
                                          int vertexCount, int rowCount, string name)
        {
            int width = columns.UniqueCount;
            // RGBAHalf rather than a 3-channel half format because Texture2D has none; the
            // alpha channel is dead weight the shader ignores.
            var data = new ushort[width * rowCount * 4];

            for (int r = 0; r < rowCount; r++)
            {
                int srcRow = r * vertexCount;
                int dstRow = r * width * 4;
                for (int c = 0; c < width; c++)
                {
                    var p = positions[srcRow + columns.ColumnToVertex[c]];
                    data[dstRow + c * 4 + 0] = Mathf.FloatToHalf(p.x);
                    data[dstRow + c * 4 + 1] = Mathf.FloatToHalf(p.y);
                    data[dstRow + c * 4 + 2] = Mathf.FloatToHalf(p.z);
                    data[dstRow + c * 4 + 3] = 0;
                }
            }

            var tex = CreateTexture(width, rowCount, TextureFormat.RGBAHalf, $"T_VAT_{name}_Position");
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            return SaveTexture(tex);
        }

        static Texture2D BuildNormalMap(Vector3[] normals, int vertexCount, int rowCount, string name)
        {
            var data = new byte[vertexCount * rowCount * 2];

            for (int r = 0; r < rowCount; r++)
            {
                int srcRow = r * vertexCount;
                int dstRow = r * vertexCount * 2;
                for (int v = 0; v < vertexCount; v++)
                {
                    var e = EncodeOctahedral(normals[srcRow + v]);
                    data[dstRow + v * 2 + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt(e.x * 255f), 0, 255);
                    data[dstRow + v * 2 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(e.y * 255f), 0, 255);
                }
            }

            var tex = CreateTexture(vertexCount, rowCount, TextureFormat.RG16, $"T_VAT_{name}_Normal");
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            return SaveTexture(tex);
        }

        static Texture2D BuildIndexMap(Columns columns, string name)
        {
            int width = columns.VertexToColumn.Length;
            var data = new float[width];
            for (int v = 0; v < width; v++) data[v] = columns.VertexToColumn[v];

            var tex = CreateTexture(width, 1, TextureFormat.RFloat, $"T_VAT_{name}_Index");
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            return SaveTexture(tex);
        }

        /// <summary>
        /// Octahedral encode, matching VatDecodeNormal in VertexAnimation.hlsl. 8 bits per
        /// channel costs about half a degree of accuracy, which is invisible on a flat-shaded
        /// 2.7k-triangle character and a quarter the memory of storing xyz halves.
        /// </summary>
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

        static Texture2D CreateTexture(int width, int height, TextureFormat format, string name)
        {
            // linear: true — these hold geometry, not colour. sRGB would corrupt every value.
            var tex = new Texture2D(width, height, format, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            return tex;
        }

        /// <summary>
        /// Saved as a .asset rather than a PNG on purpose: .asset skips the texture importer
        /// entirely, so no platform override can quietly compress a position map into mush.
        /// </summary>
        static Texture2D SaveTexture(Texture2D tex)
        {
            string path = $"{OutputFolder}/{tex.name}.asset";
            AssetDatabase.CreateAsset(tex, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // --- asset construction ---------------------------------------------------------

        /// <summary>
        /// Flattens the layout to one submesh with one merged albedo, or leaves it alone and
        /// warns if the materials cannot be merged safely (different atlases, or not recolour
        /// materials at all) — silently shipping a wrongly-painted character would be worse.
        /// </summary>
        static void MergeToSingleMaterial(Layout layout, string name, StringBuilder log)
        {
            if (layout.Materials.Count <= 1)
            {
                log.AppendLine("merge requested but there is only one material; nothing to do");
                return;
            }

            string path = $"{OutputFolder}/T_VAT_Albedo_{name}_Merged.png";
            var merged = VatAlbedoMerge.Build(layout.Materials, layout.Submeshes, layout.Uv, path);
            if (merged == null || merged.Texture == null)
            {
                Debug.LogWarning($"[VatBaker] {name}: materials cannot be merged safely; " +
                                 "keeping them separate.");
                return;
            }

            log.AppendLine(merged.Report);
            if (merged.ContestedTexels > 0)
                Debug.LogWarning($"[VatBaker] {name}: {merged.ContestedTexels} texels are claimed " +
                                 "by more than one material; the later material wins there.");

            var combined = new List<int>();
            foreach (var sub in layout.Submeshes) combined.AddRange(sub);

            var first = layout.Materials[0];
            layout.Materials.Clear();
            layout.Materials.Add(first);
            layout.Submeshes.Clear();
            layout.Submeshes.Add(combined);
            layout.MergedAlbedo = merged.Texture;
        }

        static Mesh BuildMesh(Layout layout, Vector3[] positions, Vector3[] normals,
                              int rowCount, string name)
        {
            var mesh = new Mesh { name = $"SM_VAT_{name}" };
            if (layout.VertexCount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            // Frame 0 as the rest pose, so the asset preview and any shader-less fallback
            // still show a recognisable character rather than a dot at the origin.
            var restPositions = new Vector3[layout.VertexCount];
            var restNormals = new Vector3[layout.VertexCount];
            Array.Copy(positions, 0, restPositions, 0, layout.VertexCount);
            Array.Copy(normals, 0, restNormals, 0, layout.VertexCount);

            mesh.vertices = restPositions;
            mesh.normals = restNormals;
            mesh.tangents = layout.Tangents;
            mesh.uv = layout.Uv;

            mesh.subMeshCount = layout.Submeshes.Count;
            for (int i = 0; i < layout.Submeshes.Count; i++)
                mesh.SetTriangles(layout.Submeshes[i], i, false);

            // Bounds must cover every frame of every clip. Unity has no idea the vertex
            // shader moves anything, so a rest-pose bounding box would pop and cull wrongly.
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < rowCount * layout.VertexCount; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            mesh.bounds = new Bounds((min + max) * 0.5f, max - min);

            string path = $"{OutputFolder}/{mesh.name}.asset";
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        static Material[] BuildMaterials(Layout layout, Texture2D position, Texture2D normal,
                                         Texture2D index, string name, StringBuilder log)
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[VatBaker] Shader '{ShaderName}' not found.");
                return null;
            }

            var result = new Material[layout.Materials.Count];
            for (int i = 0; i < layout.Materials.Count; i++)
            {
                var source = layout.Materials[i];
                string suffix = layout.Materials.Count > 1 ? $"_{i}" : string.Empty;
                var material = new Material(shader) { name = $"M_VAT_{name}{suffix}" };

                if (source != null)
                {
                    if (source.HasProperty("_BaseMap"))
                        material.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                    if (source.HasProperty("_BaseColor"))
                        material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
                    if (source.HasProperty("_Metallic"))
                        material.SetFloat("_Metallic", source.GetFloat("_Metallic"));
                    if (source.HasProperty("_Smoothness"))
                        material.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));

                    // A source material that paints itself from an RGB mask has no plain
                    // albedo to copy, so resolve one.
                    var resolved = layout.MergedAlbedo != null
                        ? layout.MergedAlbedo
                        : ResolveRecolorAlbedo(source, log);
                    if (resolved != null)
                    {
                        material.SetTexture("_BaseMap", resolved);
                        material.SetColor("_BaseColor", Color.white);
                    }
                }

                material.SetTexture("_VatPositionMap", position);
                material.SetTexture("_VatNormalMap", normal);
                material.SetTexture("_VatIndexMap", index);
                material.enableInstancing = true;

                string path = $"{OutputFolder}/{material.name}.mat";
                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.SaveAssets();
                result[i] = AssetDatabase.LoadAssetAtPath<Material>(path);
            }

            return result;
        }

        /// <summary>
        /// Flattens a "Shader Graphs/RGBRecolor_*" material into an ordinary albedo texture.
        ///
        /// Those materials do not have a base map to copy — they tint a shared RGB mask atlas,
        /// where each channel selects one of three colours and the channel's intensity doubles
        /// as baked shading. Resolving it to a texture means the stock URP Lit VAT shader can
        /// render these creatures without reimplementing the graph.
        ///
        /// NOT reproduced: the graph's eye/cornea/lip Rectangle overlays, which paint small
        /// fixed UV rects on top. Bodies match; expect eyes and lips to differ.
        /// </summary>
        static Texture2D ResolveRecolorAlbedo(Material source, StringBuilder log)
        {
            if (source.shader == null || !source.shader.name.Contains("RGBRecolor")) return null;
            if (!source.HasProperty("_MainTex") || !source.HasProperty("_Color1")) return null;

            var mask = source.GetTexture("_MainTex");
            if (mask == null) return null;

            string maskPath = AssetDatabase.GetAssetPath(mask);
            if (string.IsNullOrEmpty(maskPath) || !File.Exists(maskPath)) return null;

            string outPath = $"{OutputFolder}/T_VAT_Albedo_{source.name}.png";
            if (File.Exists(outPath))
                return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);

            var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!readable.LoadImage(File.ReadAllBytes(maskPath), false))
            {
                UnityEngine.Object.DestroyImmediate(readable);
                return null;
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

            UnityEngine.Object.DestroyImmediate(readable);
            UnityEngine.Object.DestroyImmediate(output);

            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
            log.AppendLine($"resolved recolor albedo for {source.name} -> {outPath}");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static VatClipSet BuildClipSet(Request request, List<AnimationClip> clips, List<RowPlan> rows,
                                       Mesh mesh, Material[] materials, Texture2D position,
                                       Texture2D normal, Texture2D index,
                                       int sampleCount, int vertexCount, int totalRows)
        {
            string path = $"{OutputFolder}/VatClipSet_{request.Name}.asset";
            var set = AssetDatabase.LoadAssetAtPath<VatClipSet>(path);
            bool created = set == null;
            if (created) set = ScriptableObject.CreateInstance<VatClipSet>();

            set.Mesh = mesh;
            set.Materials = materials;
            set.PositionMap = position;
            set.NormalMap = normal;
            set.IndexMap = index;
            set.SampleCount = sampleCount;
            set.VertexCount = vertexCount;
            set.TotalRows = totalRows;

            var entries = new VatClipSet.Clip[clips.Count];
            int cursor = 0;
            for (int i = 0; i < clips.Count; i++)
            {
                entries[i] = new VatClipSet.Clip
                {
                    Name = clips[i].name,
                    StartRow = cursor,
                    FrameCount = rows[i].FrameCount,
                    FrameRate = rows[i].Loop
                        ? rows[i].FrameCount / Mathf.Max(1e-4f, clips[i].length)
                        : (rows[i].FrameCount - 1) / Mathf.Max(1e-4f, clips[i].length),
                    Loop = rows[i].Loop,
                };
                cursor += rows[i].FrameCount;
            }
            set.Clips = entries;

            if (created) AssetDatabase.CreateAsset(set, path);
            else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<VatClipSet>(path);
        }

        static void BuildPrefab(Request request, VatClipSet set, StringBuilder log)
        {
            string path = $"{PrefabFolder}/VAT_{request.Name}.prefab";

            var go = new GameObject($"VAT_{request.Name}");
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = set.Mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = set.Materials;

                var player = go.AddComponent<VatPlayer>();
                player.ClipSet = set;
                player.Renderer = renderer;
                ConfigureFromClipSet(player, set, request.Player);

                // This prefab exists only to be looked at, so give it its own clock: drop it
                // in a scene and it animates with no wiring. The gameplay prefab deliberately
                // does not get one — there, EnemyManager owns the tick.
                var driver = go.AddComponent<VatSelfDrive>();
                driver.Player = player;
                driver.LoopAction = -1;

                PrefabUtility.SaveAsPrefabAsset(go, path);
                AssetDatabase.SaveAssets();
                log.AppendLine($"prefab {path}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Raised once the gameplay prefab's skeleton has been replaced by the baked renderer
        /// and before the prefab is saved, with (prefab instance, clip set, player).
        ///
        /// This is how a host project rewires its own components — pointing a character
        /// controller at the <see cref="VatPlayer"/> instead of the now-deleted Animator, say.
        /// The package deliberately knows nothing about those types.
        /// </summary>
        public static Action<GameObject, VatClipSet, VatPlayer> OnGameplayPrefabBuilt;

        /// <summary>
        /// The playable character: a copy of the source prefab with the whole skinned model
        /// and skeleton swapped for the baked renderer. Written as a NEW asset — the original
        /// prefab is never touched, so existing scene references to it cannot break.
        /// </summary>
        static void BuildGameplayPrefab(Request request, GameObject source, VatClipSet set,
                                        StringBuilder log)
        {
            string path = $"{PrefabFolder}/{source.name}_VAT.prefab";
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);

            try
            {
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                                                  InteractionMode.AutomatedAction);

                var animator = instance.GetComponentInChildren<Animator>(true);
                var modelRoot = animator != null ? animator.transform : instance.transform;

                // Empty markers (a projectile origin, a muzzle point) are about to lose the
                // bones they hang off. Record each one's bind-pose offset so it can be put
                // back, and say so loudly — a marker that must keep moving needs its own
                // baked transform track. A marker is a childless transform carrying nothing
                // but its Transform, and which no SkinnedMeshRenderer lists as a bone.
                var bones = new HashSet<Transform>();
                foreach (var skinned in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (skinned.rootBone != null) bones.Add(skinned.rootBone);
                    if (skinned.bones == null) continue;
                    foreach (var bone in skinned.bones)
                        if (bone != null) bones.Add(bone);
                }

                var markers = new List<(string Name, Vector3 Position, Quaternion Rotation)>();
                foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t == modelRoot || t.childCount > 0 || bones.Contains(t)) continue;
                    if (t.GetComponents<Component>().Length > 1) continue;
                    markers.Add((t.name,
                                 modelRoot.InverseTransformPoint(t.position),
                                 Quaternion.Inverse(modelRoot.rotation) * t.rotation));
                }

                // Strip the skinned model and the skeleton.
                for (int i = modelRoot.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.DestroyImmediate(modelRoot.GetChild(i).gameObject);
                if (animator != null) UnityEngine.Object.DestroyImmediate(animator);

                var filter = modelRoot.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = set.Mesh;
                var renderer = modelRoot.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = set.Materials;

                var player = modelRoot.gameObject.AddComponent<VatPlayer>();
                player.ClipSet = set;
                player.Renderer = renderer;
                ConfigureFromClipSet(player, set, request.Player);

                foreach (var marker in markers)
                {
                    var socket = new GameObject(marker.Name);
                    socket.transform.SetParent(modelRoot, false);
                    socket.transform.localPosition = marker.Position;
                    socket.transform.localRotation = marker.Rotation;
                }
                if (markers.Count > 0)
                {
                    log.AppendLine($"WARNING {markers.Count} bone-parented marker(s) kept at their " +
                                   "bind-pose offset but no longer animated: " +
                                   string.Join(", ", markers.Select(m => m.Name)) +
                                   ". Bake a transform track for any that must move.");
                }

                OnGameplayPrefabBuilt?.Invoke(instance, set, player);

                PrefabUtility.SaveAsPrefabAsset(instance, path);
                AssetDatabase.SaveAssets();
                log.AppendLine($"gameplay prefab {path}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Drops any default locomotion node or action whose clip is not in this set, so the
        /// baked prefab does not log warnings for clips the character never had.
        /// </summary>
        static void ConfigureFromClipSet(VatPlayer player, VatClipSet set, PlayerSetup setup)
        {
            // Remember which clip each semantic slot points at, so the slots still resolve
            // after the arrays are compacted down to the clips this bake actually contains.
            string attackClip = setup?.AttackClip ?? NameAt(player.Actions, player.AttackAction);
            string secondaryClip = setup?.SecondaryAttackClip
                                   ?? NameAt(player.Actions, player.SecondaryAttackAction);
            string hitClip = setup?.HitClip ?? NameAt(player.Actions, player.HitAction);
            string deathClip = setup?.DeathClip ?? NameAt(player.Actions, player.DeathAction);

            if (setup?.Locomotion != null) player.Locomotion = setup.Locomotion;
            if (setup?.Actions != null) player.Actions = setup.Actions;

            var loco = new List<VatPlayer.LocomotionNode>();
            foreach (var node in player.Locomotion)
                if (set.IndexOf(node.ClipName) >= 0) loco.Add(node);
            if (loco.Count > 0) player.Locomotion = loco.ToArray();
            else Debug.LogWarning($"[VatBaker] {set.name}: no locomotion clip survived filtering.");

            var actions = new List<VatPlayer.Action>();
            foreach (var action in player.Actions)
                if (set.IndexOf(action.ClipName) >= 0) actions.Add(action);
            player.Actions = actions.ToArray();

            player.AttackAction = actions.FindIndex(a => a.ClipName == attackClip);
            player.SecondaryAttackAction = string.IsNullOrEmpty(secondaryClip)
                ? -1
                : actions.FindIndex(a => a.ClipName == secondaryClip);
            player.HitAction = actions.FindIndex(a => a.ClipName == hitClip);
            player.DeathAction = actions.FindIndex(a => a.ClipName == deathClip);
        }

        static string NameAt(VatPlayer.Action[] actions, int index)
        {
            return actions != null && index >= 0 && index < actions.Length
                ? actions[index].ClipName
                : null;
        }

        static string SizeOf(Texture2D tex)
        {
            long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
            return $"{bytes / 1024f / 1024f:F2} MB";
        }
    }
}
