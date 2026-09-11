using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VatPipeline;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// A/B benchmark for a VAT bake against the skinned original, at the port brief's
    /// acceptance load (237 enemies at wave 30).
    ///
    /// Measures the two costs the bake is meant to remove, separately:
    ///   animate — evaluating 237 humanoid Animators, versus 237 VatPlayer.Tick calls
    ///   render  — one camera render of all 237, so skinning and draw call setup are included
    ///
    /// Runs in the editor rather than play mode so the numbers are not polluted by gameplay
    /// systems. That makes them a controlled comparison of these two paths, not a frame
    /// budget: read the ratio, not the absolute.
    /// </summary>
    public static class VatBenchmark
    {
        const string ReportFolder = "Temp/VatVerify";
        const int Warmup = 20;
        const int Frames = 120;
        public static void Run(string skinnedPath, string vatPath, int count)
        {
            var skinnedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(skinnedPath);
            var vatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(vatPath);
            if (skinnedPrefab == null || vatPrefab == null)
            {
                Debug.LogError($"[VatBenchmark] Missing prefab: skinned={skinnedPrefab != null} " +
                               $"vat={vatPrefab != null}");
                return;
            }

            string previousScene = SceneManager.GetActiveScene().path;
            var log = new StringBuilder();
            log.AppendLine($"{count} instances, {Frames} timed frames after {Warmup} warmup");

            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camera = BuildStage(count);

                // Render into an offscreen target: a camera with no targetTexture tries to
                // present to the editor back buffer, and URP's final blit then fails against
                // the game view's dimensions, flooding the log and wrecking the timings.
                var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                log.AppendLine($"render target {rt.width}x{rt.height}");

                try
                {
                    MeasureSkinned(skinnedPrefab, count, camera, log);
                    ClearInstances();
                    MeasureVat(vatPrefab, count, camera, log);
                }
                finally
                {
                    camera.targetTexture = null;
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                Directory.CreateDirectory(ReportFolder);
                File.WriteAllText($"{ReportFolder}/benchmark.txt", log.ToString());
                Debug.Log($"[VatBenchmark]\n{log}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScene))
                    EditorSceneManager.OpenScene(previousScene, OpenSceneMode.Single);
            }
        }

        static Camera BuildStage(int count)
        {
            var camera = new GameObject("BenchCamera").AddComponent<Camera>();
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            float span = columns * 1.4f;
            camera.transform.SetPositionAndRotation(
                new Vector3(span * 0.5f, span * 0.55f, -span * 0.75f),
                Quaternion.Euler(22f, 0f, 0f));
            camera.fieldOfView = 55f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.11f, 0.13f, 1f);
            camera.farClipPlane = 500f;

            var light = new GameObject("BenchLight").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            light.transform.rotation = Quaternion.Euler(45f, 35f, 0f);
            light.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.3f, 0.31f, 0.34f, 1f);
            return camera;
        }

        static readonly List<GameObject> Instances = new List<GameObject>();

        static void Spawn(GameObject prefab, int count)
        {
            Instances.Clear();
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            for (int i = 0; i < count; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.position = new Vector3((i % columns) * 1.4f, 0f, (i / columns) * 1.4f);
                Instances.Add(go);
            }
        }

        static void ClearInstances()
        {
            foreach (var go in Instances)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            Instances.Clear();
        }

        static void MeasureSkinned(GameObject prefab, int count, Camera camera, StringBuilder log)
        {
            Spawn(prefab, count);

            var animators = new List<Animator>(count);
            int renderers = 0;
            foreach (var go in Instances)
            {
                var animator = go.GetComponentInChildren<Animator>(true);
                if (animator != null)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Rebind();
                    animators.Add(animator);
                }
                renderers += go.GetComponentsInChildren<Renderer>(true).Length;
            }

            double animate = Time(Warmup, Frames, () =>
            {
                for (int i = 0; i < animators.Count; i++) animators[i].Update(1f / 60f);
            });
            double render = Time(Warmup, Frames, () => camera.Render());

            log.AppendLine($"SKINNED  animators={animators.Count} renderers={renderers}");
            log.AppendLine($"  animate {animate:F3} ms/frame");
            log.AppendLine($"  render  {render:F3} ms/frame");
            log.AppendLine($"  total   {animate + render:F3} ms/frame");
        }

        static void MeasureVat(GameObject prefab, int count, Camera camera, StringBuilder log)
        {
            Spawn(prefab, count);

            var players = new List<VatPlayer>(count);
            int renderers = 0;
            foreach (var go in Instances)
            {
                var player = go.GetComponentInChildren<VatPlayer>(true);
                if (player != null)
                {
                    player.Rebind(Random.value);
                    player.Speed = Random.Range(0f, 3.4f);
                    players.Add(player);
                }
                renderers += go.GetComponentsInChildren<Renderer>(true).Length;
            }

            double animate = Time(Warmup, Frames, () =>
            {
                for (int i = 0; i < players.Count; i++) players[i].Tick(1f / 60f);
            });
            double render = Time(Warmup, Frames, () => camera.Render());

            log.AppendLine($"VAT      players={players.Count} renderers={renderers}");
            log.AppendLine($"  animate {animate:F3} ms/frame");
            log.AppendLine($"  render  {render:F3} ms/frame");
            log.AppendLine($"  total   {animate + render:F3} ms/frame");
        }

        static double Time(int warmup, int frames, System.Action step)
        {
            for (int i = 0; i < warmup; i++) step();

            double start = UnityEngine.Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < frames; i++) step();
            double elapsed = UnityEngine.Time.realtimeSinceStartupAsDouble - start;
            return elapsed * 1000.0 / frames;
        }
    }
}
