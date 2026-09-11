# VAT Pipeline

Bakes a skinned, animated character into **one static mesh plus a few data textures**. The
shader reads a vertex's position and normal for the current frame straight out of those
textures, so playback needs no skeleton and no skinning — a crowd of hundreds collapses into a
handful of GPU-instanced draw calls.

Measured on a 500-instance crowd of a ~2,800-vertex humanoid in URP 17.6:
**24.53 ms → 1.92 ms**. 1500 instances spread across 5 different clips render as
**9 instanced batches / 10 draw calls** — per-instance time offsets and different clips do
not break instancing, because both are instanced shader properties.

It also exports a **portable package** — a bind-pose `.glb`, the data textures as plain PNGs,
and a `SPEC.md` documenting the decode — for engines that cannot consume a Unity shader and
have to rebuild it themselves.

## Install

Copy `com.vatpipeline.core` into your project's `Packages/` folder. Unity picks embedded
packages up on its next focus or restart.

Requires URP 17.0+ and Unity 6000.0+.

## Use it

1. Import the **Host Project Integration** sample (Package Manager → Samples) into your own
   `Editor` folder and edit the paths at the top. That one file is the whole integration.
2. Build an `AnimatorController` holding exactly the clips you want baked — **the controller
   is the row layout, and the clip list is the entire memory budget.**
3. Run your bake menu item.
4. Drop the generated `VAT_<Name>.prefab` in a scene, or open the crowd scene.

## What's in here

| Path | |
|---|---|
| `Runtime/VatClipSet.cs` | the baked asset: mesh, textures, materials, clip table |
| `Runtime/VatPlayer.cs` | plays a clip set through a MaterialPropertyBlock |
| `Runtime/VatSelfDrive.cs` | drives a player with no gameplay code attached |
| `Runtime/VatComparisonRig.cs` | runs a bake and its skinned original in lockstep |
| `Runtime/VatCrowdRig.cs` | spawns a crowd in groups with per-instance rate jitter |
| `Editor/VatBaker.cs` | the bake |
| `Editor/VatPortableExporter.cs` | the engine-neutral export (glb + PNGs + SPEC.md) |
| `Editor/VatAlbedoMerge.cs` | flattens several materials into one primitive |
| `Editor/VatBakeVerifier.cs` | measures a bake against freshly skinned ground truth |
| `Editor/VatBenchmark.cs` | frame time, VAT vs skinned, at a given instance count |
| `Editor/VatTestScene.cs` | builds the single, crowd and comparison scenes |
| `Shaders/VatLit.shader` | wraps URP's own Lit passes, so lighting is URP's, not ours |

`Documentation~/VAT_Agent_Export_Process.md` is the long version: the architecture, the
reasoning behind the non-obvious choices, and ~25 traps that each cost a round trip to find.
Written for someone — or some agent — bringing this up in a project with no memory of it.
`Documentation~/How To Export.md` is the one-page human version.

## Constraints

- **≤ 8,192 vertices** per character — texture width is the vertex count.
- No blend shapes, no cloth, no root motion (VAT is in-place).
- An Animator and Avatar must be present: the bake samples clips *through* them, so humanoid
  retargeting is applied.
- GPU instancing here is the classic path, which takes these renderers off the SRP Batcher and
  the GPU Resident Drawer. That is the trade that produces the win.

## Project-specific things live in your project

The package holds no paths, no character names and no component references. Folders
(`VatBaker.OutputFolder`, `VatBaker.PrefabFolder`, `VatPortableExporter.OutputRoot`,
`VatTestScene.*ScenePath`) are overridable statics, and `VatBaker.OnGameplayPrefabBuilt` is
where you rewire a baked prefab's own components — the bake deletes the Animator, so anything
holding a reference to it needs moving onto the `VatPlayer`.
