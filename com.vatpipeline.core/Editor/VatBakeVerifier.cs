using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VatPipeline;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Checks a VAT bake against the skinned original two ways:
    ///
    /// 1. Numerically. For a spread of times in every clip it skins the source the normal
    ///    way and, separately, reads the baked textures back on the CPU exactly as the
    ///    vertex shader would. Reports per-vertex position error in millimetres and normal
    ///    error in degrees. This is the test that actually proves the row maths, the
    ///    deduplication and the half/octahedral precision — deliberately reimplemented here
    ///    rather than sharing VatBaker's code, so a bug in one does not hide in both.
    ///
    /// 2. Visually. Renders the skinned original and the baked prefab side by side into a
    ///    PNG so the shader path itself gets looked at.
    /// </summary>
    public static class VatBakeVerifier
    {
        const string ShotFolder = "Temp/VatVerify";

        /// <summary>
        /// Numerically compares every clip, then renders a handful of them.
        /// </summary>
        /// <param name="renderClips">
        /// Which clips to render, by name. Null renders the first four in the set — pass an
        /// explicit list to pick the poses that actually stress this character (a wide swing,
        /// a death sprawl). A name the set does not contain is skipped with a warning.
        /// </param>
        public static void Verify(string sourcePath, string controllerPath, string clipSetPath,
                                  string vatPrefabPath, string[] renderClips = null)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            var set = AssetDatabase.LoadAssetAtPath<VatClipSet>(clipSetPath);
            var vatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(vatPrefabPath);

            if (source == null || controller == null || set == null || vatPrefab == null)
            {
                Debug.LogError("[VatBakeVerifier] Missing input: " +
                               $"source={source != null} controller={controller != null} " +
                               $"clipSet={set != null} vatPrefab={vatPrefab != null}");
                return;
            }

            string previousScene = SceneManager.GetActiveScene().path;
            var log = new StringBuilder();

            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                BuildStage();

                // Both at the SAME position. Each is rendered alone and the two images are
                // diffed, so nothing is attributable to perspective between two screen
                // positions — which is exactly what made a side-by-side comparison
                // unreadable: at the end of a swinging arm the sword moves a long way per
                // frame while the torso barely moves at all.
                var skinned = (GameObject)PrefabUtility.InstantiatePrefab(source);
                skinned.transform.position = Vector3.zero;
                var vat = (GameObject)PrefabUtility.InstantiatePrefab(vatPrefab);
                vat.transform.position = Vector3.zero;

                var animator = skinned.GetComponentInChildren<Animator>(true);
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var player = vat.GetComponent<VatPlayer>();

                CompareNumerically(skinned, animator, controller, set, log);

                Directory.CreateDirectory(ShotFolder);

                // No clip names are hardcoded: a name from another character's library would
                // silently render nothing and the verification would "pass" having looked at
                // no pixels at all.
                if (renderClips == null || renderClips.Length == 0)
                {
                    int take = Mathf.Min(4, set.Clips.Length);
                    renderClips = new string[take];
                    for (int i = 0; i < take; i++) renderClips[i] = set.Clips[i].Name;
                }

                foreach (string clipName in renderClips)
                {
                    // A frame partway in, not frame 0: the first frame of most clips is the
                    // rest pose, where a bake is right even when the row maths is wrong.
                    int frame = FrameCountOf(set, clipName) / 2;
                    if (frame < 0) continue;
                    CompareRendered(skinned, vat, animator, controller, set, player,
                                    clipName, frame, log);
                }

                // The report is written to disk as well as logged: a scene change can
                // invalidate asset references before the log line runs.
                File.WriteAllText($"{ShotFolder}/report.txt", log.ToString());
                Debug.Log($"[VatBakeVerifier]\n{log}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScene))
                    EditorSceneManager.OpenScene(previousScene, OpenSceneMode.Single);
            }
        }

        static void BuildStage()
        {
            var camera = new GameObject("VerifyCamera").AddComponent<Camera>();
            // Pulled back far enough that a fully extended sword stays in frame.
            camera.transform.SetPositionAndRotation(new Vector3(0.15f, 0.55f, -2.8f), Quaternion.identity);
            camera.fieldOfView = 40f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.13f, 0.15f, 1f);
            camera.nearClipPlane = 0.05f;

            var light = new GameObject("VerifyLight").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(35f, 155f, 0f);
            light.shadows = LightShadows.Soft;

            // Deterministic ambient, so the two captures are comparable run to run.
            UnityEngine.Rendering.AmbientMode previous = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.28f, 0.29f, 0.33f, 1f);
            if (previous == UnityEngine.Rendering.AmbientMode.Skybox) RenderSettings.skybox = null;
        }

        // --- numeric comparison ---------------------------------------------------------

        sealed class Part
        {
            public SkinnedMeshRenderer Skinned;
            public Transform Transform;
            public Mesh SourceMesh;
            public int Offset;
            public int Count;
        }

        static List<Part> CollectParts(GameObject instance)
        {
            var parts = new List<Part>();
            int offset = 0;
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Skinned = smr, Transform = smr.transform, SourceMesh = smr.sharedMesh,
                    Offset = offset, Count = smr.sharedMesh.vertexCount,
                });
                offset += smr.sharedMesh.vertexCount;
            }
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = mr.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                parts.Add(new Part
                {
                    Transform = mr.transform, SourceMesh = filter.sharedMesh,
                    Offset = offset, Count = filter.sharedMesh.vertexCount,
                });
                offset += filter.sharedMesh.vertexCount;
            }
            return parts;
        }

        static void CompareNumerically(GameObject skinned, Animator animator,
                                       AnimatorController controller, VatClipSet set,
                                       StringBuilder log)
        {
            var parts = CollectParts(skinned);
            var root = animator.transform;
            var scratch = new Mesh { name = "VerifyScratch" };

            var truthPositions = new Vector3[set.VertexCount];
            var truthNormals = new Vector3[set.VertexCount];

            var indexLookup = ReadIndexMap(set);
            float[] normalizedTimes = { 0f, 0.13f, 0.37f, 0.5f, 0.62f, 0.88f };

            var graph = PlayableGraph.Create("VatVerify");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "out", animator);

            float worstPosition = 0f;
            float worstNormal = 0f;
            string worstWhere = "-";

            try
            {
                var seen = new HashSet<AnimationClip>();
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null || !seen.Add(clip)) continue;
                    int clipIndex = set.IndexOf(clip.name);
                    if (clipIndex < 0)
                    {
                        log.AppendLine($"MISSING clip {clip.name} is not in the clip set");
                        continue;
                    }

                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(false);
                    playable.SetApplyPlayableIK(false);
                    output.SetSourcePlayable(playable);

                    float clipWorstPos = 0f;
                    double clipSumSq = 0d;
                    float clipWorstNormal = 0f;
                    int samples = 0;

                    foreach (float normalized in normalizedTimes)
                    {
                        float t = normalized * clip.length;
                        playable.SetTime(t);
                        playable.SetTime(t);
                        graph.Evaluate(0f);

                        SampleSkinned(parts, root, scratch, truthPositions, truthNormals);

                        for (int v = 0; v < set.VertexCount; v++)
                        {
                            var baked = SampleBakedPosition(set, indexLookup[v], clipIndex, normalized);
                            float error = (baked - truthPositions[v]).magnitude;
                            clipWorstPos = Mathf.Max(clipWorstPos, error);
                            clipSumSq += (double)error * error;

                            var bakedNormal = SampleBakedNormal(set, v, clipIndex, normalized);
                            float angle = Vector3.Angle(bakedNormal, truthNormals[v]);
                            clipWorstNormal = Mathf.Max(clipWorstNormal, angle);
                            samples++;
                        }
                    }

                    float rms = samples > 0 ? Mathf.Sqrt((float)(clipSumSq / samples)) : 0f;
                    log.AppendLine($"{clip.name,-20} maxPos={clipWorstPos * 1000f,7:F3} mm  " +
                                   $"rmsPos={rms * 1000f,7:F4} mm  maxNormal={clipWorstNormal,6:F2} deg");

                    if (clipWorstPos > worstPosition)
                    {
                        worstPosition = clipWorstPos;
                        worstWhere = clip.name;
                    }
                    worstNormal = Mathf.Max(worstNormal, clipWorstNormal);

                    playable.Destroy();
                }
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEngine.Object.DestroyImmediate(scratch);
            }

            log.AppendLine($"WORST position error {worstPosition * 1000f:F3} mm (in {worstWhere}), " +
                           $"worst normal error {worstNormal:F2} deg");
        }

        static void SampleSkinned(List<Part> parts, Transform root, Mesh scratch,
                                  Vector3[] positions, Vector3[] normals)
        {
            var worldToRoot = root.worldToLocalMatrix;
            foreach (var part in parts)
            {
                Vector3[] sourcePositions;
                Vector3[] sourceNormals;

                if (part.Skinned != null)
                {
                    part.Skinned.BakeMesh(scratch, true);
                    sourcePositions = scratch.vertices;
                    sourceNormals = scratch.normals;
                }
                else
                {
                    sourcePositions = part.SourceMesh.vertices;
                    sourceNormals = part.SourceMesh.normals;
                }

                var toRoot = worldToRoot * part.Transform.localToWorldMatrix;
                for (int i = 0; i < part.Count; i++)
                {
                    positions[part.Offset + i] = toRoot.MultiplyPoint3x4(sourcePositions[i]);
                    normals[part.Offset + i] = toRoot.MultiplyVector(sourceNormals[i]).normalized;
                }
            }
        }

        static int[] ReadIndexMap(VatClipSet set)
        {
            var lookup = new int[set.VertexCount];
            var pixels = set.IndexMap.GetPixels();
            for (int v = 0; v < set.VertexCount; v++)
                lookup[v] = Mathf.RoundToInt(pixels[v].r);
            return lookup;
        }

        /// <summary>Mirrors VatPlayer.RowsFor plus the shader's row lerp, on the CPU.</summary>
        static void ResolveRows(VatClipSet set, int clipIndex, float normalized,
                               out int row0, out int row1, out float frac)
        {
            var clip = set.Clips[clipIndex];
            int count = Mathf.Max(1, clip.FrameCount);

            if (clip.Loop)
            {
                float x = Mathf.Repeat(normalized, 1f) * count;
                int i0 = Mathf.Min(Mathf.FloorToInt(x), count - 1);
                frac = x - i0;
                row0 = clip.StartRow + i0;
                row1 = clip.StartRow + (i0 + 1) % count;
            }
            else
            {
                float x = Mathf.Clamp01(normalized) * (count - 1);
                int i0 = Mathf.Min(Mathf.FloorToInt(x), count - 1);
                frac = x - i0;
                row0 = clip.StartRow + i0;
                row1 = clip.StartRow + Mathf.Min(i0 + 1, count - 1);
            }
        }

        static Vector3 SampleBakedPosition(VatClipSet set, int column, int clipIndex, float normalized)
        {
            ResolveRows(set, clipIndex, normalized, out int row0, out int row1, out float frac);
            var a = set.PositionMap.GetPixel(column, row0);
            var b = set.PositionMap.GetPixel(column, row1);
            return Vector3.Lerp(new Vector3(a.r, a.g, a.b), new Vector3(b.r, b.g, b.b), frac);
        }

        static Vector3 SampleBakedNormal(VatClipSet set, int vertex, int clipIndex, float normalized)
        {
            ResolveRows(set, clipIndex, normalized, out int row0, out int row1, out float frac);
            var a = set.NormalMap.GetPixel(vertex, row0);
            var b = set.NormalMap.GetPixel(vertex, row1);
            var na = DecodeOctahedral(new Vector2(a.r, a.g));
            var nb = DecodeOctahedral(new Vector2(b.r, b.g));
            return Vector3.Lerp(na, nb, frac).normalized;
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

        // --- visual comparison ----------------------------------------------------------

        /// <summary>
        /// Renders the skinned original and the baked version from the same camera at the
        /// same world position, one at a time, and diffs the two images. Addressing an exact
        /// baked row (rather than an arbitrary time) means the two should agree to within
        /// antialiasing and half-float precision, so any real shader bug shows up as a large
        /// block of differing pixels rather than a thin outline.
        /// </summary>
        /// <summary>Frames in a named clip, or -1 when the set does not hold it.</summary>
        static int FrameCountOf(VatClipSet set, string clipName)
        {
            int i = set.IndexOf(clipName);
            return i < 0 ? -1 : set.Clips[i].FrameCount;
        }

        static void CompareRendered(GameObject skinned, GameObject vat, Animator animator,
                                    AnimatorController controller, VatClipSet set,
                                    VatPlayer player, string clipName, int row,
                                    StringBuilder log)
        {
            AnimationClip clip = null;
            foreach (var c in controller.animationClips)
                if (c != null && c.name == clipName) { clip = c; break; }

            int clipIndex = set.IndexOf(clipName);
            if (clip == null || clipIndex < 0)
            {
                log.AppendLine($"render compare skipped, no clip {clipName}");
                return;
            }

            var baked = set.Clips[clipIndex];
            row = Mathf.Clamp(row, 0, baked.FrameCount - 1);
            int steps = baked.Loop ? baked.FrameCount : baked.FrameCount - 1;
            float normalized = (float)row / steps;

            var camera = UnityEngine.Object.FindAnyObjectByType<Camera>();
            Texture2D skinnedShot = null;
            Texture2D vatShot = null;
            var graph = PlayableGraph.Create("VatVerifyCapture");

            try
            {
                // --- skinned, alone ---
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "out", animator);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                output.SetSourcePlayable(playable);

                float t = normalized * clip.length;
                playable.SetTime(t);
                playable.SetTime(t);
                graph.Evaluate(0f);

                vat.SetActive(false);
                skinned.SetActive(true);
                skinnedShot = Render(camera);

                // --- baked, alone, at the same baked row ---
                PosePlayer(player, set, clipIndex, clipName, normalized);
                skinned.SetActive(false);
                vat.SetActive(true);
                vatShot = Render(camera);

                int differing = Diff(skinnedShot, vatShot, out Texture2D diff, out int covered);
                float percent = covered > 0 ? 100f * differing / covered : 0f;
                log.AppendLine($"{clipName,-20} row {row,3}: {differing} of {covered} covered px differ " +
                               $"({percent:F2}%)");

                File.WriteAllBytes($"{ShotFolder}/{clipName}_skinned.png", skinnedShot.EncodeToPNG());
                File.WriteAllBytes($"{ShotFolder}/{clipName}_vat.png", vatShot.EncodeToPNG());
                File.WriteAllBytes($"{ShotFolder}/{clipName}_diff.png", diff.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(diff);

                skinned.SetActive(true);
                vat.SetActive(true);
            }
            finally
            {
                if (skinnedShot != null) UnityEngine.Object.DestroyImmediate(skinnedShot);
                if (vatShot != null) UnityEngine.Object.DestroyImmediate(vatShot);
                if (graph.IsValid()) graph.Destroy();
            }
        }

        static void PosePlayer(VatPlayer player, VatClipSet set, int clipIndex,
                               string clipName, float normalized)
        {
            int locoIndex = Array.FindIndex(player.Locomotion, n => n.ClipName == clipName);
            if (locoIndex >= 0)
            {
                // Pin Speed to this node's own threshold so the blend resolves to it alone.
                player.Speed = player.Locomotion[locoIndex].Threshold;
                player.Rebind(normalized);
                player.Tick(0f);
                return;
            }

            int actionIndex = Array.FindIndex(player.Actions, a => a.ClipName == clipName);
            if (actionIndex < 0) return;

            var original = player.Actions[actionIndex];
            player.Rebind();
            // Temporarily strip the fade and the early exit so a single Tick lands exactly
            // on the requested row instead of part way through a transition.
            player.Actions[actionIndex] = new VatPlayer.Action
            {
                ClipName = original.ClipName,
                EnterFade = 0f,
                ExitTime = 1f,
                ReturnFade = 0f,
                HoldLastFrame = true,
            };
            player.TriggerAction(actionIndex);
            player.Tick(normalized * set.Clips[clipIndex].Length);
            player.Actions[actionIndex] = original;
        }

        static Texture2D Render(Camera camera)
        {
            var rt = new RenderTexture(900, 700, 24, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readback.Apply();
                RenderTexture.active = previous;
                camera.targetTexture = null;
                return readback;
            }
            finally
            {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Counts pixels that differ beyond an antialiasing tolerance. "Covered" is the union
        /// of pixels either image drew the character into, so the percentage is not diluted by
        /// a mostly empty frame.
        /// </summary>
        static int Diff(Texture2D a, Texture2D b, out Texture2D diff, out int covered)
        {
            var pa = a.GetPixels32();
            var pb = b.GetPixels32();
            var output = new Color32[pa.Length];
            var background = pa[0];

            int differing = 0;
            covered = 0;

            for (int i = 0; i < pa.Length; i++)
            {
                bool inA = Distance(pa[i], background) > 12;
                bool inB = Distance(pb[i], background) > 12;
                if (inA || inB) covered++;

                int d = Distance(pa[i], pb[i]);
                if (d > 24)
                {
                    differing++;
                    output[i] = new Color32(255, 40, 40, 255);
                }
                else
                {
                    byte grey = (byte)(inA || inB ? 90 : 20);
                    output[i] = new Color32(grey, grey, grey, 255);
                }
            }

            diff = new Texture2D(a.width, a.height, TextureFormat.RGB24, false);
            diff.SetPixels32(output);
            diff.Apply();
            return differing;
        }

        static int Distance(Color32 x, Color32 y)
        {
            return Mathf.Abs(x.r - y.r) + Mathf.Abs(x.g - y.g) + Mathf.Abs(x.b - y.b);
        }
    }
}
