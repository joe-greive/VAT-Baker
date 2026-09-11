using System;
using UnityEngine;

namespace VatPipeline
{
    /// <summary>
    /// Drives any number of skinned-original / VAT-bake pairs from one set of controls, so they
    /// can be compared live. Works in play mode and in the editor.
    ///
    /// Feeds both sides of each pair the same inputs the game feeds them — the Animator's
    /// "Speed" float and Attack/Hit/Die triggers on one side, <see cref="VatPlayer"/> on the
    /// other — rather than forcing both to a chosen frame. So what you are looking at is the
    /// real runtime behaviour of each path, including transitions.
    ///
    /// Fast-moving extremities are the honest comparison: at the end of a swinging arm a weapon
    /// covers a lot of ground per frame, so a small timing difference between the two reads as
    /// a big positional one. Judge torso and feet by eye, and trust VatBakeVerifier's numbers
    /// for the rest.
    ///
    /// In edit mode, enable "Always Refresh" in the Scene view toolbar or nothing moves.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class VatComparisonRig : MonoBehaviour
    {
        [Serializable]
        public struct Pair
        {
            public string Label;
            public Animator Skinned;
            public VatPlayer Vat;
        }

        [Header("Subjects")]
        public Pair[] Pairs = Array.Empty<Pair>();

        [Header("Controls")]
        [Range(0f, 4f)]
        [Tooltip("Locomotion blend input. A character baked with one move clip plays it at " +
                 "any value; the blend only does something on a bake with several locomotion " +
                 "clips across a speed range.")]
        public float Speed = 3.4f;

        [Range(0.1f, 2f)]
        public float PlaybackSpeed = 1f;

        [Tooltip("Seconds between automatic attacks. 0 (the default) disables them, which " +
                 "keeps each pair frame-locked: the Animator and VatPlayer consume a trigger " +
                 "on slightly different frames, so repeated attacks make a pair drift apart " +
                 "even though each side is behaving correctly.")]
        public float AttackInterval;

        [Header("One-shots (tick to fire, clears itself)")]
        public bool FireAttack;
        [Tooltip("Fires the species' alternate attack. Falls back to the primary attack on " +
                 "species that only have one, so it is always safe to press.")]
        public bool FireAttack2;
        public bool FireHit;
        public bool FireDeath;
        [Tooltip("Tick to send everything back to locomotion from a held death pose.")]
        public bool Reset;

        static readonly int AnimSpeed = Animator.StringToHash("Speed");
        static readonly int AnimAttack = Animator.StringToHash("Attack");
        static readonly int AnimAttack2 = Animator.StringToHash("Attack2");
        static readonly int AnimHit = Animator.StringToHash("Hit");
        static readonly int AnimDie = Animator.StringToHash("Die");

        float _timer;
        double _lastRealtime;

        void OnEnable()
        {
            _lastRealtime = Time.realtimeSinceStartupAsDouble;
            foreach (var p in Pairs)
                if (p.Skinned != null) p.Skinned.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = Application.isPlaying
                ? Time.deltaTime
                : Mathf.Clamp((float)(now - _lastRealtime), 0f, 0.1f);
            _lastRealtime = now;

            if (Reset) { Reset = false; ForEach(ResetPair); }
            if (FireAttack) { FireAttack = false; ForEach(Attack); }
            if (FireAttack2) { FireAttack2 = false; ForEach(SecondaryAttack); }
            if (FireHit) { FireHit = false; ForEach(Hit); }
            if (FireDeath) { FireDeath = false; ForEach(Death); }

            if (AttackInterval > 0f)
            {
                _timer += dt;
                if (_timer >= AttackInterval)
                {
                    _timer = 0f;
                    ForEach(Attack);
                }
            }

            Step(dt);
        }

        /// <summary>
        /// Advances every pair. Public so a fixed-step caller gets exactly what Update does
        /// instead of reimplementing it and drifting.
        /// </summary>
        public void Step(float dt)
        {
            foreach (var pair in Pairs)
            {
                if (pair.Skinned != null)
                {
                    pair.Skinned.speed = PlaybackSpeed;
                    pair.Skinned.SetFloat(AnimSpeed, Speed);
                    // Animators do not tick themselves outside play mode.
                    if (!Application.isPlaying) pair.Skinned.Update(dt);
                }

                if (pair.Vat != null)
                {
                    pair.Vat.PlaybackSpeed = PlaybackSpeed;
                    pair.Vat.Speed = Speed;
                    pair.Vat.Tick(dt);
                }
            }
        }

        void ForEach(Action<Pair> action)
        {
            foreach (var pair in Pairs) action(pair);
        }

        void ResetPair(Pair p)
        {
            if (p.Skinned != null) p.Skinned.Rebind();
            if (p.Vat != null) p.Vat.Rebind();
        }

        void Attack(Pair p)
        {
            if (p.Skinned != null) p.Skinned.SetTrigger(AnimAttack);
            if (p.Vat != null) p.Vat.TriggerAttack();
        }

        void SecondaryAttack(Pair p)
        {
            // Only send the trigger if the controller declares it — Animator.SetTrigger on a
            // missing parameter logs a warning every call.
            if (p.Skinned != null && HasParameter(p.Skinned, "Attack2"))
                p.Skinned.SetTrigger(AnimAttack2);
            else if (p.Skinned != null)
                p.Skinned.SetTrigger(AnimAttack);

            if (p.Vat != null) p.Vat.TriggerSecondaryAttack();
        }

        static bool HasParameter(Animator animator, string name)
        {
            foreach (var parameter in animator.parameters)
                if (parameter.name == name) return true;
            return false;
        }

        void Hit(Pair p)
        {
            if (p.Skinned != null) p.Skinned.SetTrigger(AnimHit);
            if (p.Vat != null) p.Vat.TriggerHit();
        }

        void Death(Pair p)
        {
            if (p.Skinned != null) p.Skinned.SetTrigger(AnimDie);
            if (p.Vat != null) p.Vat.TriggerDeath();
        }
    }
}
