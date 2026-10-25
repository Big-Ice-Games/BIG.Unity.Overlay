using System.Collections;
using System.Collections.Generic;
using Kirurobo;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace BIG.Unity.Overlay
{
    public enum OverlayCorner
    {
        BottomRight = 0,
        BottomLeft = 1,
        TopRight = 2,
        TopLeft = 3,
    }

    /// <summary>
    /// How the OS window relates to the content.
    /// GPU note: with a transparent overlay EVERY window pixel is paid for THREE times per
    /// frame (Unity renders the backbuffer, the legacy BitBlt swapchain copies it, DWM
    /// composites it with alpha over the desktop) — the window area, not the content, is
    /// the overlay's GPU bill.
    /// </summary>
    public enum OverlayWindowMode
    {
        /// <summary>
        /// The window invisibly covers ALL monitors and never moves; only the content rect
        /// travels inside it. Zero window moves/resizes, but the full virtual desktop is
        /// rendered and composited every frame — fine for simple overlays, expensive on
        /// big/multi-monitor setups.
        /// </summary>
        CoverDesktop = 0,
        /// <summary>
        /// The window is exactly the scaled content (+ Window Margin) and MOVES with it —
        /// dragging the content drags the OS window, scaling resizes it. The backbuffer and
        /// the DWM composition pay only for the panel, not the desktop (the fix for the
        /// "idle overlay eats half the GPU" problem).
        /// </summary>
        FitContent = 1,
    }

    /// <summary> Raised when the global cursor enters or leaves the overlay hit area. </summary>
    public readonly struct OverlayHoverChanged
    {
        public OverlayHoverChanged(bool isOver) => IsOver = isOver;
        public readonly bool IsOver;
    }

    /// <summary>
    /// Overlay keys for <see cref="IUserData"/>. Run BIG > Generate User Keys to access them as Keys.Overlay.*.
    /// </summary>
    public class OverlayUserDataKeysProvider : IUserDataKeysProvider
    {
        public string Name => "Overlay";

        public static readonly UserDataKey Saved = new UserDataKey("OVERLAY_SAVED");
        public static readonly UserDataKey PositionX = new UserDataKey("OVERLAY_POS_X");
        public static readonly UserDataKey PositionY = new UserDataKey("OVERLAY_POS_Y");
        public static readonly UserDataKey Scale = new UserDataKey("OVERLAY_SCALE");
        public static readonly UserDataKey Topmost = new UserDataKey("OVERLAY_TOPMOST");
        public static readonly UserDataKey Fullscreen = new UserDataKey("OVERLAY_FULLSCREEN");
    }

    /// <summary>
    /// The one overlay component.
    ///
    /// FULLSCREEN (2026-10-25, <see cref="SetFullscreen"/>, default for new players via Default Fullscreen):
    /// an opaque window over the whole monitor, the content fitted and centered on black — no drag,
    /// no scroll zoom, no click-through. Leaving it restores the overlay spot/scale saved on entry.
    ///
    /// WINDOW — two modes (<see cref="OverlayWindowMode"/>):
    /// CoverDesktop (legacy default): an invisible layer stretched over the ENTIRE virtual desktop
    /// (all monitors). It never moves and never resizes, so there is no window flicker — what the player
    /// drags and scales is the CONTENT rect, with pure uGUI. SIMPLE but GPU-EXPENSIVE: a transparent
    /// window pays per PIXEL OF WINDOW AREA three times a frame (Unity backbuffer render, the legacy
    /// BitBlt swapchain copy, DWM alpha composition) — on a multi-monitor desktop that is most of a GPU.
    /// FitContent: the window wraps the scaled content (+ Window Margin) and MOVES with it — dragging
    /// the content drags the OS window (global-cursor driven), scaling resizes it around its center.
    /// The desktop is never rendered, only the panel. Prefer this for games; the cost is live window
    /// moves/resizes during gestures (imperceptible on DWM in practice).
    /// Either way the window starts parked off-screen (see OverlayBootstrap) and shows up only here,
    /// already transparent.
    ///
    /// CONTENT — on first launch the content fills the primary monitor's work area (above the taskbar) —
    /// the overlay's "fullscreen"; scroll shrinks it into a panel. With Start Fullscreen off it aligns flush
    /// to Start Corner instead. Fullscreen and corners are one-shot moves (<see cref="FitContentToMonitor"/>,
    /// <see cref="SnapContentToCorner(OverlayCorner)"/>) — no state is kept beyond position and scale.
    /// After the player drags or scales the content, its position and scale persist through
    /// <see cref="IUserData"/> and are restored on the next launch (with a fallback to Start Corner when that spot
    /// is no longer on any monitor). Grab the Move Handle to drag; scroll over the hit area to scale
    /// within Min/Max Scale (uses <see cref="GlobalMouseScroll"/>, so it works without window focus).
    /// The content rect should use single-point anchors (anchorMin == anchorMax).
    ///
    /// HIT AREA — click-through controlled by GEOMETRY instead of UniWinC's per-pixel opacity test
    /// (with per-pixel testing a fully hidden overlay becomes a permanent "hole" that can never detect hover).
    /// Cursor over any active hit rect (or the Move Handle) → window clickable, outside → clicks fall through
    /// to the desktop. Hover is exposed three ways: <see cref="IsCursorOver"/>, the <see cref="OverlayHoverChanged"/>
    /// BIG event and the OnCursorEnter/OnCursorExit UnityEvents below.
    /// </summary>
    public sealed class OverlayWindow : BaseBehaviour
    {
        public static OverlayWindow Instance { get; private set; }

        [Inject] private IUserData _userData;

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#else
        [Header("Window")]
#endif
        [SerializeField] private UniWindowController _uniWindowController;

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField, Tooltip("CoverDesktop: the window spans all monitors and never moves (simple, GPU-expensive). FitContent: the window is the scaled content + margin and moves with it (the whole desktop is no longer rendered/composited every frame).")]
        private OverlayWindowMode _windowMode = OverlayWindowMode.CoverDesktop;

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField, Tooltip("FitContent only: transparent padding around the content in physical pixels — room for drop shadows or popups that overhang the panel edge.")]
        private float _windowMargin = 0f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#else
        [Header("Content")]
#endif
        [SerializeField, Tooltip("The draggable game panel. Use single-point anchors (anchorMin == anchorMax); any pivot works.")]
        private RectTransform _content;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("On first launch the content fills the primary monitor's work area (scroll shrinks it from there). Off = the content parks in Start Corner instead.")]
        private bool _startFullscreen = true;

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField, Tooltip("FULLSCREEN MODE for players who never chose: an opaque window covering the whole monitor, the content scaled to fit and centered on black (bars at the sides of a square game). Off = the transparent overlay. The player's choice (SetFullscreen) persists and wins.")]
        private bool _defaultFullscreen = true;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Corner of the primary monitor's work area the content aligns to on first launch when Start Fullscreen is off (and the fallback when the saved spot left every monitor). One-shot — no corner state is kept afterwards.")]
        private OverlayCorner _startCorner = OverlayCorner.BottomRight;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Grab this rect to drag the content around the desktop — can safely be the WHOLE content: presses on Selectables (buttons, sliders...), inside any ScrollRect (panning scrolls the content, not the window) or inside a Drag Exception rect never start the drag, and the game can reclaim a gesture with CancelDrag() (e.g. when a chess piece got picked up). Counts as hit area automatically.")]
        private RectTransform _moveHandle;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Presses inside these rects never start the content/window drag — manual exceptions for areas with their own drag gestures that are neither Selectables nor ScrollRects. Inactive objects are skipped; AddDragException/RemoveDragException manage the list at runtime.")]
        private List<RectTransform> _dragExceptionRects = new List<RectTransform>();

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Scroll over the hit area scales the content. 0 disables scroll scaling (e.g. when the game uses the wheel itself).")]
        private float _scrollScaleStep = 0.1f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Content scale constraints for scroll scaling (and for the restored scale).")]
        private float _minScale = 0.5f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField] private float _maxScale = 2f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#else
        [Header("Hit Area")]
#endif
        [SerializeField, Tooltip("Cursor over ANY of these rects (or the Move Handle) = window clickable, outside = click-through. Inactive objects are skipped. Empty list + no handle = click-through stays in UniWinC mode.")]
        private List<RectTransform> _hitRects = new List<RectTransform>();

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#endif
        [SerializeField, Tooltip("Extra margin around the hit rects in screen pixels — easier to hit a hidden overlay with the cursor.")]
        private float _hitPadding = 8f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#endif
        [SerializeField, Tooltip("Invoked when the global cursor enters the hit area (window becomes clickable).")]
        private UnityEvent _onCursorEnter;

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#endif
        [SerializeField, Tooltip("Invoked when the global cursor leaves the hit area (window becomes click-through).")]
        private UnityEvent _onCursorExit;

        private Camera _uiCamera;
        private bool _hitAreaActive;
        private bool _lastClickThrough = true;
        private bool _cursorOver;

        // Drag starts ARMED on press and moves the content only after the cursor travels DRAG_THRESHOLD
        // pixels — a plain click never nudges the content, and uGUI (piece drag) gets time to claim the gesture.
        private const float DRAG_THRESHOLD = 8f;

        private bool _dragArmed;
        private bool _dragging;
        private bool _mouseWasPressed;
        private Vector2 _pressScreenPoint;
        private Vector2 _dragStartContentPosition;
        private Vector2 _dragOffset;
        private float _scale = 1f;
        private bool _stateDirty;
        private bool _alwaysOnTop = true;
        private bool _fullscreen;
        private bool _windowReady; // Start placed the window — mode switches apply live from here on

        // FitContent mode: the window's bottom-left in UniWinC coordinates (the one source of
        // truth — the controller's getter can lag a frame behind a just-issued move).
        private Vector2 _windowUniwinPosition;
        private Vector2 _pressCursorUniwin;
        private Vector2 _dragStartWindowPosition;
        private Vector2 _dragOffsetUniwin;

        private bool FitsContent => _windowMode == OverlayWindowMode.FitContent;

        /// <summary> Whether the global cursor (regardless of click-through) is over the hit area. </summary>
        public bool IsCursorOver => _cursorOver;

        /// <summary> Current content scale. </summary>
        public float ContentScale => _scale;

        /// <summary> Whether the overlay window stays above all other windows. </summary>
        public bool IsAlwaysOnTop => _alwaysOnTop;

        /// <summary> Fullscreen mode (opaque, monitor-sized, content fitted on black) instead of the transparent overlay. </summary>
        public bool IsFullscreen => _fullscreen;

        protected override void Awake()
        {
            base.Awake();

            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Read early (Awake, after injection) so settings views see the right state in their Start.
            _alwaysOnTop = _userData.GetBool(OverlayUserDataKeysProvider.Topmost, true);
            _fullscreen = _userData.GetBool(OverlayUserDataKeysProvider.Fullscreen, _defaultFullscreen);
        }

        private IEnumerator Start()
        {
            if (Application.isEditor)
                yield break; // in the editor UniWinC would control the Game View window

            if (_uniWindowController == null)
                _uniWindowController = FindAnyObjectByType<UniWindowController>();

            if (_uniWindowController == null)
            {
                // Without the controller we cannot manage the window through UniWinC,
                // but it must not stay parked off-screen forever.
                this.Log("No UniWindowController in the scene — restoring the window with raw Win32.", LogLevel.Error);
                NativeWindow.EmergencyShow();
                yield break;
            }

            // The overlay MUST keep polling the cursor while unfocused — with a paused player the
            // click-through state freezes and the invisible desktop-covering window blocks all input.
            Application.runInBackground = true;

            _uniWindowController.shouldFitMonitor = false;
            _uniWindowController.isTopmost = _alwaysOnTop; // the player's saved choice beats the inspector value
            InitializeHitArea();

            // UniWinC captures the window and enables transparency in its first Update —
            // until then the window stays parked off-screen.
            yield return null;
            yield return null;

            if (_fullscreen)
            {
                EnterFullscreen();

                // Window style changes (borderless/layered) can move the window once more a moment later.
                yield return new WaitForSecondsRealtime(0.5f);
                EnterFullscreen();
            }
            else if (FitsContent)
            {
                RestoreWindowPlacement();

                // Window style changes (borderless/layered) can move the window once more a
                // moment later — re-assert the placement we decided on.
                yield return new WaitForSecondsRealtime(0.5f);
                ApplyWindowRect(_windowUniwinPosition, _scale);
            }
            else
            {
                CoverVirtualDesktop();
                PositionContent();

                // Window style changes (borderless/layered) can move the window once more a moment later.
                yield return new WaitForSecondsRealtime(0.5f);
                CoverVirtualDesktop();
            }

            _windowReady = true;
            _uniWindowController.OnMonitorChanged += HandleMonitorChanged;
        }

        public override void OnDestroy()
        {
            if (_uniWindowController != null)
                _uniWindowController.OnMonitorChanged -= HandleMonitorChanged;

            SaveContentState();

            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        private void Update()
        {
            // Fullscreen: no drag, no scroll zoom, no click-through — the window is the game. The
            // canvas still tracks the window size a frame late, so keep the content centered.
            if (_fullscreen)
            {
                if (_windowReady && _uniWindowController != null)
                {
                    CenterContent();
                    _uniWindowController.isClickThrough = false; // re-asserted like the hover flag below
                }
                return;
            }

            // FitContent: the canvas tracks the window size with a frame of lag (Screen
            // resolution updates after the resize lands) — keep the content centered every
            // frame instead of chasing callbacks. Idempotent, allocation-free.
            if (FitsContent && _hitAreaActive)
            {
                CenterContent();
            }

            if (!_hitAreaActive)
                return;

            bool cursorValid = TryGetCursorScreenPoint(out Vector2 screenPoint);

            UpdateHover(cursorValid, screenPoint);
            UpdateDrag(cursorValid, screenPoint);
            UpdateScrollScale(cursorValid, screenPoint);
        }

        #region Window

        /// <summary>
        /// Always-on-top for the overlay window. On (default) the overlay floats above every other
        /// application; off makes it an ordinary window that can hide behind others. The choice
        /// persists through <see cref="IUserData"/> and is restored on the next launch.
        /// </summary>
        public void SetAlwaysOnTop(bool alwaysOnTop)
        {
            _alwaysOnTop = alwaysOnTop;
            _userData.Set(OverlayUserDataKeysProvider.Topmost, alwaysOnTop);

            if (!Application.isEditor && _uniWindowController != null)
                _uniWindowController.isTopmost = alwaysOnTop;
        }

        /// <summary>
        /// FULLSCREEN vs the transparent overlay. On: the window becomes opaque and covers the whole
        /// monitor it is on, the content is scaled to fit the monitor and centered on black (a square
        /// game gets bars at the sides); drag, scroll zoom and click-through are off. Off: back to the
        /// overlay at the spot and scale the player had before. Persists through <see cref="IUserData"/>.
        /// </summary>
        public void SetFullscreen(bool fullscreen)
        {
            if (_fullscreen == fullscreen)
                return;

            _userData.Set(OverlayUserDataKeysProvider.Fullscreen, fullscreen);
            if (Application.isEditor || !_windowReady || _uniWindowController == null)
            {
                _fullscreen = fullscreen; // applied by Start (or never, in the editor)
                return;
            }

            if (fullscreen)
            {
                // Remember the overlay spot first — SaveContentState is silent while fullscreen.
                _stateDirty = true;
                SaveContentState();
                _fullscreen = true;
                EnterFullscreen();
            }
            else
            {
                _fullscreen = false;
                ExitFullscreen();
            }
        }

        /// <summary> Opaque, monitor-sized window; content fitted (letterboxed) and centered. Idempotent. </summary>
        private void EnterFullscreen()
        {
            Rect monitor = UniWindowController.GetMonitorRect(MonitorUnderWindow());

            _uniWindowController.isTransparent = false;
            // UniWinC brings the decorated style (title bar, frame) back with transparency off — strip it.
            NativeWindow.MakeBorderless();
            Camera camera = _uniWindowController.currentCamera;
            if (camera != null)
            {
                // UniWinC restores the camera's ORIGINAL background when transparency goes off —
                // the bars must be black whatever the scene camera had.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
            }
            _uniWindowController.isClickThrough = false;

            _windowUniwinPosition = monitor.position;
            _windowSize = monitor.size;
            _uniWindowController.windowSize = monitor.size;
            _uniWindowController.windowPosition = monitor.position;

            if (_content != null && _content.rect.width > 0f && _content.rect.height > 0f)
            {
                _scale = Mathf.Min(monitor.width / _content.rect.width, monitor.height / _content.rect.height);
                _content.localScale = new Vector3(_scale, _scale, 1f);
            }
            CenterContent();
        }

        /// <summary> Back to the transparent overlay at the saved spot and scale (or the start placement). </summary>
        private void ExitFullscreen()
        {
            _uniWindowController.isTransparent = true;
            _uniWindowController.isClickThrough = true;
            _lastClickThrough = true;

            if (FitsContent)
            {
                RestoreWindowPlacement();
            }
            else
            {
                if (_content != null)
                    _content.localScale = Vector3.one;
                CoverVirtualDesktop();
                PositionContent();
            }
        }

        /// <summary> Index of the monitor holding the window's center (FitContent) — else the primary one. </summary>
        private int MonitorUnderWindow()
        {
            Vector2 center = _windowUniwinPosition + _windowSize * 0.5f;
            int count = UniWindowController.GetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                if (UniWindowController.GetMonitorRect(i).Contains(center))
                    return i;
            }
            return 0;
        }

        /// <summary>
        /// Stretch the invisible window over the bounding box of ALL monitors. The window never moves
        /// afterwards — the content does — so there is nothing to flicker.
        /// </summary>
        private void CoverVirtualDesktop()
        {
            int count = UniWindowController.GetMonitorCount();
            if (count == 0)
                return;

            Rect union = UniWindowController.GetMonitorRect(0);
            for (int i = 1; i < count; i++)
            {
                Rect monitor = UniWindowController.GetMonitorRect(i);
                union = Rect.MinMaxRect(
                    Mathf.Min(union.xMin, monitor.xMin),
                    Mathf.Min(union.yMin, monitor.yMin),
                    Mathf.Max(union.xMax, monitor.xMax),
                    Mathf.Max(union.yMax, monitor.yMax));
            }

            _uniWindowController.windowSize = union.size;
            _uniWindowController.windowPosition = union.position;
        }

        private void HandleMonitorChanged()
        {
            if (_fullscreen)
            {
                EnterFullscreen(); // re-fit to the (possibly new) monitor
                return;
            }

            if (FitsContent)
            {
                // When the monitor with the window got detached, pull it back to the start placement.
                if (!IsVisibleOnAnyMonitor(new Rect(_windowUniwinPosition, WindowSizeFor(_scale))))
                    ApplyStartPlacement();
                return;
            }

            CoverVirtualDesktop();

            if (_content == null || !TryGetContentScreenRect(out Rect contentRect))
                return;

            // When the monitor with the content got detached, pull the content back to the start placement.
            if (!IsVisibleOnAnyMonitor(ScreenToUniWin(contentRect)))
                ApplyStartPlacement();
        }

        #endregion

        #region FitContent window

        /// <summary> Window size (physical px) that wraps the content at <paramref name="scale"/> plus the margin. </summary>
        private Vector2 WindowSizeFor(float scale)
        {
            Vector2 size = _content != null ? _content.rect.size : new Vector2(100f, 100f);
            size = size * scale + 2f * _windowMargin * Vector2.one;
            return new Vector2(Mathf.Max(50f, Mathf.Round(size.x)), Mathf.Max(50f, Mathf.Round(size.y)));
        }

        /// <summary>
        /// THE one writer of the window rect in FitContent mode: clamps the scale, resizes the
        /// window to wrap the scaled content and moves it to <paramref name="uniwinPosition"/>
        /// (bottom-left, UniWinC coordinates). The content itself just stays centered (Update).
        /// </summary>
        private void ApplyWindowRect(Vector2 uniwinPosition, float scale)
        {
            _scale = Mathf.Clamp(scale, _minScale, MaxScaleNow());
            SetWindowRect(uniwinPosition, WindowSizeFor(_scale));

            if (_content != null)
                _content.localScale = new Vector3(_scale, _scale, 1f);
            CenterContent();
            _stateDirty = true;
        }

        /// <summary> The native write of the window rect (FitContent) — every call costs a swapchain resize, so callers batch. </summary>
        private void SetWindowRect(Vector2 uniwinPosition, Vector2 size)
        {
            _windowUniwinPosition = new Vector2(Mathf.Round(uniwinPosition.x), Mathf.Round(uniwinPosition.y));
            _windowSize = size;

            _uniWindowController.windowSize = size;
            _uniWindowController.windowPosition = _windowUniwinPosition;

            // A resize can drop the DWM transparency state (observed as a BLACK backdrop when
            // the window grows to ~monitor size). SetTransparent has no equality guard, so
            // re-asserting actually re-runs EnableTransparent — which re-composes the window
            // and made EVERY scroll-zoom step blink. Only near-monitor-sized windows (the case
            // that lost the alpha) re-assert it now.
            if (_uniWindowController.isTransparent && IsNearMonitorSize(size))
                _uniWindowController.isTransparent = true;
        }

        // ---- Zoom (FitContent) ----
        // EVERY OS-window resize of the transparent window shows one empty frame — nothing smooths
        // that, so a zoom must resize as RARELY as possible (2026-10-25, after both variants blinked:
        // the per-notch exact fit blinked on every notch, the "grow with headroom + shrink 0.6 s after
        // the wheel rests" variant blinked at the late shrink):
        // - zooming OUT never resizes: the content shrinks inside the window, the margin is
        //   transparent (and click-through — the hit area is the content, not the window);
        // - zooming IN resizes only when the content no longer fits, straight to a generous size
        //   (headroom, capped below the monitor-size zone where DWM drops the alpha), so the next
        //   notches — and every later zoom in the same range — fit without a resize;
        // - the window never shrinks back during the session; the saved spot/scale are the exact
        //   fit (FittedWindowPosition), so the next launch starts tight again.

        /// <summary> The window's real size (physical px) — may exceed the content fit after a zoom. </summary>
        private Vector2 _windowSize;
        private const float ZOOM_GROW_HEADROOM = 1.5f;
        /// <summary> The headroom alone never grows the window past this share of the work area (DWM black-backdrop zone). </summary>
        private const float ZOOM_HEADROOM_MONITOR_CAP = 0.8f;

        /// <summary> One scroll notch: the content rescales around the window center; the window grows only when it must. </summary>
        private void ZoomInsideWindow(float clamped)
        {
            Vector2 center = _windowUniwinPosition + _windowSize * 0.5f;
            Vector2 needed = WindowSizeFor(clamped);
            _scale = clamped;
            if (_content != null)
                _content.localScale = new Vector3(_scale, _scale, 1f);

            if (needed.x > _windowSize.x || needed.y > _windowSize.y)
            {
                Rect area = ResolveWorkArea(0);
                Vector2 grown = needed * ZOOM_GROW_HEADROOM;
                if (area.width > 0f && area.height > 0f)
                {
                    grown = new Vector2(Mathf.Min(grown.x, area.width * ZOOM_HEADROOM_MONITOR_CAP),
                        Mathf.Min(grown.y, area.height * ZOOM_HEADROOM_MONITOR_CAP));
                }
                grown = new Vector2(Mathf.Round(Mathf.Max(grown.x, needed.x)), Mathf.Round(Mathf.Max(grown.y, needed.y)));
                SetWindowRect(center - grown * 0.5f, grown);
            }

            CenterContent();
            _stateDirty = true;
        }

        /// <summary> The window spot an EXACT content fit would have (what saves/restores use while a zoom settles). </summary>
        private Vector2 FittedWindowPosition => _windowUniwinPosition + (_windowSize - WindowSizeFor(_scale)) * 0.5f;

        /// <summary> The window covers ~a monitor in either axis — where DWM may drop the alpha composition. </summary>
        private bool IsNearMonitorSize(Vector2 windowSize)
        {
            Rect area = ResolveWorkArea(0);
            return area.width > 0f && area.height > 0f
                   && (windowSize.x >= area.width * 0.9f || windowSize.y >= area.height * 0.9f);
        }

        /// <summary> The scroll-scale ceiling: the inspector max, stretched up to the fullscreen fit like CoverDesktop mode. </summary>
        private float MaxScaleNow()
        {
            float max = _maxScale;
            if (FitsContent && _content != null)
            {
                Rect area = ResolveWorkArea(0);
                Rect rect = _content.rect;
                if (rect.width > 0f && rect.height > 0f && area.width > 0f && area.height > 0f)
                {
                    float fit = Mathf.Min(
                        (area.width - 2f * _windowMargin) / rect.width,
                        (area.height - 2f * _windowMargin) / rect.height);
                    max = Mathf.Max(max, fit);
                }
            }
            return max;
        }

        /// <summary>
        /// Pins the scaled content to the canvas center (the window wraps it exactly, so
        /// centered == filling the window minus the margin). Works with any single-point
        /// anchors/pivot the consumer scene uses.
        /// </summary>
        private void CenterContent()
        {
            if (_content == null || _content.parent is not RectTransform parent)
                return;

            Vector2 target = parent.rect.center - AnchorReference(parent, _content) - _content.rect.center * _scale;
            if ((_content.anchoredPosition - target).sqrMagnitude > 0.25f)
                _content.anchoredPosition = target;
        }

        /// <summary> FitContent boot: the saved window spot + scale, or the start placement. </summary>
        private void RestoreWindowPlacement()
        {
            float scale = Mathf.Clamp(_userData.GetFloat(OverlayUserDataKeysProvider.Scale, 1f), _minScale, MaxScaleNow());

            if (_userData.GetBool(OverlayUserDataKeysProvider.Saved))
            {
                var position = new Vector2(
                    _userData.GetFloat(OverlayUserDataKeysProvider.PositionX),
                    _userData.GetFloat(OverlayUserDataKeysProvider.PositionY));

                // Monitor layout could have changed since the save (and pre-FitContent saves
                // held CONTENT coordinates) — restore only a spot that is still on a monitor.
                if (IsVisibleOnAnyMonitor(new Rect(position, WindowSizeFor(scale))))
                {
                    ApplyWindowRect(position, scale);
                    _stateDirty = false;
                    return;
                }
            }

            _scale = scale;
            ApplyStartPlacement();
        }

        #endregion

        #region Content

        /// <summary> One-shot move: aligns the content flush to the corner of the primary monitor's work area. </summary>
        public void SnapContentToCorner(OverlayCorner corner) => SnapContentToCorner(corner, 0);

        /// <summary> Int overload for wiring directly into UnityEvents (0=BottomRight, 1=BottomLeft, 2=TopRight, 3=TopLeft). </summary>
        public void SnapContentToCorner(int cornerIndex) => SnapContentToCorner((OverlayCorner)Mathf.Clamp(cornerIndex, 0, 3), 0);

        /// <summary> One-shot move: aligns the content flush to the corner of the given monitor's work area (above the taskbar). </summary>
        public void SnapContentToCorner(OverlayCorner corner, int monitorIndex)
        {
            if (Application.isEditor || _uniWindowController == null || _content == null)
                return;

            if (_content.parent is not RectTransform parent)
                return;

            Rect workArea = ResolveWorkArea(monitorIndex);
            bool left = corner is OverlayCorner.BottomLeft or OverlayCorner.TopLeft;
            bool bottom = corner is OverlayCorner.BottomRight or OverlayCorner.BottomLeft;

            if (FitsContent)
            {
                // The window IS the panel — snap the window rect itself.
                Vector2 size = WindowSizeFor(_scale);
                ApplyWindowRect(new Vector2(
                    left ? workArea.xMin : workArea.xMax - size.x,
                    bottom ? workArea.yMin : workArea.yMax - size.y), _scale);
                return;
            }

            // Work area corner: UniWinC coords -> window client -> screen pixels -> parent local.
            var uniwinCorner = new Vector2(left ? workArea.xMin : workArea.xMax, bottom ? workArea.yMin : workArea.yMax);
            Vector2 screenCorner = UniWinToScreen(uniwinCorner);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenCorner, _uiCamera, out Vector2 targetLocal))
                return;

            // Matching corner of the scaled content rect, relative to its pivot.
            Rect rect = _content.rect;
            var rectCorner = new Vector2(left ? rect.xMin : rect.xMax, bottom ? rect.yMin : rect.yMax) * _scale;

            _content.anchoredPosition = targetLocal - AnchorReference(parent, _content) - rectCorner;
            _stateDirty = true;
        }

        /// <summary>
        /// Set content scale (clamped to Min/Max Scale; the max stretches up to the fullscreen fit,
        /// so scrolling can always bring the content back to filling the primary monitor).
        /// </summary>
        public void SetContentScale(float scale)
        {
            if (FitsContent && !Application.isEditor && _uniWindowController != null)
            {
                // Resize the window around its CENTER so zooming does not walk the panel.
                // Scrolling past the min/max changes nothing — do not touch anything.
                float clamped = Mathf.Clamp(scale, _minScale, MaxScaleNow());
                if (Mathf.Approximately(clamped, _scale))
                    return;
                ZoomInsideWindow(clamped);
                return;
            }

            float max = _maxScale;
            if (TryGetFitScale(0, out float fit, out _, out _))
                max = Mathf.Max(max, fit);

            _scale = Mathf.Clamp(scale, _minScale, max);
            if (_content != null)
                _content.localScale = new Vector3(_scale, _scale, 1f);
            _stateDirty = true;
        }

        /// <summary>
        /// One-shot move: scales the content to fill the given monitor's work area (above the taskbar)
        /// and centers it there — the overlay equivalent of fullscreen. Scroll shrinks it back into a panel.
        /// </summary>
        public void FitContentToMonitor(int monitorIndex = 0)
        {
            if (FitsContent)
            {
                if (Application.isEditor || _uniWindowController == null || _content == null)
                    return;

                // Inset: a borderless window that EXACTLY covers a monitor gets promoted by
                // DWM to the fullscreen presentation path, which skips the alpha composition
                // (the backdrop turns black). A few px of slack keeps it an ordinary
                // composited window; the content fit is min-axis anyway, so nobody sees it.
                Rect workArea = ResolveWorkArea(monitorIndex);
                workArea = Rect.MinMaxRect(workArea.xMin + 2f, workArea.yMin + 2f, workArea.xMax - 2f, workArea.yMax - 2f);
                Rect rect = _content.rect;
                if (rect.width <= 0f || rect.height <= 0f || workArea.width <= 0f || workArea.height <= 0f)
                    return;

                float fitScale = Mathf.Min(
                    (workArea.width - 2f * _windowMargin) / rect.width,
                    (workArea.height - 2f * _windowMargin) / rect.height);
                fitScale = Mathf.Max(fitScale, _minScale);
                ApplyWindowRect(workArea.center - WindowSizeFor(fitScale) * 0.5f, fitScale);
                return;
            }

            if (!TryGetFitScale(monitorIndex, out float fit, out Rect area, out RectTransform parent))
                return;

            _scale = Mathf.Max(fit, _minScale);
            _content.localScale = new Vector3(_scale, _scale, 1f);
            _content.anchoredPosition = area.center - AnchorReference(parent, _content) - _content.rect.center * _scale;
            _stateDirty = true;
        }

        /// <summary>
        /// Scale at which the content fills the monitor's work area (min of the two axis ratios),
        /// plus that work area converted to the content parent's local space. False in the editor
        /// or before UniWinC knows the window.
        /// </summary>
        private bool TryGetFitScale(int monitorIndex, out float fitScale, out Rect localWorkArea, out RectTransform parent)
        {
            fitScale = 0f;
            localWorkArea = default;
            parent = null;

            if (Application.isEditor || _uniWindowController == null || _content == null)
                return false;

            if (_content.parent is not RectTransform p)
                return false;

            Rect workArea = ResolveWorkArea(monitorIndex);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(p, UniWinToScreen(workArea.min), _uiCamera, out Vector2 minLocal)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(p, UniWinToScreen(workArea.max), _uiCamera, out Vector2 maxLocal))
            {
                return false;
            }

            localWorkArea = Rect.MinMaxRect(minLocal.x, minLocal.y, maxLocal.x, maxLocal.y);
            Rect rect = _content.rect;
            if (rect.width <= 0f || rect.height <= 0f || localWorkArea.width <= 0f || localWorkArea.height <= 0f)
                return false;

            fitScale = Mathf.Min(localWorkArea.width / rect.width, localWorkArea.height / rect.height);
            parent = p;
            return true;
        }

        /// <summary>
        /// Cancels the current (or just-armed) content drag and restores the position from the press moment.
        /// Call it when the game claims the gesture for itself — e.g. a chess piece got picked up on drag start.
        /// </summary>
        public void CancelDrag()
        {
            if (!_dragArmed && !_dragging)
                return;

            if (_dragging)
            {
                if (FitsContent)
                    SetWindowRect(_dragStartWindowPosition, _windowSize); // position only — a resize would blink
                else if (_content != null)
                    _content.anchoredPosition = _dragStartContentPosition;
            }

            _dragArmed = false;
            _dragging = false;
        }

        private void UpdateDrag(bool cursorValid, Vector2 screenPoint)
        {
            if (FitsContent)
            {
                UpdateWindowDrag(cursorValid, screenPoint);
                return;
            }

            bool pressed = IsMousePressed();

            if (_dragArmed || _dragging)
            {
                if (!pressed)
                {
                    if (_dragging)
                        SaveContentState();
                    _dragArmed = false;
                    _dragging = false;
                }
                else if (_dragArmed && cursorValid
                         && (screenPoint - _pressScreenPoint).sqrMagnitude >= DRAG_THRESHOLD * DRAG_THRESHOLD
                         && _content != null && _content.parent is RectTransform armedParent
                         && RectTransformUtility.ScreenPointToLocalPointInRectangle(armedParent, screenPoint, _uiCamera, out Vector2 armedLocal))
                {
                    // Threshold crossed — rebase the offset here, so the content starts moving without a jump.
                    _dragOffset = armedLocal - _content.anchoredPosition;
                    _dragArmed = false;
                    _dragging = true;
                }
                else if (_dragging && cursorValid && _content != null && _content.parent is RectTransform parent
                         && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPoint, _uiCamera, out Vector2 local))
                {
                    _content.anchoredPosition = local - _dragOffset;
                    _stateDirty = true;
                }
            }
            else if (pressed && !_mouseWasPressed && cursorValid
                     && _content != null && IsOverRect(_moveHandle, screenPoint)
                     && !IsOverGestureClaimer(screenPoint))
            {
                _pressScreenPoint = screenPoint;
                _dragStartContentPosition = _content.anchoredPosition;
                _dragArmed = true;
            }

            _mouseWasPressed = pressed;
        }

        /// <summary>
        /// FitContent drag: the gesture moves the OS WINDOW, driven by the GLOBAL cursor
        /// (UniWinC coordinates) — window-local points would chase a moving origin. The press
        /// is still validated window-locally (move handle, not over a Selectable).
        /// </summary>
        private void UpdateWindowDrag(bool cursorValid, Vector2 screenPoint)
        {
            bool pressed = IsMousePressed();
            Vector2 cursorUniwin = _uniWindowController.cursorPosition;

            if (_dragArmed || _dragging)
            {
                if (!pressed)
                {
                    if (_dragging)
                        SaveContentState();
                    _dragArmed = false;
                    _dragging = false;
                }
                else if (_dragArmed
                         && (cursorUniwin - _pressCursorUniwin).sqrMagnitude >= DRAG_THRESHOLD * DRAG_THRESHOLD)
                {
                    // Threshold crossed — rebase the offset here, so the window starts moving without a jump.
                    _dragOffsetUniwin = cursorUniwin - _windowUniwinPosition;
                    _dragArmed = false;
                    _dragging = true;
                }
                else if (_dragging)
                {
                    // Position only — no resize mid-drag, the cheapest possible window move.
                    _windowUniwinPosition = new Vector2(
                        Mathf.Round(cursorUniwin.x - _dragOffsetUniwin.x),
                        Mathf.Round(cursorUniwin.y - _dragOffsetUniwin.y));
                    _uniWindowController.windowPosition = _windowUniwinPosition;
                    _stateDirty = true;
                }
            }
            else if (pressed && !_mouseWasPressed && cursorValid
                     && IsOverRect(_moveHandle, screenPoint)
                     && !IsOverGestureClaimer(screenPoint))
            {
                _pressCursorUniwin = cursorUniwin;
                _dragStartWindowPosition = _windowUniwinPosition;
                _dragArmed = true;
            }

            _mouseWasPressed = pressed;
        }

        private void UpdateScrollScale(bool cursorValid, Vector2 screenPoint)
        {
            // Step 0 = the game owns the wheel — do NOT touch the shared GlobalMouseScroll counter at all.
            if (_scrollScaleStep <= 0f || _content == null)
                return;

            // Drain the counter every frame; apply only when the cursor is over the overlay.
            float notches = GlobalMouseScroll.ConsumeNotches();
            if (!_cursorOver || notches == 0f)
                return;

            // A ScrollRect under the cursor owns the wheel (it scrolls its content) — zooming the
            // window at the same time made scroll views jump/blink. Same rule as the drag claim.
            if (cursorValid && IsOverScrollRect(screenPoint))
                return;

            SetContentScale(_scale * (1f + notches * _scrollScaleStep));
        }

        private void PositionContent()
        {
            if (_content == null)
                return;

            SetContentScale(_userData.GetFloat(OverlayUserDataKeysProvider.Scale, 1f));

            if (_userData.GetBool(OverlayUserDataKeysProvider.Saved))
            {
                var position = new Vector2(
                    _userData.GetFloat(OverlayUserDataKeysProvider.PositionX),
                    _userData.GetFloat(OverlayUserDataKeysProvider.PositionY));
                _content.anchoredPosition = position;

                // Monitor layout could have changed since the save — the content must stay reachable.
                if (TryGetContentScreenRect(out Rect contentRect) && IsVisibleOnAnyMonitor(ScreenToUniWin(contentRect)))
                {
                    _stateDirty = false;
                    return;
                }
            }

            ApplyStartPlacement();
        }

        /// <summary> First launch (and off-monitor fallback): fullscreen on the primary monitor, or Start Corner. </summary>
        private void ApplyStartPlacement()
        {
            if (_startFullscreen)
                FitContentToMonitor(0);
            else
                SnapContentToCorner(_startCorner, 0);
        }

        /// <summary> Persists content position and scale — restored on the next launch. Called automatically after drag/scale. </summary>
        public void SaveContentState()
        {
            // Fullscreen keeps the OVERLAY spot that was saved when it was entered.
            if (Application.isEditor || _content == null || !_stateDirty || _fullscreen)
                return;

            _stateDirty = false;
            // FitContent persists the WINDOW spot (UniWinC coords); CoverDesktop the content's
            // anchored position — RestoreWindowPlacement/PositionContent read the matching one
            // and both validate against the monitors, so a mode switch degrades to the start
            // placement instead of a garbage spot.
            Vector2 position = FitsContent ? FittedWindowPosition : _content.anchoredPosition;
            _userData.Set(OverlayUserDataKeysProvider.Saved, true);
            _userData.Set(OverlayUserDataKeysProvider.PositionX, position.x);
            _userData.Set(OverlayUserDataKeysProvider.PositionY, position.y);
            _userData.Set(OverlayUserDataKeysProvider.Scale, _scale);
        }

        private static Vector2 AnchorReference(RectTransform parent, RectTransform child)
        {
            Rect parentRect = parent.rect;
            return new Vector2(
                Mathf.Lerp(parentRect.xMin, parentRect.xMax, child.anchorMin.x),
                Mathf.Lerp(parentRect.yMin, parentRect.yMax, child.anchorMin.y));
        }

        private bool TryGetContentScreenRect(out Rect screenRect)
        {
            screenRect = default;
            if (_content == null)
                return false;

            var corners = new Vector3[4];
            _content.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(_uiCamera, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(_uiCamera, corners[2]);
            screenRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        #endregion

        #region Monitors

        private static bool IsVisibleOnAnyMonitor(Rect uniwinRect)
        {
            int count = UniWindowController.GetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                Rect monitor = UniWindowController.GetMonitorRect(i);
                float overlapX = Mathf.Min(uniwinRect.xMax, monitor.xMax) - Mathf.Max(uniwinRect.xMin, monitor.xMin);
                float overlapY = Mathf.Min(uniwinRect.yMax, monitor.yMax) - Mathf.Max(uniwinRect.yMin, monitor.yMin);
                if (overlapX >= 50f && overlapY >= 50f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Work area (monitor minus taskbar) of the given monitor. An index out of range falls back to monitor 0.
        /// </summary>
        private Rect ResolveWorkArea(int monitorIndex)
        {
            int count = UniWindowController.GetMonitorCount();
            int index = monitorIndex >= 0 && monitorIndex < count ? monitorIndex : 0;

            Rect monitorRect = UniWindowController.GetMonitorRect(index);

            if (NativeWindow.TryGetWorkAreaForMonitor(monitorRect, out Rect workArea))
                return workArea;

            // UniWinC does not know the monitors yet (empty rect)? Primary monitor's work area.
            if ((monitorRect.width <= 0f || monitorRect.height <= 0f)
                && NativeWindow.TryGetPrimaryWorkArea(out Rect primaryWork))
            {
                return primaryWork;
            }

            return monitorRect; // no taskbar data — full monitor
        }

        #endregion

        #region Coordinates

        /// <summary>
        /// UniWinC coordinates (origin bottom-left of the primary monitor, Y up) -> Screen pixels of our window.
        /// The window covers the virtual desktop, so this is a plain offset plus the client-to-Screen DPI rescale.
        /// </summary>
        private Vector2 UniWinToScreen(Vector2 uniwinPoint)
        {
            Vector2 inWindow = uniwinPoint - _uniWindowController.windowPosition;
            Vector2 client = _uniWindowController.clientSize;
            if (client.x <= 0f || client.y <= 0f)
                return inWindow;

            return new Vector2(inWindow.x * Screen.width / client.x, inWindow.y * Screen.height / client.y);
        }

        private Rect ScreenToUniWin(Rect screenRect)
        {
            Vector2 client = _uniWindowController.clientSize;
            if (client.x <= 0f || client.y <= 0f)
                return screenRect;

            Vector2 windowPosition = _uniWindowController.windowPosition;
            var min = new Vector2(screenRect.xMin * client.x / Screen.width, screenRect.yMin * client.y / Screen.height) + windowPosition;
            var max = new Vector2(screenRect.xMax * client.x / Screen.width, screenRect.yMax * client.y / Screen.height) + windowPosition;
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        #endregion

        #region Hit area

        private void InitializeHitArea()
        {
            RectTransform first = _moveHandle != null ? _moveHandle : null;
            if (first == null)
            {
                foreach (RectTransform rect in _hitRects)
                {
                    if (rect != null)
                    {
                        first = rect;
                        break;
                    }
                }
            }

            if (first == null)
                return; // no rects configured — click-through stays in UniWinC mode

            Canvas canvas = first.GetComponentInParent<Canvas>();
            _uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

            // Take over: UniWinC automation off, start in "pass everything through" mode.
            _uniWindowController.hitTestType = UniWindowController.HitTestType.None;
            _uniWindowController.isClickThrough = true;
            _lastClickThrough = true;
            _hitAreaActive = true;
        }

        private void UpdateHover(bool cursorValid, Vector2 screenPoint)
        {
            bool over = cursorValid && IsCursorOverAnything(screenPoint);
            if (over != _cursorOver)
            {
                _cursorOver = over;
                Events.Raise(new OverlayHoverChanged(over));

                if (over)
                {
                    _onCursorEnter?.Invoke();
                }
                else
                {
                    _onCursorExit?.Invoke();
                    SaveContentState(); // natural moment to flush the state without spamming disk writes
                }
            }

            // Re-asserted EVERY frame, not only on change — focus/style/monitor changes can reset the
            // native flag behind our back, and a stale "clickable" on a desktop-covering window
            // blocks input on the whole desktop until the next hover toggle.
            _uniWindowController.isClickThrough = !over;
        }

        /// <summary>
        /// The same conversion UniWinC does internally (GetClientCursorPosition): cursor and window
        /// are in UniWinC space, cursor relative to the window rescaled to Screen pixels (matters with DPI != 100%).
        /// Borderless window → client area offset = 0.
        /// </summary>
        private bool TryGetCursorScreenPoint(out Vector2 screenPoint)
        {
            screenPoint = default;

            Vector2 cursorInWindow = _uniWindowController.cursorPosition - _uniWindowController.windowPosition;
            Vector2 client = _uniWindowController.clientSize;
            if (client.x <= 0f || client.y <= 0f)
                return false;

            if (cursorInWindow.x < 0f || cursorInWindow.y < 0f || cursorInWindow.x > client.x || cursorInWindow.y > client.y)
                return false;

            screenPoint = new Vector2(
                cursorInWindow.x * Screen.width / client.x,
                cursorInWindow.y * Screen.height / client.y);
            return true;
        }

        private bool IsCursorOverAnything(Vector2 screenPoint)
        {
            // The Move Handle counts as hit area automatically — otherwise the window would be
            // click-through over it and it could never be grabbed.
            if (IsOverRect(_moveHandle, screenPoint))
                return true;

            foreach (RectTransform rect in _hitRects)
            {
                if (IsOverRect(rect, screenPoint))
                    return true;
            }

            return false;
        }

        private bool IsOverRect(RectTransform rect, Vector2 screenPoint)
        {
            // An inactive object (hidden panel) must not catch the mouse —
            // otherwise an invisible rect would block the desktop under it.
            if (rect == null || !rect.gameObject.activeInHierarchy)
                return false;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, _uiCamera, out Vector2 local))
                return false;

            Rect r = rect.rect;
            return local.x >= r.xMin - _hitPadding && local.x <= r.xMax + _hitPadding
                && local.y >= r.yMin - _hitPadding && local.y <= r.yMax + _hitPadding;
        }

        private static bool IsMousePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.leftButton.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }

        private static readonly List<RaycastResult> RAYCAST_RESULTS = new List<RaycastResult>(16);

        /// <summary>
        /// True when the press landed on something that claims the gesture for itself — those
        /// always win over the content drag. Three families:
        /// Selectables (button, slider, toggle, dropdown...) and SCROLL VIEWS (a press anywhere
        /// inside a ScrollRect — its layout groups, rows, grids — pans the scroll content, not
        /// the window), both checked on the TOP raycast hit only (the element that actually
        /// receives the uGUI events); plus the manual Drag Exception rects (plain geometry,
        /// for areas with custom drag gestures). Deliberately NOT any IDragHandler: that also
        /// caught game-specific drag components and made whole panels undraggable.
        /// </summary>
        private bool IsOverGestureClaimer(Vector2 screenPoint)
        {
            foreach (RectTransform rect in _dragExceptionRects)
            {
                if (IsOverRect(rect, screenPoint))
                    return true;
            }

            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                return false;

            var pointer = new PointerEventData(eventSystem) { position = screenPoint };
            RAYCAST_RESULTS.Clear();
            eventSystem.RaycastAll(pointer, RAYCAST_RESULTS);

            bool over = false;
            if (RAYCAST_RESULTS.Count > 0)
            {
                GameObject top = RAYCAST_RESULTS[0].gameObject;
                over = top.GetComponentInParent<Selectable>() != null
                    || top.GetComponentInParent<ScrollRect>() != null;
            }

            RAYCAST_RESULTS.Clear();
            return over;
        }

        /// <summary> The TOP raycast hit sits inside a ScrollRect (the wheel claim — see UpdateScrollScale). </summary>
        private static bool IsOverScrollRect(Vector2 screenPoint)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                return false;

            var pointer = new PointerEventData(eventSystem) { position = screenPoint };
            RAYCAST_RESULTS.Clear();
            eventSystem.RaycastAll(pointer, RAYCAST_RESULTS);
            bool over = RAYCAST_RESULTS.Count > 0 && RAYCAST_RESULTS[0].gameObject.GetComponentInParent<ScrollRect>() != null;
            RAYCAST_RESULTS.Clear();
            return over;
        }

        /// <summary> Runtime drag exception: presses inside <paramref name="rect"/> stop starting the window drag. </summary>
        public void AddDragException(RectTransform rect)
        {
            if (rect != null && !_dragExceptionRects.Contains(rect))
                _dragExceptionRects.Add(rect);
        }

        /// <summary> Removes a rect added with <see cref="AddDragException"/> (or set in the inspector). </summary>
        public void RemoveDragException(RectTransform rect) => _dragExceptionRects.Remove(rect);

        #endregion
    }
}
