using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Overlay.Editor
{
    /// <summary>
    /// Always-compiling dependency checker. The runtime assembly is gated by defineConstraints (BIG_UNIWINC),
    /// so a missing UniWinC produces no compile errors — this class tells the developer what to install instead.
    /// </summary>
    [InitializeOnLoad]
    internal static class OverlayDependencyChecker
    {
        static OverlayDependencyChecker()
        {
            bool installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Any(p => p.name == "com.kirurobo.uniwinc");

            if (!installed)
            {
                Debug.LogError("[BIG] BIG.Unity.Overlay requires UniWinC. Install it via Package Manager > Install package from git URL: https://github.com/kirurobo/UniWindowController.git#upm");
            }
        }
    }
}
