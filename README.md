# BIG.Unity.Overlay

Turns a Unity game into a transparent desktop overlay that behaves like a normal window: drag it
anywhere (across monitors), resize it within constraints, click through everything outside your UI.
Windows standalone builds. Built on [BIG.Unity](https://github.com/Big-Ice-Games/BIG.Unity) and the free
[UniWinC](https://github.com/kirurobo/UniWindowController).

## Installation

Package Manager > `+` > `Install package from git URL`, in this order:

```
https://github.com/Big-Ice-Games/BIG.Unity.git
```
```
https://github.com/kirurobo/UniWindowController.git#upm
```
```
https://github.com/Big-Ice-Games/BIG.Unity.Overlay.git
```

Until UniWinC is installed the package does not compile and the console tells you what is missing.

## Step by step

### 1. Window
1. Add UniWinC's `UniWindowController` prefab/component to your startup scene.
2. On `UniWindowController`: enable **Is Transparent**, disable **Fit to monitor**.
3. Add **OverlayWindow** to any scene object and assign the `UniWindowController` reference.
4. Set **Start Corner** + **Window Size** (first launch) and **Min/Max Window Size** (resize constraints).

The game starts hidden off-screen and appears already transparent, flush with the chosen corner of the
work area (above the taskbar). After the player drags or resizes the window, its position and size are
persisted and restored on the next launch. Corners are one-shot moves — you can always call
`OverlayWindow.Instance.SnapToCorner(...)` from code or wire a button to `SnapToCorner(int)`.

### 2. Click-through and hover
1. Fill the **Hit Area** list with the RectTransforms of your interactive panels (handles are included automatically).
2. Everything outside these rects lets clicks fall through to the desktop; inactive objects are skipped.
3. Hover hooks, pick whichever fits:
   * **OnCursorEnter / OnCursorExit** UnityEvents in the inspector (e.g. show/hide side panels),
   * `OverlayWindow.Instance.IsCursorOver` in code,
   * the `OverlayHoverChanged` BIG event:

```csharp
public class MyOverlayView : BaseBehaviour
{
    [Subscribe]
    private void OnHover(OverlayHoverChanged e) { /* e.IsOver → fade in, else fade out */ }
}
```

### 3. Drag and resize
No extra components — assign two rects on `OverlayWindow`:
1. **Move Handle** — any rect (e.g. the title label); grab it to drag the whole OS window.
2. **Resize Handle** — a bottom-right corner grip; drag to resize within Min/Max Window Size
   (top-left corner stays in place).

Both handles count as hit area automatically; position and size save on release.

### 4. Mouse wheel without focus
Read the wheel through the plugin instead of Unity input — it works even when another window has focus:

```csharp
float notches = GlobalMouseScroll.ConsumeNotches(); // per frame, positive = up
```

### 5. Autostart with Windows
Add **AutostartToggleView** to a `Toggle` in your settings UI. Optionally set **Steam App Id** —
the shortcut then launches the game through the Steam client; with 0 it points straight at the exe.

### 6. Settings keys
Window position/size are stored through BIG's `IUserData`. Run **BIG > Generate User Keys** to access
them in code as `Keys.Overlay.*`.

## Notes

* Everything is editor-safe: in the editor the window is left alone (UniWinC would move the Game View).
* With Odin Inspector in the project the `OverlayWindow` fields show up in foldout groups.
* Monitors with different DPI scale the window on crossing — standard Windows behavior.

## License

MIT — see [LICENSE.md](LICENSE.md). This package redistributes no third-party code —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for required packages.
