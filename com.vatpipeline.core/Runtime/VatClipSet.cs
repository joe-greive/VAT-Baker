using System;
using UnityEngine;

namespace VatPipeline
{
    /// <summary>
    /// Everything a baked character needs at runtime: the combined mesh, the vertex
    /// animation textures, and the row ranges that carve those textures back into clips.
    ///
    /// Produced by <c>VatBaker</c> (editor only). One asset per baked character, so a
    /// pooled prefab can swap definitions without touching its renderer.
    /// </summary>
    [CreateAssetMenu(menuName = "VAT/Clip Set", fileName = "VatClipSet")]
    public sealed class VatClipSet : ScriptableObject
    {
        /// <summary>One baked animation clip, expressed as a span of texture rows.</summary>
        [Serializable]
        public struct Clip
        {
            public string Name;
            [Tooltip("First row of this clip in the VAT.")]
            public int StartRow;
            [Tooltip("Number of baked rows. A looping clip's last row is NOT a duplicate of " +
                     "its first — the player wraps back around instead, which keeps cycles seamless.")]
            public int FrameCount;
            public float FrameRate;
            public bool Loop;

            /// <summary>
            /// Playback duration in seconds at unit speed. A looping clip's rows span
            /// [0, length) so all FrameCount rows are distinct steps; a one-shot's span
            /// [0, length] inclusive, so it has FrameCount-1 steps between rows. Getting
            /// this wrong makes every one-shot play one frame too slow.
            /// </summary>
            public float Length
            {
                get
                {
                    if (FrameRate <= 0f) return 0f;
                    int steps = Loop ? FrameCount : FrameCount - 1;
                    return Mathf.Max(0, steps) / FrameRate;
                }
            }
        }

        [Header("Baked assets")]
        public Mesh Mesh;
        [Tooltip("One per submesh, in submesh order. A character with separate body and gear " +
                 "materials keeps them separate rather than being repainted with the first.")]
        public Material[] Materials = Array.Empty<Material>();
        public Texture2D PositionMap;
        public Texture2D NormalMap;
        public Texture2D IndexMap;

        [Header("Layout")]
        [Tooltip("Columns in PositionMap — distinct bind positions after deduplication.")]
        public int SampleCount;
        [Tooltip("Columns in NormalMap and IndexMap — vertices in the combined mesh.")]
        public int VertexCount;
        [Tooltip("Rows in PositionMap and NormalMap — every frame of every clip, concatenated.")]
        public int TotalRows;

        [Header("Clips")]
        public Clip[] Clips = Array.Empty<Clip>();

        /// <summary>Index of a clip by name, or -1. Linear, but Clips is single digits long.</summary>
        public int IndexOf(string clipName)
        {
            if (Clips == null) return -1;
            for (int i = 0; i < Clips.Length; i++)
                if (Clips[i].Name == clipName)
                    return i;
            return -1;
        }
    }
}
