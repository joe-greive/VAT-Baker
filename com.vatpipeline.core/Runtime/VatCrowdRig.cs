using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VatPipeline
{
    /// <summary>
    /// Spawns a crowd of VAT-baked characters, gives each one a slightly different playback
    /// rate and phase, splits them into groups, and lets you fire a specific clip at a single
    /// group. Built for two purposes: seeing whether a crowd reads as a crowd rather than a
    /// chorus line, and settling whether per-instance animation state survives batching.
    ///
    /// Every instance carries its own pose in a MaterialPropertyBlock, so they share exactly
    /// one mesh, one material and one set of textures while sitting on completely different
    /// clips at completely different times.
    ///
    /// The stats overlay is the honest way to check batching: Unity's render counters report
    /// the game view, so they are meaningless for an offscreen camera.Render() in edit mode.
    /// Press Play and read the overlay, or open the Frame Debugger, which names the reason
    /// any batch broke.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class VatCrowdRig : MonoBehaviour
    {
        [Header("Crowd")]
        [Tooltip("A baked VAT prefab. Any VatSelfDrive on it is stripped on spawn - this rig " +
                 "owns the tick, and two clocks on one player would double its speed.")]
        public GameObject Prefab;
        [Min(1)] public int Count = 237;
        [Min(1)] public int Columns = 16;
        [Min(0.1f)] public float Spacing = 1.4f;

        [Header("Groups")]
        [Tooltip("Instances are split into contiguous row bands, so which group you just " +
                 "triggered is obvious at a glance.")]
        [Min(1)] public int GroupCount = 4;

        [Header("Motion")]
        [Tooltip("Locomotion blend input for every group without its own override. " +
                 "Idle 0, walk 1.6, run 3.4.")]
        public float BaseSpeed;

        [Tooltip("Per-instance playback rate variation, as a fraction. 0.12 means each " +
                 "instance plays between 0.88x and 1.12x - enough to stop a crowd looking " +
                 "like one animation copied 237 times.")]
        [Range(0f, 0.5f)] public float RateJitter = 0.12f;

        [Tooltip("Start each instance at a random point in its cycle. Without this the whole " +
                 "crowd steps in unison, which is the giveaway that reads as fake.")]
        public bool RandomPhase = true;

        [Tooltip("One entry per group to override BaseSpeed for that group. Leave empty to " +
                 "use BaseSpeed everywhere. Shorter than GroupCount is fine.")]
        public float[] GroupSpeeds;

        [Header("Fire a clip at one group")]
        [Min(0)] public int Group;
        [Tooltip("Index into the VatPlayer's Actions: 0 attack, 1 hit, 2 death.")]
        [Min(0)] public int Action;
        [Tooltip("Tick to fire at the group above. Clears itself.")]
        public bool Fire;
        [Tooltip("Tick to fire the action at every group at once.")]
        public bool FireAll;
        [Tooltip("Tick to send everything back to locomotion.")]
        public bool ResetAll;

        [Header("Debug")]
        [Tooltip("In play mode, overlays Unity's render counters. Editor only.")]
        public bool ShowStats = true;

        readonly List<VatPlayer> _players = new List<VatPlayer>();
        readonly List<float> _rates = new List<float>();
        readonly List<int> _groups = new List<int>();
        double _lastRealtime;
        bool _cacheValid;

        void OnEnable()
        {
            _lastRealtime = Time.realtimeSinceStartupAsDouble;
            _cacheValid = false;
        }

        /// <summary>
        /// Rebuilt by scanning children rather than kept across calls: these lists do not
        /// survive a domain reload, and this rig is meant to keep working after one.
        /// </summary>
        void EnsureCache()
        {
            if (_cacheValid && _players.Count > 0) return;

            _players.Clear();
            _rates.Clear();
            _groups.Clear();

            var found = GetComponentsInChildren<VatPlayer>(true);
            int perGroup = Mathf.Max(1, Mathf.CeilToInt((float)found.Length / Mathf.Max(1, GroupCount)));

            // Deterministic jitter from the index, so a reload does not reshuffle the crowd.
            for (int i = 0; i < found.Length; i++)
            {
                _players.Add(found[i]);
                _groups.Add(Mathf.Min(GroupCount - 1, i / perGroup));
                _rates.Add(1f + (Hash01(i) * 2f - 1f) * RateJitter);
            }

            _cacheValid = true;
        }

        static float Hash01(int i)
        {
            uint h = (uint)i * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        [ContextMenu("Spawn Crowd")]
        public void SpawnCrowd()
        {
            ClearCrowd();
            if (Prefab == null)
            {
                Debug.LogError("[VatCrowdRig] No Prefab assigned.", this);
                return;
            }

            for (int i = 0; i < Count; i++)
            {
                var go = Instantiate(Prefab, transform);
                go.name = $"{Prefab.name}_{i:D3}";
                go.transform.localPosition = new Vector3(
                    (i % Columns) * Spacing, 0f, (i / Columns) * Spacing);

                // This rig is the clock. A self-drive left on the prefab would tick the same
                // player a second time and run it at double speed.
                foreach (var driver in go.GetComponentsInChildren<VatSelfDrive>(true))
                    Kill(driver);
            }

            _cacheValid = false;
            EnsureCache();
            ResetCrowd();
            Debug.Log($"[VatCrowdRig] Spawned {_players.Count} instances " +
                      $"({transform.childCount} children) in {GroupCount} groups.", this);
        }

        [ContextMenu("Clear Crowd")]
        public void ClearCrowd()
        {
            // Detach first, then destroy. Destroy outside edit mode is deferred to end of
            // frame, so the children are still parented when a Spawn immediately follows -
            // which is how a repeated Spawn silently stacked 500 into 1500.
            var doomed = new List<GameObject>(transform.childCount);
            for (int i = transform.childCount - 1; i >= 0; i--)
                doomed.Add(transform.GetChild(i).gameObject);

            foreach (var go in doomed)
            {
                go.transform.SetParent(null, false);
                Kill(go);
            }

            _players.Clear();
            _rates.Clear();
            _groups.Clear();
            _cacheValid = false;
        }

        static void Kill(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        public void ResetCrowd()
        {
            EnsureCache();
            for (int i = 0; i < _players.Count; i++)
                _players[i].Rebind(RandomPhase ? Hash01(i * 7919 + 13) : 0f);
        }

        /// <summary>Fires an action on one group, or on every group when group is negative.</summary>
        public void TriggerGroup(int group, int action)
        {
            EnsureCache();
            int fired = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                if (group >= 0 && _groups[i] != group) continue;
                _players[i].TriggerAction(action);
                fired++;
            }
            Debug.Log($"[VatCrowdRig] Fired action {action} at " +
                      (group < 0 ? "all groups" : $"group {group}") + $" ({fired} instances).", this);
        }

        /// <summary>Speed for a group, falling back to BaseSpeed when there is no override.</summary>
        public float SpeedForGroup(int group)
        {
            if (GroupSpeeds != null && group >= 0 && group < GroupSpeeds.Length)
                return GroupSpeeds[group];
            return BaseSpeed;
        }

        void Update()
        {
            EnsureCache();

            double now = Time.realtimeSinceStartupAsDouble;
            float dt = Application.isPlaying
                ? Time.deltaTime
                : Mathf.Clamp((float)(now - _lastRealtime), 0f, 0.1f);
            _lastRealtime = now;

            if (ResetAll) { ResetAll = false; ResetCrowd(); }
            if (Fire) { Fire = false; TriggerGroup(Group, Action); }
            if (FireAll) { FireAll = false; TriggerGroup(-1, Action); }

            // Step BEFORE reading input, deliberately. Input is the most fragile thing in
            // here and this rig is worthless if it stops animating; anything that throws
            // while polling a keyboard must not be able to take the crowd down with it.
            Step(dt);

            if (Application.isPlaying) HandleKeys();
        }

        /// <summary>
        /// Advances the whole crowd by one step, applying each group's speed and each
        /// instance's own playback rate. Public so a fixed-step caller (a test, a benchmark)
        /// gets exactly what Update does instead of having to reimplement it and drift.
        /// </summary>
        public void Step(float dt)
        {
            EnsureCache();
            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                if (player == null) continue;
                player.Speed = SpeedForGroup(_groups[i]);
                player.PlaybackSpeed = _rates[i];
                player.Tick(dt);
            }
        }

        /// <summary>
        /// Reads the keyboard through the Input System rather than UnityEngine.Input. Under
        /// "Input System Package (New)" the legacy API *throws* rather than returning false,
        /// and the throw kills the rest of Update — which is exactly how this rig once came to
        /// animate in edit mode and sit dead still in play mode.
        /// </summary>
        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            var digits = new[]
            {
                kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key,
                kb.digit5Key, kb.digit6Key, kb.digit7Key, kb.digit8Key, kb.digit9Key,
            };

            for (int g = 0; g < Mathf.Min(GroupCount, digits.Length); g++)
                if (digits[g].wasPressedThisFrame)
                    TriggerGroup(g, Action);

            if (kb.digit0Key.wasPressedThisFrame) TriggerGroup(-1, Action);
            if (kb.rKey.wasPressedThisFrame) ResetCrowd();
        }

#if UNITY_EDITOR
        void OnGUI()
        {
            if (!ShowStats || !Application.isPlaying) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = false };
            style.normal.textColor = Color.white;

            int instanced = UnityEditor.UnityStats.instancedBatchedDrawCalls;
            int instancedBatches = UnityEditor.UnityStats.instancedBatches;

            var text =
                $"instances: {_players.Count}   groups: {GroupCount}\n" +
                $"drawCalls: {UnityEditor.UnityStats.drawCalls}   " +
                $"setPass: {UnityEditor.UnityStats.setPassCalls}\n" +
                $"instancedBatches: {instancedBatches}   instancedDrawCalls: {instanced}\n" +
                $"srpBatcherDrawCalls: {UnityEditor.UnityStats.srpBatcherDrawCalls}\n" +
                $"visibleSkinnedMeshes: {UnityEditor.UnityStats.visibleSkinnedMeshes}\n" +
                $"triangles: {UnityEditor.UnityStats.triangles}   " +
                $"verts: {UnityEditor.UnityStats.vertices}\n" +
                // UnityStats.frameTime/renderTime are deprecated in Unity 6; smoothDeltaTime
                // is close enough for a live sanity read and carries no warning.
                $"frame: {Time.smoothDeltaTime * 1000f:F2} ms " +
                $"({(Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f):F0} fps)\n" +
                $"keys: 1-{Mathf.Min(GroupCount, 9)} fire group, 0 fire all, R reset";

            GUI.Box(new Rect(8, 8, 430, 168), GUIContent.none);
            GUI.Label(new Rect(16, 12, 420, 160), text, style);
        }
#endif
    }
}
