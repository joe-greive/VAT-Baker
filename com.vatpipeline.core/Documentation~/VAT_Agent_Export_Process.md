# VAT Agent Export Process — Unity → a portable VAT package

**Audience:** an AI agent implementing this VAT export pipeline in a *different* Unity project,
and producing handoff material for whoever consumes it on the far side — another engine, a web
renderer, a colleague rebuilding the decode as a node graph.

**Companion:** `How To Export.md` (the short human version).

---

## 0. TL;DR of the whole job

1. Bake each animated character to **one boneless mesh + three data textures** (position hi,
   position lo, normal), addressed by `vertexId` on X and frame row on Y.
2. Ship a folder per character: `.glb`, 3 PNGs, 1 basemap PNG, `SPEC.md`, `export-log.txt`.
3. `SPEC.md` is the entire contract — the target rebuilds the decode as a node graph and cannot
   read your shader, so **anything not in SPEC.md does not exist**.
4. Verify against *independent ground truth*, never a round-trip.

Non-negotiables that will otherwise waste a full round-trip each:
**one primitive**, **vertex count == texture width**, **pinned rows after one-shots**,
**≤8192 vertices**, **no `.shader`/`.hlsl`/`.cs` in the payload**.

---

## 1. What VAT is, and when it's worth it

Skinned animation costs a skeleton evaluation plus a skinning pass per character. VAT replaces
both: bake every vertex's animated position (and normal) per frame into textures, and have the
vertex shader read them back. The character becomes a static mesh + a float ("which row").

Cost moves from CPU/skinning to **texture memory**, which is linear in
`vertices × rows × bytesPerTexel`. Frames are the only cheap lever.

**Good candidates:** many copies of low-poly characters, short clip lists, no runtime IK, no
blend shapes, no cloth or ragdoll, no per-bone procedural motion.
**Bad candidates:** hero characters needing IK/look-at, long clip libraries, anything where a
skeleton is queried at runtime (sockets, attach points, hit boxes on bones).

Reference measurement from the source project (237 characters, ~2.6k tris each):
animate 19.70 → 0.20 ms, render 4.83 → 1.72 ms, renderers 1,659 → 237. Instancing confirmed:
1,500 instances on 5 different clips → **9 instanced batches / 10 draw calls**.

---

## 2. Prerequisites in the source Unity project

| Need | Why |
|---|---|
| Skinned prefab with an `Animator` | The bake samples through a `PlayableGraph`, so it needs a live Animator + Avatar. |
| An `AnimatorController` referencing exactly the clips you want baked | The clip list *is* the row layout. The baker enumerates `controller.animationClips`. |
| Humanoid avatar (if retargeting) | Lets you reuse a clip library across bodies. Generic rigs work but clips are locked to their own rig. |
| ≤8192 vertices per character | Texture width == vertex count, and 8192 is the widest texture every current target is guaranteed to accept. Decimate first if over. |

**Check these before baking anything** (a 60-second probe saves hours):
- vertex + triangle counts per renderer, and **unique bind positions** (dedup potential)
- number of distinct materials (drives the merge decision, §6)
- blend shape count (must be 0 — VAT bakes final positions, so blend shapes would need driving at
  bake time)
- `Cloth` / `CharacterJoint` / `Rigidbody` on the rig (must be 0)
- animation events (they won't survive; the runtime must re-trigger them)
- bone-parented child objects (sockets, weapons) — see §5
- `applyRootMotion` (should be false; VAT is in-place)

---

## 3. Two bakes, not one

This is the single most common design mistake. The in-engine bake and the portable export want
**incompatible layouts**, so do not try to repack one into the other.

| | In-engine (Unity) | Portable export |
|---|---|---|
| Position lookup | **deduplicated** columns + an index texture (`vertexId → column`) | **pixel X = `vertexId`**, no indirection |
| Position format | `RGBAHalf` saved as `.asset` | 16-bit split across two 8-bit PNGs |
| Submeshes | one per material (fine) | **exactly one** |
| Extra rows | none | **pinned row after every one-shot** |
| Consumer | your own HLSL shader | a node graph rebuilt from `SPEC.md` |

Dedup is a 3–4× win on position memory (these meshes split ~4× for UV seams and hard edges) but
requires an index texture, which a least-capable target cannot be assumed to support. So the
portable path rebakes vertexId-major.

Keep them as two classes (`VatBaker`, `VatPortableExporter`) sharing only the sampling *approach*.
Deliberately reimplement the verification in the exporter rather than sharing a helper — a bug in
one then cannot hide in both.

---

## 4. The Unity exporter — architecture

Five editor scripts. Adapt names to the host project's conventions.

### 4.1 Sampling (the core, identical in both bakes)

```
instantiate prefab → set applyRootMotion=false, cullingMode=AlwaysAnimate
PlayableGraph (DirectorUpdateMode.Manual) + AnimationPlayableOutput on the Animator
for each clip:
    AnimationClipPlayable, SetApplyFootIK(false), SetApplyPlayableIK(false)
    for each frame:
        playable.SetTime(t); playable.SetTime(t);   // twice — see gotcha
        graph.Evaluate(0)
        for each SkinnedMeshRenderer: BakeMesh(scratch, true)
        transform vertices+normals into the Animator transform's space
```

- **Use a PlayableGraph, not `AnimationMode.SampleAnimationClip`.** Humanoid *retargeting* is
  applied exactly as at runtime. If your clips come from a different character pack than the
  mesh, retargeting is not optional and this is the only faithful path.
- **`SetTime` twice.** The first call only primes the playable's previous time; one call leaves
  the pose one frame stale. This produces a subtle, uniform one-frame lag that is very easy to
  miss.
- **Bake into the Animator transform's space**, and lift each renderer in with
  `animatorRoot.worldToLocalMatrix * renderer.transform.localToWorldMatrix`. `BakeMesh` returns
  the renderer's own local space, so skipping this silently offsets any renderer whose transform
  isn't identity.
- Normals: `matrix.MultiplyVector(bakedNormal).normalized`. Correct for rigid/uniform-scale
  matrices, which is what rigs give you. If a rig has non-uniform scale you need the
  inverse-transpose.

### 4.2 Row planning — loop vs one-shot

They are **not** the same, and mixing them up makes one-shots play one frame too slow forever:

```
frames = max(2, round(clip.length * fps))
loop     : rowCount = frames        rows span [0, length)   → last row wraps to first, no duplicate
one-shot : rowCount = frames + 1    rows span [0, length]   → last row IS the true end pose
```

Steps between rows is `frameCount` for a loop and `frameCount - 1` for a one-shot. Publish both
the flag and the frame count in the clip table so the consumer can pick the right maths.

### 4.3 Pinned rows (portable export only)

Every one-shot needs **one extra row appended after it**, byte-identical to that clip's final
frame. The runtime hands off to it when the clip completes and holds it (a corpse, a
follow-through). Two rules:

- **Copy the last frame's data**, do not re-sample at `t = length`. A re-sample can differ by a
  sub-frame and produce a visible snap at the handoff.
- List the pinned row number in the clip table. The consumer builds a dedicated "corpse" material
  from it: `ClipFrames = 1, Speed = 0, jitters = 0, ClipStart = pinnedRow`.

Verify pinned rows decode to a **0.0000 mm** delta from their clip's final frame.

### 4.4 Encoding

**Position — 16-bit split across two 8-bit PNGs.** No float/EXR survives the target's import.

```
size   = boundsMax - boundsMin                       // per axis, over EVERY frame of EVERY clip
n01    = clamp01((p - boundsMin) / size)
q      = clamp(round(n01 * 65535), 0, 65535)
hi.rgb = q >> 8        lo.rgb = q & 255              alpha = 255
```

Decode contract — **publish this exact line**:

```
n01 = (hi*256 + lo) / 65535
pos = boundsMin + n01 * boundsSize
```

> ⚠ **A 0.4% trap.** A plausible-looking alternative is `n01 = hi/255 + (lo/255)/256`, which
> equals `(hi*256+lo)/65280` — 0.39% too large, and it can exceed 1.0. On a 2 m character that's
> ~8 mm of drift that looks like "nearly right". The consumer's generic guide contains this
> variant. State the `/65535` form explicitly and say why.

Degenerate axis guard: if `size[axis] < 1e-6`, force it to `1e-6` or you divide by zero on a
perfectly flat character.

**Normal — octahedral into RG**, B=0, A=255:

```
encode: l1 = |x|+|y|+|z|;  p = n.xy / l1
        if n.z < 0:  p = (1 - |p.yx|) * sign(p.xy)      // the fold — omitting it is a real bug
        rg = p * 0.5 + 0.5

decode: f  = rg*2 - 1
        nz = 1 - |f.x| - |f.y|
        t  = clamp(-nz, 0, 1)
        n  = normalize(vec3(f.x + (f.x>=0 ? -t : t), f.y + (f.y>=0 ? -t : t), nz))
```

8-bit octahedral costs ~0.9° of accuracy — invisible on flat-shaded low-poly, and a quarter the
memory of xyz halves.

**Do not ship an 8-bit single-texture position variant.** Measured: 8-bit gives ~3 mm RMS spread
over every vertex, re-quantising differently each frame, which reads as constant surface shimmer.
hi/lo is ~0.02 mm for one extra sample and ~3 MB.

### 4.5 Texel layout and the row-orientation trap

Width = vertex count. Height = total rows. `u = (vertexId + 0.5) / width`,
`v = (row + 0.5) / height`.

> ⚠ **`SetPixels32` index 0 is bottom-left, and `EncodeToPNG` writes rows top-first. So frame 0
> ends up as the PNG's LAST row.**

In a bottom-left-origin sampler that is `v ≈ 0`, which is what `v = (row+0.5)/height` assumes. In
a **top-left-origin** importer the consumer must use `v = 1 - (row+0.5)/height`.

This has produced two separate false diagnoses. It is nasty because **positions still look
perfect when V is flipped** — every row is a plausible pose, you just get the wrong frame — while
normals then belong to a different pose than the positions being drawn, which reads as inside-out
shading. Document it in the *decode* section, not a troubleshooting footnote, and give a
30-second test: pin the row to a death clip's pinned row and check you get a corpse.

If the consumer exposes a `VFlip` flag, tell them which way to set it. If in doubt, offer to ship
pre-flipped so PNG row 0 = frame 0.

### 4.6 Mesh export

Write the mesh **last**, from the exact arrays the bake indexed. **Hand-write the `.glb`.**

FBX and glTF exporters weld and reorder vertices. Vertex order *is* the column mapping, so a
reorder silently shreds the character with no error anywhere. Hand-writing is ~150 lines and
gives exact control. A minimal valid glb:

- 12-byte header: magic `0x46546C67`, version 2, total length
- JSON chunk (type `0x4E4F534A`), space-padded to 4 bytes
- BIN chunk (type `0x004E4942`), zero-padded to 4 bytes
- accessors for POSITION (with required `min`/`max`), NORMAL, TANGENT, TEXCOORD_0, indices
- `UNSIGNED_SHORT` indices while vertexCount ≤ 65535
- glTF texture space runs V downward → write `uv.y` as `1 - uv.y`

**Coordinate space.** Unity is left-handed +Z forward; glTF is right-handed −Z forward. Negate Z
on positions, normals and tangents, and reverse triangle winding. Apply it to the **mesh and the
textures together**, once, so they cannot disagree. This convention was confirmed correct on the
consumer side with zero yaw offset.

---

## 5. Weapons, sockets and attachments

Three cases, in increasing pain:

1. **Weapon is a skinned mesh on the same rig** (best). It bakes as ordinary geometry, no special
   path. If the weapon lives on a *different* character in the same pack, you can often lift it:
   check that both rigs share bone names **and** bit-identical local transforms, then rebind
   `bones[]` by name. Measured 0.000 mm / 0.000° between two creatures on a shared skeleton, so
   the weapon landed exactly as authored with no offset tuning. Verify the rig delta before
   trusting it.
2. **Weapon is a rigid `MeshRenderer` parented to a bone.** Bake it by transforming its mesh by
   its bone's matrix each frame. It just becomes more baked vertices.
3. **An empty socket transform** the game reads at runtime (projectile origin, muzzle). VAT
   destroys the skeleton, so the socket is gone. Either bake auxiliary per-frame transform tracks
   for it, or preserve its bind-pose offset and accept that it no longer animates. **Say which
   you did** — a ranged enemy firing from a static offset is a bug you'll be blamed for later.

---

## 6. Multi-material characters — merge to one primitive

**The consuming importer re-indexes multi-primitive meshes**, which destroys vertex order and
therefore the whole column mapping. A 2-primitive character renders as shredded geometry. So:

> **Any character with 2+ materials must be flattened to a single primitive with a single
> merged albedo before export.**

Two sub-problems:

**(a) Grouping.** A single renderer can have several submeshes whose materials sit in a
*different slot order* than its neighbour's. Group triangles **per submesh**, not per renderer.

**(b) Merging albedos.** If the materials are a recolour family (one shared mask atlas tinted by
per-material colours), you can resolve each into a flat albedo and composite:

- fill the whole output with material 0's resolution first, so no holes exist (bilinear sampling
  and mip generation at UV island edges would otherwise pull in black)
- rasterize each material's UV coverage from its own triangles, and overwrite only inside it
- **count texels claimed by more than one material and report the number.** Where islands
  overlap, the later material wins and those texels are wrong.
- refuse to merge if the materials don't share one atlas — a silently mis-painted character is
  worse than a loud failure

Measure it, don't assume: on one character body vs. a borrowed weapon the regions were **fully
disjoint (0 contested)** so the merge was lossless; on another, body vs. gear genuinely
overlapped at **506 of 49,078 covered texels** on island boundaries, invisible at gameplay
distance. Both were the right call; only measurement told them apart.

Also beware measuring at the wrong resolution: 168 contested texels at 1024² became 506 at
2048². Measure at the atlas's real resolution and quote it with the resolution.

---

## 7. Verification — the part that actually saves round-trips

### 7.1 Never trust a round-trip

> A round-trip test (encode → decode → compare) proves only that your **encoder and decoder
> agree**. It passes perfectly when the data fed to the encoder was wrong.

Always find an *independent* path to the same truth. The best one here is free: the `.glb`'s own
`NORMAL` and `POSITION` attributes come from Unity's skinning and never touch the octahedral
encoder. Decode texture row for frame 0 and compare:

```
mean dot(glb NORMAL, decoded VAT normal)  →  expect 1.0000
per-axis corr(glb POSITION, decoded pos)  →  expect 1.0000
```

(Remember §4.5: frame 0 is the PNG's **last** row. Comparing against row 0 gives ~+0.22 and looks
like a catastrophic bug. This exact mistake was made during development.)

### 7.2 Baseline every metric on known-good data

A collaborator reported the normals as "garbage" using
`dot(normal, normalize(pos - frameCentroid))`, scoring +0.20/+0.08/+0.24 against an expected
"+0.5 or better". Running that same metric on **known-correct** glb normals gave
+0.201/+0.109/+0.279 — the metric simply isn't diagnostic for humanoids, whose limb normals are
roughly perpendicular to the radial direction from a torso centroid. The data was perfect.

**Before believing a metric, run it on data you know is right.** Then you have a threshold
instead of a guess.

### 7.3 The mandatory checks, per export

| Check | Expectation |
|---|---|
| vertex count == position width == normal width | exact |
| hi / lo / normal all same dimensions | exact |
| glb POSITION vs decoded frame-0 row, every vertex | < quantisation floor (~0.04 mm) |
| glb NORMAL vs decoded frame-0 row | mean dot ≥ 0.99 (we get 1.0000) |
| pinned row vs its clip's final frame | 0.0000 mm |
| every one-shot has a pinned row in the table; every loop has none | exact |
| glb: 1 primitive, indices divisible by 3, all indices < vertexCount, accessor byteLengths match counts, all bufferViews inside the BIN chunk | exact |
| basemap count == primitive count | exact |
| no `.shader` / `.hlsl` / `.cs` anywhere in the folder | exact |
| vertexCount ≤ 8192 | exact |
| bounds contain every frame | by construction (min/max over all rows) |

Build the glb-vs-texture alignment check **into the exporter** so every future rebake
self-validates and errors loudly. It is the one invariant everything else rests on.

### 7.4 In-engine visual verification

Also verify the *in-engine* bake, separately, because it catches shader bugs the export can't:

- Render the skinned original and the VAT version **alone, at the same world position**, and diff
  the images. Expect ~0.1% of covered pixels differing (antialiasing only).
- Do **not** judge by side-by-side screenshots. A weapon at the end of a swinging arm covers a lot
  of ground per frame, so a 2-frame timing difference reads as a huge positional error. This
  wasted real time.
- Report on-row and between-row error separately. On-row is precision (sub-mm). Between-row is
  temporal resolution: at 30 fps a fast sword tip hit **147.6 mm** mid-frame vs 0.49 mm on-row;
  60 fps quartered it to 39.3 mm. Neither is a bug — but the consumer must do the two-row lerp.

---

## 8. The handoff bundle

One folder per character. **Exactly** these files:

```
<Character>/
  <character>Model.glb                     bind-pose mesh, no skinning, no animation tracks
  vat_position_<Character>_16bit_hi.png
  vat_position_<Character>_16bit_lo.png
  vat_normal_<Character>.png
  basemap_<Character>.png                  the only sRGB texture in the package
  SPEC.md
  export-log.txt
```

No `reference/` folder of shader source. The consumer cannot use it, and it invites them to read
your HLSL instead of the spec — which is exactly how a spec rots.

Name the mesh after the character, not `model.glb`. Six folders each containing `model.glb` is a
mistake you make once.

### `SPEC.md` must contain

1. **Mesh** — vertex count (== texture width, and the cap), triangle count, filename, primitive
   count, and per-primitive material/albedo mapping.
2. **Textures** — dimensions, encodings, and a file table.
3. **Import rules**, stated as rules not suggestions: linear/no-sRGB, **point/nearest** filtering,
   clamp wrap, **no mipmaps, no compression**. Note which single texture *is* sRGB (the basemap).
4. **Bounds** — `min` and `size` per axis, full float precision, plus the note that they're the
   union over every frame including pinned rows and must also drive the culling volume.
5. **Clip table** — `| Clip | Start row | Frames | FPS | loop\|one-shot | Pinned row |`, plus the
   loop-vs-one-shot row-span explanation.
6. **Decode maths** — plain arithmetic, no HLSL intrinsics. Position (with the `/65535` warning),
   normal (full octahedral decode), and what to do with tangents.
7. **Playback** — the row/lerp formula for loop and one-shot, and the handoff to the pinned row.
8. **Per-instance variation** — what the only per-instance input is.
9. **Notes** — tempo, orientation/handedness, the vertexId-must-index-the-shared-array rule,
   whether any merge happened and its contested-texel count, and anything deliberately not
   carried over.
10. **Measured accuracy** — the §7.3 numbers, from this build, not from memory.
11. **Troubleshooting** — symptom → cause, at minimum: collapsed mesh (filtering/compression),
    wrong clip or backwards animation (V flip), inside-out (V flip or missing sRGB fix on the
    normal sample), washed-out (a `vat_*` imported as sRGB), popping at screen edges (culling
    bounds).

### `export-log.txt` must contain

Everything the exporter did — parts collected, clip plan with row ranges and pinned rows, merge
report, verification numbers — **and a complete file manifest with per-file byte sizes and a
total.** A missing texture cost a round-trip once; the manifest makes it a 5-second check.

---

## 9. Reconciling with the consumer's own renderer

A crowd renderer on the far side will typically derive the frame from `TIME` and an instance
index, with per-instance phase and rate jitter, rather than tracking playback per character on
the CPU. Your export has to fit that, and there are five points of friction worth pre-empting
**in writing**:

1. **Frame ownership.** Their shader computes the row; your `SPEC.md` per-instance row scheme is
   only one option. What they actually need from you is `ClipStart`, `ClipFrames`, `Fps`, and the
   loop flag per clip — which the clip table already gives. Don't insist on your playback model.
2. **The `/65535` vs `/65280` decode.** §4.4. Their generic guide has the wrong variant. Flag it.
3. **sRGB applies to the NORMAL texture too.** Their guide says to insert Linear-to-RGB nodes
   after the *position* samples. The normal texture is equally data, and if it's sampled as sRGB
   the octahedral values are gamma-mangled. Measured: correct decode gives mean dot 1.0000 against
   ground truth; the same data sampled as sRGB gives **0.49** (~60° average error) — which renders
   as inside-out shading with positions still looking fine. **Say explicitly that the fix must be
   applied to the normal sample as well.**
4. **Pinned rows are their corpse material.** Point at it: `ClipFrames = 1, Speed = 0, jitters
   = 0, ClipStart = <pinned row>`. Also tell them the second-to-last death frame should visually
   match the pinned row, because their hold deadlines exit 2 frames before the wrap.
5. **Single primitive is *their* constraint, discovered the hard way.** Record it as a hard export
   rule, not a preference, so nobody re-litigates it.

Also worth volunteering: max-size clamping. If they report texture dimensions that aren't what you
shipped (e.g. everything 2048 wide), an importer Max Size is resampling your data — which is fatal
for octahedral normals and quietly corrosive for positions. Ask them to confirm dimensions match
`SPEC.md` exactly as a first-line check.

---

## 10. Traps, consolidated

**Baking**
- `TextureFormat.RGBHalf` does not exist. Use `RGBAHalf`.
- `SetTime` twice or the pose is one frame stale.
- Mesh bounds must be the union over every frame; Unity has no idea the vertex shader moves
  anything, so rest-pose bounds pop and cull wrong.
- Dedup on the **whole animated trajectory**, not the bind pose, or you merge vertices that later
  diverge.
- Guard a degenerate bounds axis before dividing.

**Assets**
- Never modify a source/third-party prefab. Write variants to your own folder.
- Rebuilding a prefab breaks scene references: the GUID survives, the fileID doesn't, and the
  field goes null with no warning. Always write a *new* prefab.
- Save data textures as `.asset` in-engine so no platform importer compresses them.
- `File.Copy` preserves the source's modified time, so a copied basemap looks older than the
  package. Never use file mtimes for freshness; compare content.

**Runtime component design**
- No `Update` on the per-character component if a manager already iterates them; expose
  `Tick(dt)` and make it public so tests can drive it at a fixed step.
- Order `Update` so simulation runs **before** input is polled. If input throws, everything after
  it dies silently — and if the input read is behind `if (Application.isPlaying)` the component
  works in edit mode and is dead in play mode.
- `Awake` doesn't run on editor-instantiated objects; self-initialise lazily.
- Editor `deltaTime` is meaningless for `[ExecuteAlways]`; keep your own clamped clock.
- `Destroy` is deferred outside edit mode, so Clear-then-Spawn stacks children. Unparent first.
- Two clocks on one player = double speed. Strip the self-driver when a rig owns the tick.

**Verification tooling**
- Engine render-stat APIs may report the *game view*, not your offscreen render — returning
  numbers that look plausible and mean nothing.
- A camera rendering with no target texture can flood errors and wreck timings.
- Don't leave a diagnostic tool in the repo that prints meaningless numbers; delete it.

---

## 11. Order of work for a new project

1. **Probe** the character (§2). Decide dedup/merge/decimation needs before writing anything.
2. Build the **in-engine bake** first, even when the export is the only deliverable — it gives
   you a same-engine A/B against the skinned original, which is the fastest way to find bake
   bugs.
3. Build the **verifier** immediately after, before the exporter. Numeric ground-truth comparison
   plus an image diff at the same world position.
4. Build the **portable exporter** as a separate class. Bake vertexId-major, add pinned rows, merge
   to one primitive, hand-write the glb.
5. Put the **glb-vs-texture alignment check inside the exporter** so it can never regress.
6. Write `SPEC.md` **generated from the bake**, never hand-maintained — then it cannot drift.
7. Export one character, hand it over, and get one rendered frame back before doing the rest.
8. Only then batch the remaining characters, and add per-character menu items so changing one
   never rewrites the others.

---

## 12. Known gaps to disclose up front

- Tangents are static (frame 0). Fine without a normal map, wrong with one.
- Motion vectors aren't baked, so motion-vector TAA will smear.
- Animation events don't survive; the runtime must re-fire them from clip timing.
- Runtime IK, per-bone procedural motion and ragdoll are gone.
- Bone sockets need explicit handling (§5).
- Material variants (elite tints, hit flash) each need a VAT-shader version, or a per-instance
  colour property. Easy to forget until spawn-time material swaps blank your characters.
