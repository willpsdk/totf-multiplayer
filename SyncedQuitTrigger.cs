using System.Collections;
using MelonLoader;
using ProgressBar;
using TotF;
using UnityEngine;

namespace ToFMultiplayer
{

    public class SyncedQuitTrigger : MonoBehaviour
    {
        public ProgressRadialBehaviour ProgressBar;
        public MultiplayerPlugin Plugin;

        private static readonly float TriggerTime = QuitTrigger.triggerTime;

        private bool _isTriggered;
        private bool _triggerState;
        private bool _isCounting;
        private float _count;
        private bool _fired; // guard: only fire once per activation

        private void OnTriggerEnter(Collider col)
        {
            if (col.CompareTag("PlayerFist"))
                _isTriggered = true;
        }

        private void OnTriggerExit(Collider col)
        {
            if (col.CompareTag("PlayerFist"))
                _isTriggered = false;
        }

        private void OnDisable() => ResetTimer();

        private void Update()
        {
            // Mirror vanilla QuitTrigger state machine
            if (_isTriggered != _triggerState)
            {
                _triggerState = _isTriggered;
                if (_isTriggered) StartTimer();
                else ResetTimer();
            }

            if (!_isCounting) return;

            _count += Time.deltaTime;
            if (ProgressBar != null)
                ProgressBar.SetFillerSize(_count / TriggerTime);

            if (_count > TriggerTime && !_fired)
            {
                _fired = true;
                ResetTimer();
                MelonLogger.Msg("[SyncedQuit] Hold complete — syncing retire with remote");
                MelonCoroutines.Start(SyncedRetire());
            }
        }

        private IEnumerator SyncedRetire()
        {
            // 1. Tell the remote player to retire
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsConnected)
            {
                nm.SendRetire();
                MelonLogger.Msg("[SyncedQuit] RETIRE packet sent — waiting 0.4s before local retire");
                yield return new WaitForSeconds(0.4f);
            }

            // 2. Retire locally
            if (Plugin != null) Plugin.multiplayerBoutActive = false;
            MelonLogger.Msg("[SyncedQuit] Calling BoutController.Retire() locally");
            try { BoutController.Retire(); }
            catch (System.Exception e)
            {
                MelonLogger.Error($"[SyncedQuit] BoutController.Retire() error: {e.Message}");
            }
        }

        private void StartTimer()
        {
            _count = 0f;
            _isCounting = true;
        }

        private void ResetTimer()
        {
            if (ProgressBar != null) ProgressBar.SetFillerSize(0f);
            _count = 0f;
            _isCounting = false;
            _isTriggered = false;
            _fired = false;
        }
    }
}