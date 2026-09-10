#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using UnityEngine;

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// Parks the game window off-screen at the earliest Unity startup stages, before the first frame renders,
    /// so the user never sees the flash of a not-yet-transparent window. The window comes back on screen
    /// only when <see cref="OverlayWindow"/> places it in the target corner with transparency already enabled.
    /// The handle can be unavailable at the earliest stages, hence parking retries on each of them.
    /// </summary>
    public static class OverlayBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ParkOnSubsystemRegistration() => NativeWindow.ParkOffScreen();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void ParkOnAssembliesLoaded() => NativeWindow.ParkOffScreen();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void ParkOnBeforeSplashScreen() => NativeWindow.ParkOffScreen();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ParkOnBeforeSceneLoad() => NativeWindow.ParkOffScreen();

        // A parked window MUST be restored by OverlayWindow — when the scene has none, we spawn it here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureOverlayWindowExists()
        {
            if (Object.FindAnyObjectByType<OverlayWindow>() == null)
                new GameObject(nameof(OverlayWindow), typeof(OverlayWindow));
        }
    }
}
#endif
