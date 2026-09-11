// Copy this into your project's Editor folder and edit the paths. It is everything a host
// project needs: where assets live, which characters get baked, and how a baked prefab is
// wired back into gameplay. The package holds no project-specific knowledge of its own.
//
// Samples~ is excluded from compilation by Unity (the trailing ~), so this file is inert
// until you copy it out.

using UnityEditor;
using UnityEngine;
using VatPipeline;
using VatPipeline.EditorTools;

namespace MyGame.EditorTools
{
    public static class VatProjectMenu
    {
        const string SourcePrefab = "Assets/Characters/Skeleton.prefab";
        const string Controller = "Assets/Characters/Skeleton_VAT.controller";
        const string Name = "Skeleton";

        static string VatPrefab(string name) => $"{VatBaker.PrefabFolder}/VAT_{name}.prefab";
        static string ClipSet(string name) => $"{VatBaker.OutputFolder}/VatClipSet_{name}.asset";

        /// <summary>
        /// Points the package at this project's folders and installs the gameplay rewiring
        /// hook. Both folder properties default to Assets/VAT/… — override them if you have
        /// already baked somewhere else, or a bake will orphan the assets your scenes
        /// reference by GUID.
        /// </summary>
        [InitializeOnLoadMethod]
        static void Configure()
        {
            VatBaker.OutputFolder = "Assets/Characters/VAT";
            VatBaker.PrefabFolder = "Assets/Characters/VAT/Prefabs";
            VatPortableExporter.OutputRoot = "VAT_Export";

            VatBaker.OnGameplayPrefabBuilt -= Rewire;
            VatBaker.OnGameplayPrefabBuilt += Rewire;
        }

        /// <summary>
        /// The bake deletes the skeleton and the Animator, so any component holding a
        /// reference to them is left dangling. Move it onto the VatPlayer here.
        ///
        /// Bone-parented empty markers (a projectile origin, a muzzle point) are recreated by
        /// name under the player at their bind-pose offset — find them with Transform.Find.
        /// They no longer move; if one must, bake it its own transform track.
        /// </summary>
        static void Rewire(GameObject instance, VatClipSet set, VatPlayer player)
        {
            // var character = instance.GetComponent<MyCharacter>();
            // if (character == null) return;
            // character.Animator = null;
            // character.Vat = player;
            // character.ModelRoot = player.transform;
            // character.MuzzlePoint = player.transform.Find("MuzzlePoint");
        }

        [MenuItem("Tools/VAT/Bake")]
        public static void Bake() =>
            VatBaker.Bake(new VatBaker.Request
            {
                SourcePrefab = SourcePrefab,
                AnimatorController = Controller,
                Name = Name,
                FrameRate = 30f,
            });

        [MenuItem("Tools/VAT/Verify Bake")]
        public static void Verify() =>
            VatBakeVerifier.Verify(SourcePrefab, Controller, ClipSet(Name), VatPrefab(Name));

        [MenuItem("Tools/VAT/Benchmark vs Skinned")]
        public static void Benchmark() => VatBenchmark.Run(SourcePrefab, VatPrefab(Name), 500);

        [MenuItem("Tools/VAT/Open Test Scene")]
        public static void OpenTestScene() => VatTestScene.Open(SourcePrefab, VatPrefab(Name));

        [MenuItem("Tools/VAT/Open Crowd Scene")]
        public static void OpenCrowdScene() => VatTestScene.OpenCrowd(VatPrefab(Name), 500, 25);

        /// <summary>
        /// Leave MergeMaterials on: most glb importers re-index each primitive separately,
        /// which destroys the vertexId == pixel X mapping the whole decode rests on.
        /// </summary>
        [MenuItem("Tools/VAT/Export Portable Package")]
        public static void ExportPortable() =>
            VatPortableExporter.Export(new VatPortableExporter.Request
            {
                SourcePrefab = SourcePrefab,
                AnimatorController = Controller,
                Name = Name,
                FrameRate = 30f,
                MergeMaterials = true,
            });
    }
}
