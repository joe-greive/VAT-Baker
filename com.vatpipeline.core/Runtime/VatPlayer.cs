using System;
using UnityEngine;

namespace VatPipeline
{
    /// <summary>
    /// Plays a <see cref="VatClipSet"/> by writing two blended poses into a
    /// MaterialPropertyBlock. Replaces an Animator + 6 SkinnedMeshRenderers with one
    /// MeshRenderer and eight floats per instance.
    ///
    /// No Update of its own, by design. The owning manager calls
    /// <see cref="Tick"/>, which is what keeps hundreds of these affordable. Use
    /// <c>VatSelfDrive</c> for previewing outside the enemy loop.
    ///
    /// Fidelity note: the shader blends exactly two poses, so a crossfade collapses the
    /// locomotion blend to its dominant node for the duration of the fade (60–120 ms here).
    /// That is deliberate — a third pose would cost 50% more vertex texture fetches to hide
    /// something nobody can see.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VatPlayer : MonoBehaviour
    {
        /// <summary>A one-shot clip reached by a trigger, mirroring an Animator state.</summary>
        [Serializable]
        public struct Action
        {
            public string ClipName;
            [Tooltip("Seconds to fade in from whatever was playing.")]
            public float EnterFade;
            [Range(0f, 1f)]
            [Tooltip("Normalized time at which we start returning to locomotion. " +
                     "1 = play to the very end.")]
            public float ExitTime;
            [Tooltip("Seconds to fade back to locomotion.")]
            public float ReturnFade;
            [Tooltip("Never return to locomotion; hold the final row. Used by Die.")]
            public bool HoldLastFrame;
        }

        /// <summary>One node of the 1D locomotion blend, keyed on <see cref="Speed"/>.</summary>
        [Serializable]
        public struct LocomotionNode
        {
            public string ClipName;
            public float Threshold;
        }

        [Header("Wiring")]
        public VatClipSet ClipSet;
        public MeshRenderer Renderer;

        [Header("Locomotion blend (ascending thresholds)")]
        public LocomotionNode[] Locomotion =
        {
            new LocomotionNode { ClipName = "Idle01",            Threshold = 0f },
            new LocomotionNode { ClipName = "BattleWalkForward", Threshold = 1.6f },
            new LocomotionNode { ClipName = "BattleRunForward",  Threshold = 3.4f },
        };

        [Header("Actions")]
        public Action[] Actions =
        {
            new Action { ClipName = "Attack04", EnterFade = 0.08f, ExitTime = 0.8f, ReturnFade = 0.12f },
            new Action { ClipName = "GetHit",   EnterFade = 0.08f, ExitTime = 0.7f, ReturnFade = 0.12f },
            new Action { ClipName = "Die",      EnterFade = 0.06f, ExitTime = 1f,   ReturnFade = 0f, HoldLastFrame = true },
        };

        [Header("Semantic slots (index into Actions, -1 for none)")]
        [Tooltip("Which action the attack trigger fires. Indices rather than clip names " +
                 "because every species names its attack clip something different.")]
        public int AttackAction = 0;
        public int HitAction = 1;
        public int DeathAction = 2;
        [Tooltip("Optional alternate attack. -1 when the species only has one.")]
        public int SecondaryAttackAction = -1;

        /// <summary>Planar movement speed, drives the locomotion blend. The Animator "Speed" float.</summary>
        public float Speed;

        /// <summary>Multiplies clip playback rate. The Animator "speed" property.</summary>
        public float PlaybackSpeed = 1f;

        static readonly int PoseAId = Shader.PropertyToID("_VatPoseA");
        static readonly int PoseBId = Shader.PropertyToID("_VatPoseB");

        MaterialPropertyBlock _props;
        int[] _locoClips;
        int[] _actionClips;

        // Current source: either the locomotion blend, or a single action clip.
        bool _inLocomotion = true;
        int _actionIndex = -1;
        float _actionTime;
        float _locoPhase;

        // Crossfade from a snapshot of whatever was playing when the source changed.
        bool _fading;
        int _fadeFromClip;
        float _fadeFromNormalized;
        float _fadeElapsed;
        float _fadeDuration;

        void Awake() => EnsureInitialised();

        /// <summary>
        /// Awake does not run on objects instantiated in the editor, so Tick has to be able
        /// to bring itself up. That also makes a baked prefab scrubbable outside play mode.
        /// </summary>
        void EnsureInitialised()
        {
            if (_props == null) _props = new MaterialPropertyBlock();
            if (Renderer == null) Renderer = GetComponent<MeshRenderer>();
            if (_locoClips == null || _actionClips == null) ResolveClipIndices();
        }

        void ResolveClipIndices()
        {
            if (ClipSet == null) return;

            _locoClips = new int[Locomotion != null ? Locomotion.Length : 0];
            for (int i = 0; i < _locoClips.Length; i++)
            {
                _locoClips[i] = ClipSet.IndexOf(Locomotion[i].ClipName);
                if (_locoClips[i] < 0)
                    Debug.LogWarning($"[VatPlayer] {name}: locomotion clip '{Locomotion[i].ClipName}' " +
                                     $"is not in {ClipSet.name}.", this);
            }

            _actionClips = new int[Actions != null ? Actions.Length : 0];
            for (int i = 0; i < _actionClips.Length; i++)
            {
                _actionClips[i] = ClipSet.IndexOf(Actions[i].ClipName);
                if (_actionClips[i] < 0)
                    Debug.LogWarning($"[VatPlayer] {name}: action clip '{Actions[i].ClipName}' " +
                                     $"is not in {ClipSet.name}.", this);
            }
        }

        /// <summary>
        /// Resets to locomotion. Call on spawn from the pool. <paramref name="phase"/> in 0..1
        /// desyncs idle/walk cycles between instances, replacing the
        /// <c>Animator.Update(Random.Range(0, 0.6f))</c> trick.
        /// </summary>
        public void Rebind(float phase = 0f)
        {
            EnsureInitialised();
            _inLocomotion = true;
            _actionIndex = -1;
            _actionTime = 0f;
            _locoPhase = Mathf.Repeat(phase, 1f);
            _fading = false;
            PlaybackSpeed = 1f;
        }

        /// <summary>Starts an action by index into <see cref="Actions"/>. Restarts if already active.</summary>
        public void TriggerAction(int index)
        {
            EnsureInitialised();
            if (_actionClips == null || index < 0 || index >= _actionClips.Length) return;
            if (_actionClips[index] < 0) return;

            SnapshotForFade(Actions[index].EnterFade);
            _inLocomotion = false;
            _actionIndex = index;
            _actionTime = 0f;
        }

        /// <summary>Starts an action by clip name. Prefer the index overload on hot paths.</summary>
        public void TriggerAction(string clipName)
        {
            if (Actions == null) return;
            for (int i = 0; i < Actions.Length; i++)
                if (Actions[i].ClipName == clipName)
                {
                    TriggerAction(i);
                    return;
                }
        }

        public void TriggerAttack() => TriggerAction(AttackAction);
        public void TriggerHit() => TriggerAction(HitAction);
        public void TriggerDeath() => TriggerAction(DeathAction);

        /// <summary>Falls back to the primary attack when the species has no second one.</summary>
        public void TriggerSecondaryAttack()
            => TriggerAction(SecondaryAttackAction >= 0 ? SecondaryAttackAction : AttackAction);

        public bool HasSecondaryAttack => SecondaryAttackAction >= 0;

        /// <summary>True while a non-holding action is still playing.</summary>
        public bool IsActionPlaying => !_inLocomotion;

        void SnapshotForFade(float duration)
        {
            if (duration <= 0f || ClipSet == null)
            {
                _fading = false;
                return;
            }

            // Collapse the current source to a single clip so it fits pose slot A.
            if (_inLocomotion)
            {
                ResolveLocomotion(out int lo, out int hi, out float w);
                int dominant = w >= 0.5f ? hi : lo;
                _fadeFromClip = dominant;
                _fadeFromNormalized = _locoPhase;
            }
            else
            {
                _fadeFromClip = _actionClips[_actionIndex];
                _fadeFromNormalized = ActionNormalized();
            }

            if (_fadeFromClip < 0)
            {
                _fading = false;
                return;
            }

            _fading = true;
            _fadeElapsed = 0f;
            _fadeDuration = duration;
        }

        float ActionNormalized()
        {
            float length = ClipSet.Clips[_actionClips[_actionIndex]].Length;
            return length > 0f ? Mathf.Clamp01(_actionTime / length) : 0f;
        }

        /// <summary>Advances time and pushes the resulting pose pair onto the renderer.</summary>
        public void Tick(float deltaTime)
        {
            EnsureInitialised();
            if (ClipSet == null || Renderer == null || ClipSet.Clips == null || ClipSet.Clips.Length == 0)
                return;

            float dt = deltaTime * Mathf.Max(0f, PlaybackSpeed);

            // The frozen source keeps playing through the fade, exactly like an Animator
            // transition — otherwise the outgoing pose visibly stalls.
            if (_fading)
            {
                _fadeElapsed += deltaTime;
                var from = ClipSet.Clips[_fadeFromClip];
                if (from.Length > 0f)
                {
                    _fadeFromNormalized += dt / from.Length;
                    if (from.Loop) _fadeFromNormalized = Mathf.Repeat(_fadeFromNormalized, 1f);
                    else _fadeFromNormalized = Mathf.Clamp01(_fadeFromNormalized);
                }
                if (_fadeElapsed >= _fadeDuration) _fading = false;
            }

            if (_inLocomotion) AdvanceLocomotion(dt);
            else AdvanceAction(dt);

            WritePose();
        }

        void AdvanceLocomotion(float dt)
        {
            ResolveLocomotion(out int lo, out int hi, out float w);
            if (lo < 0) return;

            // Blend nodes share one normalized phase, which is how an Animator blend tree
            // keeps a walk and a run cycle foot-locked to each other.
            float lengthLo = ClipSet.Clips[lo].Length;
            float lengthHi = hi >= 0 ? ClipSet.Clips[hi].Length : lengthLo;
            float blended = Mathf.Lerp(lengthLo, lengthHi, w);
            if (blended > 0f) _locoPhase = Mathf.Repeat(_locoPhase + dt / blended, 1f);
        }

        void AdvanceAction(float dt)
        {
            int clip = _actionClips[_actionIndex];
            if (clip < 0)
            {
                _inLocomotion = true;
                return;
            }

            var action = Actions[_actionIndex];
            var baked = ClipSet.Clips[clip];
            _actionTime += dt;

            if (action.HoldLastFrame)
            {
                _actionTime = Mathf.Min(_actionTime, baked.Length);
                return;
            }

            float exit = baked.Length * Mathf.Clamp01(action.ExitTime <= 0f ? 1f : action.ExitTime);
            if (_actionTime >= exit)
            {
                SnapshotForFade(action.ReturnFade);
                _inLocomotion = true;
                _actionIndex = -1;
            }
        }

        void ResolveLocomotion(out int loClip, out int hiClip, out float weight)
        {
            loClip = -1;
            hiClip = -1;
            weight = 0f;
            if (_locoClips == null || _locoClips.Length == 0) return;

            if (_locoClips.Length == 1 || Speed <= Locomotion[0].Threshold)
            {
                loClip = _locoClips[0];
                hiClip = _locoClips[0];
                return;
            }

            for (int i = 1; i < _locoClips.Length; i++)
            {
                if (Speed <= Locomotion[i].Threshold || i == _locoClips.Length - 1)
                {
                    float a = Locomotion[i - 1].Threshold;
                    float b = Locomotion[i].Threshold;
                    loClip = _locoClips[i - 1];
                    hiClip = _locoClips[i];
                    weight = b > a ? Mathf.Clamp01((Speed - a) / (b - a)) : 0f;
                    return;
                }
            }
        }

        void WritePose()
        {
            Vector4 poseA, poseB;
            float weightB;

            if (_fading)
            {
                // A = the outgoing snapshot, B = where we are heading, collapsed to one clip.
                poseA = RowsFor(_fadeFromClip, _fadeFromNormalized);
                poseB = RowsFor(CurrentDominantClip(out float normalized), normalized);
                weightB = _fadeDuration > 0f ? Mathf.Clamp01(_fadeElapsed / _fadeDuration) : 1f;
            }
            else if (_inLocomotion)
            {
                ResolveLocomotion(out int lo, out int hi, out float w);
                if (lo < 0) return;
                poseA = RowsFor(lo, _locoPhase);
                poseB = RowsFor(hi, _locoPhase);
                weightB = w;
            }
            else
            {
                int clip = _actionClips[_actionIndex];
                if (clip < 0) return;
                poseA = RowsFor(clip, ActionNormalized());
                poseB = poseA;
                weightB = 0f;
            }

            poseA.w = weightB;
            poseB.w = 0f;

            _props.SetVector(PoseAId, poseA);
            _props.SetVector(PoseBId, poseB);
            Renderer.SetPropertyBlock(_props);
        }

        int CurrentDominantClip(out float normalized)
        {
            if (_inLocomotion)
            {
                ResolveLocomotion(out int lo, out int hi, out float w);
                normalized = _locoPhase;
                return w >= 0.5f ? hi : lo;
            }

            normalized = ActionNormalized();
            return _actionClips[_actionIndex];
        }

        /// <summary>
        /// Turns a clip-local normalized time into the absolute VAT rows to sample and the
        /// fraction between them, so 30 fps bakes still play smoothly at any frame rate.
        /// </summary>
        Vector4 RowsFor(int clipIndex, float normalized)
        {
            if (clipIndex < 0 || clipIndex >= ClipSet.Clips.Length) return Vector4.zero;

            var clip = ClipSet.Clips[clipIndex];
            int count = Mathf.Max(1, clip.FrameCount);
            int row0, row1;
            float frac;

            if (clip.Loop)
            {
                // Looping clips are baked over [0, length) with no duplicate end frame, so
                // the last row wraps straight back to the first.
                float x = Mathf.Repeat(normalized, 1f) * count;
                int i0 = Mathf.FloorToInt(x);
                if (i0 >= count) i0 = count - 1;
                frac = x - i0;
                row0 = i0;
                row1 = (i0 + 1) % count;
            }
            else
            {
                // One-shots are baked over [0, length] inclusive, so the final row is the
                // clip's true end pose and we clamp rather than wrap.
                float x = Mathf.Clamp01(normalized) * (count - 1);
                int i0 = Mathf.FloorToInt(x);
                if (i0 > count - 1) i0 = count - 1;
                frac = x - i0;
                row0 = i0;
                row1 = Mathf.Min(i0 + 1, count - 1);
            }

            return new Vector4(clip.StartRow + row0, clip.StartRow + row1, frac, 0f);
        }
    }
}
