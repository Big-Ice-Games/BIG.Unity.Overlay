using UnityEngine;
using UnityEngine.UI;

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// "Always on top" checkbox. Wires a settings Toggle to <see cref="OverlayWindow.SetAlwaysOnTop"/>:
    /// on (default) the overlay floats above every other application, off makes it an ordinary window
    /// that can hide behind others. The choice persists through IUserData and is restored on the next launch.
    /// </summary>
    public sealed class TopmostToggleView : MonoBehaviour
    {
        [SerializeField, Tooltip("Settings checkbox — wiring is done by this script.")]
        private Toggle _toggle;

        private void OnValidate()
        {
            if (_toggle == null)
                _toggle = GetComponent<Toggle>();
        }

        private void Start()
        {
            if (_toggle == null)
            {
                this.Log("No Toggle assigned.", LogLevel.Error);
                enabled = false;
                return;
            }

            // OverlayWindow reads the saved state in Awake, so it is correct here regardless of Start order.
            _toggle.SetIsOnWithoutNotify(OverlayWindow.Instance == null || OverlayWindow.Instance.IsAlwaysOnTop);
            _toggle.onValueChanged.AddListener(SetAlwaysOnTop);
        }

        /// <summary> Public — can also be wired directly to UI. </summary>
        public void SetAlwaysOnTop(bool alwaysOnTop)
        {
            if (OverlayWindow.Instance != null)
                OverlayWindow.Instance.SetAlwaysOnTop(alwaysOnTop);
        }
    }
}
