# How To Export a VAT Character

Short version. For the full detail see `VAT_Agent_Export_Process.md`.

---

## What a VAT export is

You turn an animated, skinned character into **one static mesh plus three data textures**. The
textures hold where every vertex is on every frame. The receiving engine's shader reads them back,
so it needs no skeleton and no skinning — a whole crowd becomes a handful of draw calls.

Cost is `vertices × frames`. **The clip list is the memory budget.**

---

## Before you start — check the character

| Check | Must be |
|---|---|
| Vertex count | **≤ 8,192** (texture width = vertex count). Decimate if over. |
| Blend shapes | 0 |
| Cloth / ragdoll joints | 0 |
| Root motion | off (VAT is in-place) |
| Animator + Avatar | present — the bake samples through them |

Also note how many **materials** it has and whether anything is **parented to a bone** (a weapon,
a muzzle point). Both change what happens next.

---

## Step 1 — Pick the clips

The clip list is the whole cost driver. Four clips is ~4–5 MB per character; the full 33-clip
library would be ~30–40 MB *each*.

A good minimum: **move, attack, damage, death.** Add variety later.

Watch for expensive outliers — one 5.3 s idle is 160 rows, more than three short clips combined.

## Step 2 — Make a controller

Build an `AnimatorController` holding exactly those clips. **The controller is the row layout** —
the exporter reads its clip list, in order.

Shape it like the game's real controllers: a locomotion state plus Attack / Hit / Die reached from
Any State, so the baked playback matches how the game will actually drive it.

## Step 3 — Handle weapons and extra materials

- **Weapon that's a skinned mesh on the same rig** → nothing to do, it bakes as normal geometry.
- **Weapon borrowed from another character on the same skeleton** → it can be rebound by bone
  name, as long as both rigs match. Verify before trusting it.
- **2+ materials** → must be **merged into one**. The receiving importer re-indexes
  multi-primitive meshes and shreds the character. The tool measures how many texels are
  contested; often it's zero and the merge is free.
- **An empty socket the game reads at runtime** (projectile origin) → VAT deletes the skeleton, so
  this needs baking separately or it stops animating. Decide, and write it down.

## Step 4 — Bake

Run the bake for that character. Never bake all characters when you changed one.

## Step 5 — Export

Run the portable export for that character. It produces one folder.

## Step 6 — Check the log before you send anything

Open `export-log.txt` and confirm:

- `glb vertex order … PASS` ← the one invariant everything rests on
- `pinned rows … max delta from final frame 0.0000 mm`
- position round trip ≈ 0.03 mm, normals ≤ ~1°
- the **file manifest** lists 6 files (7 if the character genuinely needs 2 albedos) with sizes
- the mesh is **1 primitive**

If any of those look wrong, fix it before handing over. Each bad handover costs a full round-trip.

---

## What the folder contains

```
<Character>/
  <character>Model.glb                    the mesh, no skeleton, no animation
  vat_position_<Character>_16bit_hi.png   position, high byte
  vat_position_<Character>_16bit_lo.png   position, low byte
  vat_normal_<Character>.png              normals
  basemap_<Character>.png                 the colour texture
  SPEC.md                                 the contract — how to decode all of it
  export-log.txt                          what happened + file manifest
```

`SPEC.md` is the deliverable that matters. The receiving side rebuilds the decode from it alone
and cannot read our shader — **if it isn't in SPEC.md, it doesn't exist.**

---

## What the other side needs from you

They need to know, and it's all in `SPEC.md`:

1. **Vertex count** — must exactly equal the texture width, or the mesh shreds.
2. **Bounds** (min + size per axis) — the exact floats. Wrong bounds = squashed or exploded mesh.
3. **The clip table** — start row, frame count, fps, loop or one-shot, and the **pinned row** for
   each one-shot.
4. **Import settings** — the three `vat_*` textures are *data*: point filtering, no compression,
   no mipmaps, linear. The basemap is the only sRGB texture.
5. **The decode maths**, including the two easy-to-get-wrong bits: position is
   `(hi*256 + lo) / 65535`, and the sRGB correction must be applied to the **normal** texture as
   well as the position ones.

---

## Three things that will bite

**Frame 0 is the PNG's last row.** Unity writes PNGs top-first from a bottom-left origin buffer.
If the other side flips V, positions still look fine — every row is a valid pose — but normals
then come from a different frame than the positions, which renders inside-out. Fast test: pin the
row to a death clip's pinned row and check you get a corpse.

**A round-trip test proves nothing.** Encoding then decoding your own data only shows the encoder
and decoder agree — it passes even if the input was wrong. Always compare against something
independent (the glb's own vertex data works and is free).

**Don't judge by side-by-side screenshots.** A weapon at the end of a swinging arm moves far per
frame, so a 2-frame timing difference looks like a huge error. Render each alone at the same
position and diff the images.
