using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// "Launch on Windows startup" checkbox. Mechanism: a .url file in the user's Startup folder
    /// (shell:startup) with a steam://rungameid URL (when Steam AppId is set) or a direct file:/// link
    /// to the exe. No registry and no COM — a plain text file, IL2CPP-safe. The source of truth is the
    /// file's existence, not any saved preference — if the player removes the shortcut manually,
    /// the checkbox simply shows the actual state. Hidden on non-Windows platforms.
    /// </summary>
    public sealed class AutostartToggleView : MonoBehaviour
    {
        [SerializeField, Tooltip("Settings checkbox — wiring is done by this script.")]
        private Toggle _toggle;

        [SerializeField, Tooltip("Shortcut file name in the Startup folder. Empty = '<product name>.url'. Set explicitly to survive a product rename.")]
        private string _shortcutFileName;

        [SerializeField, Tooltip("Steam AppId for launching through the Steam client (playtime and Steam overlay work). 0 = shortcut points straight at the exe.")]
        private uint _steamAppId;

        private string FileName => string.IsNullOrEmpty(_shortcutFileName)
            ? Application.productName + ".url"
            : _shortcutFileName;

        private string ShortcutPath
            => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Startup), FileName);

        private bool IsEnabled
        {
            get
            {
                try { return File.Exists(ShortcutPath); }
                catch { return false; }
            }
        }

        private void OnValidate()
        {
            if (_toggle == null)
                _toggle = GetComponent<Toggle>();
        }

        private void Start()
        {
#if !UNITY_STANDALONE_WIN && !UNITY_EDITOR_WIN
            gameObject.SetActive(false); // this form of autostart is Windows-only
#else
            if (_toggle == null)
            {
                this.Log("No Toggle assigned.", LogLevel.Error);
                enabled = false;
                return;
            }

            _toggle.SetIsOnWithoutNotify(IsEnabled);
            _toggle.onValueChanged.AddListener(SetAutostart);
#endif
        }

        /// <summary> Public — can also be wired directly to UI. </summary>
        public void SetAutostart(bool enabledByPlayer)
        {
            try
            {
                if (enabledByPlayer)
                {
                    // steam:// is taken over by the Steam client; without an AppId the shortcut
                    // points straight at the built exe (file:///, spaces encoded).
                    string url = _steamAppId != 0
                        ? $"steam://rungameid/{_steamAppId}"
                        : new System.Uri(Path.Combine(
                            Path.GetDirectoryName(Application.dataPath)!,
                            Application.productName + ".exe")).AbsoluteUri;

                    // InternetShortcut format — Windows opens the URL with its default handler.
                    File.WriteAllText(ShortcutPath,
                        "[InternetShortcut]\r\n" +
                        $"URL={url}\r\n");
                }
                else if (File.Exists(ShortcutPath))
                {
                    File.Delete(ShortcutPath);
                }
            }
            catch (System.Exception e)
            {
                // E.g. company policy blocking the Startup folder — revert the checkbox to the actual state.
                this.Log($"Autostart change failed: {e.Message}", LogLevel.Error);
                _toggle.SetIsOnWithoutNotify(IsEnabled);
            }
        }
    }
}
