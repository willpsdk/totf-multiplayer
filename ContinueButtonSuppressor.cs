using UnityEngine;

namespace ToFMultiplayer
{

    public class ContinueButtonSuppressor : MonoBehaviour
    {
        public bool Suppress;

        private void OnEnable()
        {
            if (Suppress) gameObject.SetActive(false);
        }

        public static void SetSuppressed(GameObject menu, bool on)
        {
            if (menu == null) return;
            var s = menu.GetComponent<ContinueButtonSuppressor>();
            if (s == null) s = menu.AddComponent<ContinueButtonSuppressor>();
            s.Suppress = on;
            if (on && menu.activeSelf) menu.SetActive(false);
        }
    }
}
