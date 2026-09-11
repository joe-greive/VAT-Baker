#ifndef VAT_VERTEX_ANIMATION_INCLUDED
#define VAT_VERTEX_ANIMATION_INCLUDED

// Vertex Animation Texture sampling, shared by every pass of VAT/Lit.
// Must be included AFTER LitInput.hlsl (it relies on the core texture macros) and
// BEFORE the URP pass file whose vertex function we wrap.
//
// Texture layout, produced by VatBaker.cs:
//
//   _VatPositionMap  RGBAHalf  width = deduplicated sample count, height = total rows.
//                              Object-space position relative to the character root.
//                              Deduplicated: the source meshes split ~5300 vertices out
//                              of only ~1300 distinct bind positions (UV seams and hard
//                              edges), and coincident vertices with identical skin
//                              weights always animate identically, so they share a column.
//
//   _VatNormalMap    RG16      width = mesh vertex count, height = total rows.
//                              Octahedral-encoded object-space normal. Full width, NOT
//                              deduplicated: split vertices share a position but
//                              deliberately carry different normals - that split is the
//                              whole reason the hard edges read as hard.
//
//   _VatIndexMap     RFloat    width = mesh vertex count, height = 1.
//                              vertexID -> column in _VatPositionMap.
//
// Rows are absolute frame indices across all clips concatenated. The CPU (VatPlayer)
// resolves clip ranges, looping, time scaling and crossfades and hands down finished row
// numbers, so this shader never needs to know a clip exists. That is what keeps the
// authoring control on the C# side.

TEXTURE2D(_VatPositionMap);
SAMPLER(sampler_VatPositionMap);
TEXTURE2D(_VatNormalMap);
SAMPLER(sampler_VatNormalMap);
TEXTURE2D(_VatIndexMap);
SAMPLER(sampler_VatIndexMap);

float4 _VatPositionMap_TexelSize;
float4 _VatNormalMap_TexelSize;
float4 _VatIndexMap_TexelSize;

// Per-instance pose, written through a MaterialPropertyBlock. Two poses are blended
// because that single mechanism covers both cases we need: the locomotion blend tree
// (idle/walk/run is only ever 2-way at any given Speed) and state-machine crossfades.
//
//   _VatPoseA = (rowFloor, rowCeil, rowFrac, weightOfPoseB)
//   _VatPoseB = (rowFloor, rowCeil, rowFrac, unused)
//
// The pose MUST arrive per instance. A MaterialPropertyBlock takes the renderer off the
// SRP Batcher path, which is what we want here: the batcher does not feed instanced
// properties, so every instance would sit on the same frame. Whether Unity then merges these
// into instanced batches has NOT been verified with batch counters - measure that in a
// player before quoting a draw call number.
UNITY_INSTANCING_BUFFER_START(VatInstanceProps)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VatPoseA)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VatPoseB)
UNITY_INSTANCING_BUFFER_END(VatInstanceProps)

float3 VatLoadPosition(float column, float row)
{
    float2 uv = float2((column + 0.5) * _VatPositionMap_TexelSize.x,
                       (row    + 0.5) * _VatPositionMap_TexelSize.y);
    return SAMPLE_TEXTURE2D_LOD(_VatPositionMap, sampler_VatPositionMap, uv, 0).xyz;
}

float2 VatLoadNormalOct(float column, float row)
{
    float2 uv = float2((column + 0.5) * _VatNormalMap_TexelSize.x,
                       (row    + 0.5) * _VatNormalMap_TexelSize.y);
    return SAMPLE_TEXTURE2D_LOD(_VatNormalMap, sampler_VatNormalMap, uv, 0).rg;
}

float3 VatDecodeNormal(float2 encoded)
{
    float2 f = encoded * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

// Samples one pose: two adjacent rows lerped by rowFrac, so 30 fps bakes still play
// smoothly at 60+ fps instead of stepping.
float3 VatPosePosition(float column, float4 pose)
{
    return lerp(VatLoadPosition(column, pose.x),
                VatLoadPosition(column, pose.y), pose.z);
}

float3 VatPoseNormal(float column, float4 pose)
{
    return lerp(VatDecodeNormal(VatLoadNormalOct(column, pose.x)),
                VatDecodeNormal(VatLoadNormalOct(column, pose.y)), pose.z);
}

void VatSamplePose(uint vertexID, out float3 positionOS, out float3 normalOS)
{
    float4 poseA = UNITY_ACCESS_INSTANCED_PROP(VatInstanceProps, _VatPoseA);
    float4 poseB = UNITY_ACCESS_INSTANCED_PROP(VatInstanceProps, _VatPoseB);

    float vertexColumn = (float)vertexID;
    float positionColumn = SAMPLE_TEXTURE2D_LOD(_VatIndexMap, sampler_VatIndexMap,
        float2((vertexColumn + 0.5) * _VatIndexMap_TexelSize.x, 0.5), 0).r;

    positionOS = VatPosePosition(positionColumn, poseA);
    normalOS   = VatPoseNormal(vertexColumn, poseA);

    // Every vertex of an instance takes the same branch, so this is coherent and skips
    // half the texture fetches whenever nothing is crossfading - which is most frames.
    if (poseA.w > 0.0)
    {
        positionOS = lerp(positionOS, VatPosePosition(positionColumn, poseB), poseA.w);
        normalOS   = lerp(normalOS,   VatPoseNormal(vertexColumn, poseB),    poseA.w);
    }

    normalOS = normalize(normalOS);
}

#endif
