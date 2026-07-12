using System;
using MelonLoader;
using ProgressBar;
using TotF;
using UnityEngine;

namespace ToFMultiplayer
{

    public class MultiplayerReadyUpTrigger : MonoBehaviour
    {
        public enum TriggerMode { ReadyUp, BreakSkip, Rematch }

        public ProgressRadialBehaviour ProgressBar;
        public TriggerMode Mode = TriggerMode.ReadyUp;

        // Mirror the game's hold duration exactly
        private static float HoldDuration => ContinueTrigger.triggerTime;

        private bool _isFistInside;
        private bool _triggerState;
        private bool _isCounting;
        private float _count;
        private bool _fired;
        private ContinueTrigger _continueTrigger; // cached to suppress re-enable

        public bool Fired
        {
            get => _fired;
            set => _fired = value;
        }

        // --------------------------------------------------------

        private void OnEnable()
        {
            _fired = false;
            ResetTimer();
        }

        private void OnDisable()
        {
            ResetTimer();
        }

        private void OnTriggerEnter(Collider collider)
        {
            if (collider.CompareTag("PlayerFist"))
                _isFistInside = true;
        }

        private void OnTriggerExit(Collider collider)
        {
            if (collider.CompareTag("PlayerFist"))
                _isFistInside = false;
        }

        private void Update()
        {
            // The game re-enables ContinueTrigger when the player steps back out of the
            // corner zone. Stomp it every frame so it can never fire during multiplayer.
            if (_continueTrigger == null)
                _continueTrigger = GetComponent<ContinueTrigger>();
            if (_continueTrigger != null && _continueTrigger.enabled)
                _continueTrigger.enabled = false;

            if (_fired) return;

            if (_isFistInside != _triggerState)
            {
                _triggerState = _isFistInside;
                if (_isFistInside) StartTimer();
                else ResetTimer();
            }

            if (!_isCounting) return;

            _count += Time.deltaTime;
            SetProgress(Mathf.Clamp01(_count / HoldDuration));

            if (_count >= HoldDuration)
            {
                SetProgress(0f);
                ResetTimer();
                Fire();
            }
        }

        private void StartTimer()
        {
            _count = 0f;
            _isCounting = true;
            MelonLogger.Msg($"[ReadyUp] Holding {Mode} button...");
        }

        private void ResetTimer()
        {
            _count = 0f;
            _isCounting = false;
            _isFistInside = false;
            _triggerState = false;
            SetProgress(0f);
        }

        private void Fire()
        {
            if (_fired) return;
            _fired = true;
            MelonLogger.Msg($"[ReadyUp] ✓ {Mode} hold complete");

            try
            {
                if (Mode == TriggerMode.ReadyUp)
                    MultiplayerPlugin.Instance?.OnLocalPlayerReadiedUp();
                else if (Mode == TriggerMode.BreakSkip)
                    MultiplayerPlugin.Instance?.OnLocalBreakSkipVote();
                else
                    MultiplayerPlugin.Instance?.OnLocalRematchVote();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[ReadyUp] Fire error: {e.Message}");
            }
        }

        private void SetProgress(float v)
        {
            try { ProgressBar?.SetFillerSize(v); } catch { }
        }
    }
}