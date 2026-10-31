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
    /// A product rename used to orphan the old '&lt;old name&gt;.url': the checkbox showed OFF while Windows
    /// kept launching the game. Every shortcut that targets this game (current name, legacy names, or a
    /// URL pointing at this Steam id / an exe in this build's folder) now counts as ON, and OFF deletes all.
    /// </summary>
    public sealed class AutostartToggleView : MonoBehaviour
    {
        [SerializeField, Tooltip("Settings checkbox — wiring is done by this script.")]
        private Toggle _toggle;

        [SerializeField, Tooltip("Shortcut file name in the Startup folder. Empty = '<product name>.url'. Set explicitly to survive a product rename.")]
        private string _shortcutFileName;

        [SerializeField, Tooltip("Steam AppId for launching through the Steam client (playtime and Steam overlay work). 0 = shortcut points straight at the exe.")]
        private uint _steamAppId;

        [SerializeField, Tooltip("File names this game's shortcut had under OLDER product names (e.g. 'Old Name.url'). " +
            "They count as 'autostart on' and are removed when the player turns it off — a rename must never leave " +
            "an invisible shortcut that keeps launching the game.")]
        private string[] _legacyShortcutFileNames;

        private string FileName => string.IsNullOrEmpty(_shortcutFileName)
            ? Application.productName + ".url"
            : _shortcutFileName;

        private static string StartupFolder
            => System.Environment.GetFolderPath(System.Environment.SpecialFolder.Startup);

        private string ShortcutPath => Path.Combine(StartupFolder, FileName);

        /// <summary> The URL this build writes into its shortcut. </summary>
        private string TargetUrl => _steamAppId != 0
            ? $"steam://rungameid/{_steamAppId}"
            : new System.Uri(Path.Combine(
                Path.GetDirectoryName(Application.dataPath)!,
                Application.productName + ".exe")).AbsoluteUri;

        /// <summary>
        /// Every shortcut in the Startup folder that launches THIS game: the current name, the legacy names,
        /// and any other .url whose target is this game's Steam id or an exe in this build's folder
        /// (catches shortcuts written under a product name nobody listed).
        /// </summary>
        private System.Collections.Generic.List<string> FindShortcuts()
        {
            var found = new System.Collections.Generic.List<string>();
            string folder = StartupFolder;
            if (!Directory.Exists(folder)) return found;

            if (File.Exists(ShortcutPath)) found.Add(ShortcutPath);
            if (_legacyShortcutFileNames != null)
            {
                foreach (string name in _legacyShortcutFileNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path) && !found.Contains(path)) found.Add(path);
                }
            }

            string steamUrl = _steamAppId != 0 ? $"steam://rungameid/{_steamAppId}" : null;
            string buildFolderUrl = new System.Uri(Path.GetDirectoryName(Application.dataPath)! + Path.DirectorySeparatorChar).AbsoluteUri;
            foreach (string path in Directory.GetFiles(folder, "*.url"))
            {
                if (found.Contains(path)) continue;
                string url = ReadUrl(path);
                if (url == null) continue;
                bool steamMatch = steamUrl != null && url == steamUrl;
                bool exeMatch = url.StartsWith(buildFolderUrl, System.StringComparison.OrdinalIgnoreCase)
                    && url.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase)
                    && url.IndexOf('/', buildFolderUrl.Length) < 0;
                if (steamMatch || exeMatch) found.Add(path);
            }
            return found;
        }

        private static string ReadUrl(string path)
        {
            try
            {
                foreach (string line in File.ReadAllLines(path))
                    if (line.StartsWith("URL=", System.StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
            }
            catch { }
            return null;
        }

        private bool IsEnabled
        {
            get
            {
                try { return FindShortcuts().Count > 0; }
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
                // Old shortcuts (legacy names / other names pointing at this game) are always removed —
                // turning it off kills every one of them, turning it on migrates to the current name.
                foreach (string path in FindShortcuts())
                {
                    if (enabledByPlayer && path == ShortcutPath) continue;
                    File.Delete(path);
                }

                if (enabledByPlayer)
                {
                    // steam:// is taken over by the Steam client; without an AppId the shortcut
                    // points straight at the built exe (file:///, spaces encoded).
                    // InternetShortcut format — Windows opens the URL with its default handler.
                    File.WriteAllText(ShortcutPath,
                        "[InternetShortcut]\r\n" +
                        $"URL={TargetUrl}\r\n");
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
