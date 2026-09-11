# BIG.Unity.Overlay

Turns a Unity game into a desktop overlay: an invisible window stretched over ALL monitors, with your
game panel floating on top of it like a sticker — drag it anywhere across the desktop, scale it with
the mouse wheel, click through everything outside it. Windows standalone builds. Built on
[BIG.Unity](https://github.com/Big-Ice-Games/BIG.Unity) and the free
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

## How it works

The OS window is stretched over the entire virtual desktop and NEVER moves or resizes — so there is
nothing to flicker. Dragging and scaling happen on the CONTENT rect (your game panel) with pure uGUI,
smoothly, across monitor boundaries. Click-through geometry keeps the desktop fully usable outside
your UI.

## Step by step

### 1. Setup
1. Add UniWinC's `UniWindowController` prefab/component to your startup scene.
2. On `UniWindowController`: enable **Is Transparent**, disable **Fit to monitor**.
3. Add **OverlayWindow** to any scene object and assign the `UniWindowController` reference.
4. Assign **Content** — the RectTransform of your whole game panel (single-point anchors, any pivot).
5. Assign **Move Handle** — the rect the player grabs to drag the content (e.g. a title label).
6. Set **Start Corner** and the **Min/Max Scale** constraints.

The game starts hidden off-screen and appears already transparent, with the content flush to the chosen
corner of the primary monitor's work area (above the taskbar). After the player drags or scales the
content, its position and scale persist and are restored on the next launch. Corners stay available as
one-shot moves — `OverlayWindow.Instance.SnapContentToCorner(...)` or wire a button to `SnapContentToCorner(int)`.

### 2. Click-through and hover
1. Fill the **Hit Area** list with the RectTransforms of your interactive panels (the Move Handle is
   included automatically).
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

### 3. Drag and scale
* Grab the **Move Handle** → the content follows the cursor anywhere on the desktop, across monitors.
  The handle can safely be the WHOLE content: a press on an interactive element (button, slider,
  anything with pointer/drag handlers — e.g. a chess piece) never starts the drag.
* Scroll over the hit area → the content scales within **Min/Max Scale** (**Scroll Scale Step** per notch;
  set it to 0 when your game uses the wheel itself and call `SetContentScale` on your own).
* Position and scale save automatically (on release / when the cursor leaves the overlay).

### 4. Mouse wheel without focus
Scroll scaling already works without focus. For your own wheel input read it through the plugin:

```csharp
float notches = GlobalMouseScroll.ConsumeNotches(); // per frame, positive = up
```

Note: `OverlayWindow` drains this counter for scroll scaling — with your own wheel logic set
**Scroll Scale Step** to 0 and consume the notches yourself.

### 5. Autostart with Windows
Add **AutostartToggleView** to a `Toggle` in your settings UI. Optionally set **Steam App Id** —
the shortcut then launches the game through the Steam client; with 0 it points straight at the exe.

### 6. Settings keys
Content position/scale are stored through BIG's `IUserData`. Run **BIG > Generate User Keys** to access
them in code as `Keys.Overlay.*`.

## Notes

* Everything is editor-safe: in the editor the window is left alone (UniWinC would move the Game View).
* The Canvas should be Screen Space - Overlay (or a camera canvas covering the window) — it spans the
  whole virtual desktop.
* The window renders at virtual-desktop resolution — verify GPU cost on low-end machines.
* Mixed-DPI monitor setups may render the overlay scaled/blurry on non-primary monitors
  (standard Windows behavior for a window spanning monitors).
* With Odin Inspector in the project the `OverlayWindow` fields show up in foldout groups.

## License

MIT — see [LICENSE.md](LICENSE.md). This package redistributes no third-party code —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for required packages.
