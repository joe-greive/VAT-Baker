using UnityEngine;

namespace VatPipeline
{
    /// <summary>
    /// Ticks a <see cref="VatPlayer"/> from Update, in play mode or in the editor. Only for
    /// previewing a baked character outside the enemy loop — live enemies are driven by
    /// EnemyManager, which is the whole reason VatPlayer has no Update of its own. Do not put
    /// this on a pooled prefab.
    ///
    /// In edit mode the Scene view only repaints on demand, so the preview looks frozen or
    /// choppy unless "Always Refresh" is enabled in the Scene view's toolbar.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VatPlayer))]
    public sealed class VatSelfDrive : MonoBehaviour
    {
        public VatPlayer Player;

        [Range(0f, 4f)]
        [Tooltip("Feeds VatPlayer.Speed, so you can scrub the locomotion blend live. " +
                 "Idle below 1.6, walk to 3.4, run above.")]
        public float Speed;

        [Tooltip("Fires this action index on a loop. -1 to leave locomotion alone. " +
                 "0 = attack, 1 = hit, 2 = death on a standard bake.")]
        public int LoopAction = -1;

        public float ActionInterval = 2f;

        float _timer;
        double _lastRealtime;

        void OnEnable()
        {
            if (Player == null) Player = GetComponent<VatPlayer>();
            _lastRealtime = Time.realtimeSinceStartupAsDouble;
        }

        void Update()
        {
            if (Player == null) return;

            // Time.deltaTime is not meaningful for editor updates, so keep our own clock and
            // clamp it — a domain reload or a stalled repaint can otherwise hand us a
            // multi-second step that skips a whole clip.
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = Application.isPlaying
                ? Time.deltaTime
                : Mathf.Clamp((float)(now - _lastRealtime), 0f, 0.1f);
            _lastRealtime = now;

            Player.Speed = Speed;
            Player.Tick(dt);

            if (LoopAction < 0) return;

            _timer += dt;
            if (_timer >= ActionInterval)
            {
                _timer = 0f;
                Player.TriggerAction(LoopAction);
            }
        }
    }
}
