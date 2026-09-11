using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VatPipeline;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Builds and opens a scene for looking at a VAT bake next to the skinned original, both
    /// driven in lockstep by a <see cref="VatComparisonRig"/>. Saved to disk so it survives a
    /// domain reload and can be reopened from the Project window.
    ///
    /// Works in edit mode (turn on "Always Refresh" in the Scene view toolbar) or just press
    /// Play. Select the "Controls" object and scrub Speed to move through the locomotion
    /// blend, or tick FireAttack / FireHit / FireDeath.
    /// </summary>
    public static class VatTestScene
    {
        public static string ScenePath = "Assets/VAT/Scenes/VatTest.unity";
        public static string CrowdScenePath = "Assets/VAT/Scenes/VatCrowdTest.unity";
        public static string CreatureScenePath = "Assets/VAT/Scenes/VatComparison.unity";
        /// <summary>One creature's skinned original and its baked counterpart.</summary>
        public sealed class ComparisonEntry
        {
            public string Name;
            /// <summary>The original skinned prefab. Prefer an armed variant if the bake used one.</summary>
            public string SkinnedPrefab;
            /// <summary>The prefab <c>VatBaker</c> wrote.</summary>
            public string VatPrefab;
            /// <summary>
            /// Forced onto the skinned side's Animator. A character pack's own controller
            /// usually holds a still pose and no parameters, so without this the skinned half
            /// cannot respond and the comparison is not like-for-like. Pass the same
            /// controller the bake was made from.
            /// </summary>
            public string AnimatorController;
        }

        /// <summary>
        /// One row of skinned/VAT pairs, one pair per creature, all driven by a single
        /// VatComparisonRig so a keypress hits every creature at once.
        /// </summary>
        public static void OpenCreatureComparison(IList<ComparisonEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                Debug.LogError("[VatTestScene] OpenCreatureComparison needs at least one entry.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Spacing is as tight as the run cycle's stance allows: the wider the row, the
            // further back the camera has to sit, and these are only ~2 m tall.
            const float pairSpacing = 3.6f;
            const float withinPair = 1.4f;
            float rowCentre = (entries.Count - 1) * pairSpacing * 0.5f;

            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(
                new Vector3(rowCentre, 1.15f, -7.1f), Quaternion.identity);
            // -7.1 not -6: a run stance is ~1.9 m wide, so the outermost creatures
            // overhang their pair centres and clip at the frame edge if pulled closer.
            camera.fieldOfView = 42f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.14f, 0.16f, 1f);
            camera.farClipPlane = 200f;
            camera.tag = "MainCamera";

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.transform.rotation = Quaternion.Euler(35f, 25f, 0f);
            light.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.4f, 0.45f, 1f);

            var pairs = new List<VatComparisonRig.Pair>();
            var missing = new List<string>();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                string name = entry.Name;
                var skinnedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.SkinnedPrefab);
                var vatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.VatPrefab);

                if (skinnedPrefab == null || vatPrefab == null)
                {
                    missing.Add($"{name} (skinned={skinnedPrefab != null} vat={vatPrefab != null})");
                    continue;
                }

                float x = i * pairSpacing;
                var group = new GameObject(name);
                group.transform.position = new Vector3(x, 0f, 0f);

                var skinned = (GameObject)PrefabUtility.InstantiatePrefab(skinnedPrefab);
                skinned.name = name + " - SKINNED";
                skinned.transform.SetParent(group.transform, false);
                skinned.transform.localPosition = new Vector3(-withinPair * 0.5f, 0f, 0f);

                var vat = (GameObject)PrefabUtility.InstantiatePrefab(vatPrefab);
                vat.name = name + " - VAT";
                vat.transform.SetParent(group.transform, false);
                vat.transform.localPosition = new Vector3(withinPair * 0.5f, 0f, 0f);

                // The preview prefab self-drives; the rig owns the clock here, and two clocks
                // on one player would run it at double speed.
                foreach (var driver in vat.GetComponentsInChildren<VatSelfDrive>(true))
                    UnityEngine.Object.DestroyImmediate(driver);

                var animator = skinned.GetComponentInChildren<Animator>(true);
                var controller = string.IsNullOrEmpty(entry.AnimatorController) ? null
                    : AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
                        entry.AnimatorController);
                if (animator != null && controller != null)
                {
                    animator.runtimeAnimatorController = controller;
                    animator.applyRootMotion = false;
                }
                else
                {
                    Debug.LogWarning($"[VatTestScene] {name}: animator={animator != null} " +
                                     $"controller={controller != null}; skinned side will not animate.");
                }

                pairs.Add(new VatComparisonRig.Pair
                {
                    Label = name,
                    Skinned = animator,
                    Vat = vat.GetComponentInChildren<VatPlayer>(true),
                });
            }

            var controls = new GameObject("Controls");
            var rig = controls.AddComponent<VatComparisonRig>();
            rig.Pairs = pairs.ToArray();
            rig.Speed = 3.4f;
            rig.AttackInterval = 0f;

            EditorSceneManager.SaveScene(scene, CreatureScenePath);
            Selection.activeGameObject = controls;

            string warn = missing.Count > 0 ? "  MISSING: " + string.Join(", ", missing) : "";
            Debug.Log($"[VatTestScene] {CreatureScenePath} — {pairs.Count} pairs, left is skinned, " +
                      "right is VAT. Select 'Controls' and scrub Speed or tick FireAttack / " +
                      "FireHit / FireDeath to hit every creature at once. In edit mode enable " +
                      "'Always Refresh' in the Scene view toolbar." + warn);
        }
        /// <summary>
        /// A crowd of baked instances, each on its own clip at its own rate, split into groups
        /// you can fire clips at. Press Play for live render counters — Unity's stats report
        /// the game view, so they say nothing useful in edit mode.
        /// </summary>
        public static void OpenCrowd(string vatPrefabPath, int count, int columns)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(vatPrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[VatTestScene] Missing prefab {vatPrefabPath}. " +
                               "Bake it first (Tools/VAT).");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            float span = columns * 1.4f;
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(
                new Vector3(span * 0.5f, span * 0.42f, -span * 0.55f),
                Quaternion.Euler(22f, 0f, 0f));
            camera.fieldOfView = 55f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);
            camera.farClipPlane = 500f;
            camera.tag = "MainCamera";

            // Lit from over the camera's shoulder: a crowd seen from a distance goes to mud
            // under the same rig that flatters a single character up close.
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2.2f;
            light.transform.rotation = Quaternion.Euler(38f, 20f, 0f);
            light.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.5f, 1f);

            var rigObject = new GameObject("Crowd");
            var rig = rigObject.AddComponent<VatCrowdRig>();
            rig.Prefab = prefab;
            rig.Count = count;
            rig.Columns = columns;
            rig.GroupCount = 4;
            rig.RateJitter = 0.12f;
            rig.RandomPhase = true;
            // Four bands doing different things out of the box, so the scene demonstrates the
            // point the moment it opens: idle, walk, run, idle.
            rig.GroupSpeeds = new[] { 0f, 1.6f, 3.4f, 0f };
            rig.SpawnCrowd();

            EditorSceneManager.SaveScene(scene, CrowdScenePath);
            Selection.activeGameObject = rigObject;

            Debug.Log($"[VatTestScene] {CrowdScenePath} — {count} instances, 4 groups by row " +
                      "band (idle / walk / run / idle). Press Play, then keys 1-4 fire the " +
                      "chosen Action at one group, 0 fires all, R resets. Live render " +
                      "counters overlay in play mode.");
        }

        public static void Open(string skinnedPath, string vatPath)
        {
            var skinnedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(skinnedPath);
            var vatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(vatPath);
            if (skinnedPrefab == null || vatPrefab == null)
            {
                Debug.LogError($"[VatTestScene] Missing prefab: skinned={skinnedPrefab != null} " +
                               $"vat={vatPrefab != null}. Bake it first (Tools/VAT).");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0f, 0.6f, -3.1f),
                                                    Quaternion.Euler(4f, 0f, 0f));
            camera.fieldOfView = 40f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.14f, 0.16f, 1f);
            camera.nearClipPlane = 0.05f;
            camera.tag = "MainCamera";

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(38f, 150f, 0f);
            light.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.3f, 0.31f, 0.35f, 1f);

            var skinned = (GameObject)PrefabUtility.InstantiatePrefab(skinnedPrefab);
            skinned.name = "LEFT - Skinned original";
            skinned.transform.position = new Vector3(-0.6f, 0f, 0f);

            var vat = (GameObject)PrefabUtility.InstantiatePrefab(vatPrefab);
            vat.name = "RIGHT - VAT baked";
            vat.transform.position = new Vector3(0.6f, 0f, 0f);

            var controls = new GameObject("Controls");
            var rig = controls.AddComponent<VatComparisonRig>();
            rig.Pairs = new[]
            {
                new VatComparisonRig.Pair
                {
                    Label = skinnedPrefab.name,
                    Skinned = skinned.GetComponentInChildren<Animator>(true),
                    Vat = vat.GetComponentInChildren<VatPlayer>(true),
                },
            };
            rig.Speed = 0f;

            if (rig.Pairs[0].Skinned == null || rig.Pairs[0].Vat == null)
                Debug.LogWarning($"[VatTestScene] Wiring incomplete: " +
                                 $"animator={rig.Pairs[0].Skinned != null} " +
                                 $"vatPlayer={rig.Pairs[0].Vat != null}");

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = controls;

            Debug.Log($"[VatTestScene] {ScenePath} — left is skinned, right is VAT. " +
                      "Select 'Controls' and scrub Speed, or press Play. In edit mode, enable " +
                      "'Always Refresh' in the Scene view toolbar or nothing animates.");
        }
    }
}
