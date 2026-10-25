using UnityEngine;
using UnityEngine.UI;

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// "Full screen" checkbox. Wires a settings Toggle to <see cref="OverlayWindow.SetFullscreen"/>:
    /// on, the game fills the monitor (opaque, fitted and centered on black); off, it is the
    /// transparent overlay the player can drag and scale. New players start with the overlay's
    /// Default Fullscreen; the choice persists through IUserData and is restored on the next launch.
    /// </summary>
    public sealed class FullscreenToggleView : MonoBehaviour
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
            _toggle.SetIsOnWithoutNotify(OverlayWindow.Instance != null && OverlayWindow.Instance.IsFullscreen);
            _toggle.onValueChanged.AddListener(SetFullscreen);
        }

        /// <summary> Public — can also be wired directly to UI. </summary>
        public void SetFullscreen(bool fullscreen)
        {
            if (OverlayWindow.Instance != null)
                OverlayWindow.Instance.SetFullscreen(fullscreen);
        }
    }
}
