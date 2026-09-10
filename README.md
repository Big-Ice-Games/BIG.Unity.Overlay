# BIG.Unity.Overlay

Turns a Unity game into a transparent desktop overlay snapped to a screen corner (like Snap Chess):
corner and monitor selection, click-through outside your UI, drag with corner snapping, mouse wheel
without focus and Windows autostart. Windows standalone builds.

## Installation

1. Package Manager > `+` > `Install package from git URL`, in this order:
   ```
   https://github.com/Big-Ice-Games/BIG.Unity.git
   ```
   ```
   https://github.com/kirurobo/UniWindowController.git#upm
   ```
   ```
   https://github.com/Big-Ice-Games/BIG.Unity.Overlay.git
   ```
2. Until UniWinC is installed the package does not compile and the console tells you what is missing.

## Step by step

### 1. Window setup
1. Add UniWinC's `UniWindowController` prefab/component to your startup scene.
2. On `UniWindowController`: enable **Is Transparent**, disable **Fit to monitor**.
3. Add the **OverlayWindow** component to any scene object and assign the `UniWindowController` reference.
4. Set **Window Size** — the OS window never resizes at runtime; scale your content inside it.

Done: the game starts hidden off-screen and appears already transparent in the bottom-right corner
of the work area (above the taskbar). Corner and monitor choices persist between sessions automatically.

### 2. Make your UI clickable (click-through)
1. On `OverlayWindow`, fill the **Hit Area** list with the RectTransforms of your interactive panels.
2. Everything outside these rects lets clicks fall through to the desktop; inactive objects are skipped.
3. To fade your overlay in/out on hover, subscribe:

```csharp
public class MyOverlayView : BaseBehaviour
{
    [Subscribe]
    private void OnHover(OverlayHoverChanged e) { /* e.IsOver → fade in, else fade out */ }
}
```

### 3. Corner switching
* From UI buttons: wire `Button.onClick` to `OverlayWindow.SetCorner(int)`
  (0 = BottomRight, 1 = BottomLeft, 2 = TopRight, 3 = TopLeft).
* Monitor dropdown: build options from `OverlayWindow.MonitorCount`, wire to `OverlayWindow.SetMonitor(int)`.
* To re-anchor panels when the corner changes, fill the **Corner Layout** list on `OverlayWindow`:
  * `WindowCorner` — element sticks to the window corner matching the screen corner,
  * `InnerHorizontal` / `InnerVertical` — element sticks to the side facing the screen center
    (e.g. a settings bar next to the board, a text panel above/below it).
* Game-specific reactions (animations, mirroring): subscribe to `OverlayCornerChanged`.

### 4. Dragging the window
1. Add **OverlayDragHandle** to a RectTransform that should act as the grab handle.
2. Add that same RectTransform to the **Hit Area** list (otherwise it is click-through and cannot be grabbed).
3. Drag moves the whole OS window; on release it snaps to the nearest corner of the monitor it was dropped on.

### 5. Mouse wheel without focus
Read the wheel through the plugin instead of Unity input — it works even when another window has focus:

```csharp
float notches = GlobalMouseScroll.ConsumeNotches(); // per frame, positive = up
```

### 6. Autostart with Windows
1. Add **AutostartToggleView** to a `Toggle` in your settings UI.
2. Optionally set **Steam App Id** — the shortcut then launches the game through the Steam client;
   with 0 it points straight at the exe.

### 7. Settings keys
Corner and monitor are stored through BIG's `IUserData`. Run **BIG > Generate User Keys** to access
them in code as `Keys.Overlay.Corner` and `Keys.Overlay.Monitor`.

## Notes

* Everything is editor-safe: in the editor the window is left alone (UniWinC would move the Game View).
* With Odin Inspector in the project the `OverlayWindow` fields show up in foldout groups.

## License

MIT — see [LICENSE.md](LICENSE.md). This package redistributes no third-party code —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for required packages.
