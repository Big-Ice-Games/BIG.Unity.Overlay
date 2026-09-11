using System.Collections;
using System.Collections.Generic;
using Kirurobo;
using UnityEngine;
using UnityEngine.Events;
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

        public static readonly UserDataKey Saved = new UserDataKey("WINDOW_SAVED");
        public static readonly UserDataKey PositionX = new UserDataKey("WINDOW_POS_X");
        public static readonly UserDataKey PositionY = new UserDataKey("WINDOW_POS_Y");
        public static readonly UserDataKey SizeX = new UserDataKey("WINDOW_SIZE_X");
        public static readonly UserDataKey SizeY = new UserDataKey("WINDOW_SIZE_Y");
    }

    /// <summary>
    /// The one overlay component. The window behaves like a normal draggable/resizable Windows window,
    /// just transparent and click-through outside your UI:
    ///
    /// STARTUP — on first launch the window aligns flush to Start Corner of the primary monitor's work area
    /// (above the taskbar). No corner state is kept — corners are one-shot moves (<see cref="SnapToCorner(OverlayCorner)"/>).
    /// After the player drags or resizes the window, its position and size persist through <see cref="IUserData"/>
    /// and are restored on the next launch (with a fallback to Start Corner when that spot is no longer on any monitor).
    /// The window starts parked off-screen (see OverlayBootstrap) and shows up only here, already transparent.
    ///
    /// HANDLES — assign a Move Handle rect (e.g. a title label) to drag the whole OS window with the cursor,
    /// across monitors like any Windows window, and a Resize Handle rect (bottom-right grip) to resize it
    /// within the Min/Max constraints (top-left corner stays in place). Handles are detected geometrically —
    /// no extra components, and they count as hit area automatically.
    ///
    /// HIT AREA — click-through controlled by GEOMETRY instead of UniWinC's per-pixel opacity test
    /// (with per-pixel testing a fully hidden overlay becomes a permanent "hole" that can never detect hover).
    /// Cursor over any active hit rect (or handle) → window clickable, outside → clicks fall through to the desktop.
    /// Hover is exposed three ways: <see cref="IsCursorOver"/>, the <see cref="OverlayHoverChanged"/> BIG event
    /// and the OnCursorEnter/OnCursorExit UnityEvents below.
    /// </summary>
    public sealed class OverlayWindow : BaseBehaviour
    {
        private enum DragState
        {
            None,
            Move,
            Resize,
        }

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
        [SerializeField, Tooltip("Corner the window aligns to on first launch (before the player moves it). One-shot — no corner state is kept afterwards.")]
        private OverlayCorner _startCorner = OverlayCorner.BottomRight;

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField, Tooltip("Window size on first launch, in pixels.")]
        private Vector2 _windowSize = new Vector2(1100f, 900f);

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField, Tooltip("Resize constraints for the Resize Handle (and for restored sizes).")]
        private Vector2 _minWindowSize = new Vector2(400f, 300f);

#if ODIN_INSPECTOR
        [FoldoutGroup("Window")]
#endif
        [SerializeField] private Vector2 _maxWindowSize = new Vector2(2560f, 1440f);

#if ODIN_INSPECTOR
        [FoldoutGroup("Handles")]
#else
        [Header("Handles")]
#endif
        [SerializeField, Tooltip("Grab this rect (e.g. the title label) to drag the whole OS window, across monitors like any Windows window. Counts as hit area automatically.")]
        private RectTransform _moveHandle;

#if ODIN_INSPECTOR
        [FoldoutGroup("Handles")]
#endif
        [SerializeField, Tooltip("Bottom-right corner grip: drag to resize the window within Min/Max Window Size (top-left corner stays in place). Counts as hit area automatically.")]
        private RectTransform _resizeHandle;

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#else
        [Header("Hit Area")]
#endif
        [SerializeField, Tooltip("Cursor over ANY of these rects (or a handle) = window clickable, outside = click-through. Inactive objects are skipped. Empty list + no handles = click-through stays in UniWinC mode.")]
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

        private DragState _drag;
        private bool _mouseWasPressed;
        private Vector2 _dragStartCursor;
        private Vector2 _dragStartPosition;
        private Vector2 _dragStartSize;

        /// <summary> Whether the global cursor (regardless of click-through) is over the hit area. </summary>
        public bool IsCursorOver => _cursorOver;

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
        }

        private IEnumerator Start()
        {
            if (Application.isEditor)
                yield break; // in the editor UniWinC would control the Game View window

            if (_uniWindowController == null)
                _uniWindowController = FindAnyObjectByType<UniWindowController>();

            if (_uniWindowController == null)
            {
                // Without the controller we cannot place the window through UniWinC,
                // but it must not stay parked off-screen forever.
                this.Log("No UniWindowController in the scene — restoring the window with raw Win32.", LogLevel.Error);
                NativeWindow.EmergencyShow();
                yield break;
            }

            _uniWindowController.shouldFitMonitor = false;
            InitializeHitArea();

            // UniWinC captures the window and enables transparency in its first Update —
            // until then the window stays parked off-screen.
            yield return null;
            yield return null;

            PositionWindow();

            // Window style changes (borderless/layered) can move the window once more a moment later.
            yield return new WaitForSecondsRealtime(0.5f);
            PositionWindow();

            _uniWindowController.OnMonitorChanged += EnsureVisible;
        }

        public override void OnDestroy()
        {
            if (_uniWindowController != null)
                _uniWindowController.OnMonitorChanged -= EnsureVisible;

            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        private void Update()
        {
            if (!_hitAreaActive)
                return;

            bool cursorValid = TryGetCursorScreenPoint(out Vector2 screenPoint);

            UpdateHover(cursorValid, screenPoint);
            UpdateDrag(cursorValid, screenPoint);
        }

        #region Window

        /// <summary> One-shot move: aligns the window flush to the corner of the monitor it currently sits on. </summary>
        public void SnapToCorner(OverlayCorner corner) => SnapToCorner(corner, MonitorUnderWindow());

        /// <summary> Int overload for wiring directly into UnityEvents (0=BottomRight, 1=BottomLeft, 2=TopRight, 3=TopLeft). </summary>
        public void SnapToCorner(int cornerIndex) => SnapToCorner((OverlayCorner)Mathf.Clamp(cornerIndex, 0, 3));

        /// <summary> One-shot move: aligns the window flush to the corner of the given monitor's work area (above the taskbar). </summary>
        public void SnapToCorner(OverlayCorner corner, int monitorIndex)
        {
            if (Application.isEditor || _uniWindowController == null)
                return;

            Rect workArea = ResolveWorkArea(monitorIndex);

            // workArea is in UniWinC space (origin bottom-left of the primary monitor, Y up);
            // windowPosition is the bottom-left corner of the window.
            Vector2 size = _uniWindowController.windowSize;

            float x = corner is OverlayCorner.BottomLeft or OverlayCorner.TopLeft
                ? workArea.xMin
                : workArea.xMax - size.x;

            float y = corner is OverlayCorner.BottomRight or OverlayCorner.BottomLeft
                ? workArea.yMin
                : workArea.yMax - size.y;

            _uniWindowController.windowPosition = new Vector2(x, y);
        }

        /// <summary> Clamp a window size to the configured min/max constraints. </summary>
        public Vector2 ClampSize(Vector2 size) => new Vector2(
            Mathf.Clamp(size.x, _minWindowSize.x, _maxWindowSize.x),
            Mathf.Clamp(size.y, _minWindowSize.y, _maxWindowSize.y));

        /// <summary> Persists the current window position and size — restored on the next launch. Called automatically after a drag/resize. </summary>
        public void SaveWindowState()
        {
            if (Application.isEditor || _uniWindowController == null)
                return;

            Vector2 position = _uniWindowController.windowPosition;
            Vector2 size = _uniWindowController.windowSize;
            _userData.Set(OverlayUserDataKeysProvider.Saved, true);
            _userData.Set(OverlayUserDataKeysProvider.PositionX, position.x);
            _userData.Set(OverlayUserDataKeysProvider.PositionY, position.y);
            _userData.Set(OverlayUserDataKeysProvider.SizeX, size.x);
            _userData.Set(OverlayUserDataKeysProvider.SizeY, size.y);
        }

        /// <summary> Saved state when there is one and it is still on a monitor, Start Corner otherwise. </summary>
        private void PositionWindow()
        {
            if (TryRestoreWindowState())
                return;

            _uniWindowController.windowSize = ClampSize(_windowSize);
            SnapToCorner(_startCorner, 0);
        }

        private bool TryRestoreWindowState()
        {
            if (!_userData.GetBool(OverlayUserDataKeysProvider.Saved))
                return false;

            var position = new Vector2(
                _userData.GetFloat(OverlayUserDataKeysProvider.PositionX),
                _userData.GetFloat(OverlayUserDataKeysProvider.PositionY));
            var size = ClampSize(new Vector2(
                _userData.GetFloat(OverlayUserDataKeysProvider.SizeX, _windowSize.x),
                _userData.GetFloat(OverlayUserDataKeysProvider.SizeY, _windowSize.y)));

            // Monitor layout could have changed since the save — the window must stay reachable.
            if (!IsVisibleOnAnyMonitor(new Rect(position, size)))
                return false;

            _uniWindowController.windowSize = size;
            _uniWindowController.windowPosition = position;
            return true;
        }

        /// <summary> Monitor layout changed — when the window fell off every screen, pull it back to Start Corner. </summary>
        private void EnsureVisible()
        {
            var windowRect = new Rect(_uniWindowController.windowPosition, _uniWindowController.windowSize);
            if (!IsVisibleOnAnyMonitor(windowRect))
                SnapToCorner(_startCorner, 0);
        }

        private static bool IsVisibleOnAnyMonitor(Rect windowRect)
        {
            int count = UniWindowController.GetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                Rect monitor = UniWindowController.GetMonitorRect(i);
                float overlapX = Mathf.Min(windowRect.xMax, monitor.xMax) - Mathf.Max(windowRect.xMin, monitor.xMin);
                float overlapY = Mathf.Min(windowRect.yMax, monitor.yMax) - Mathf.Max(windowRect.yMin, monitor.yMin);
                if (overlapX >= 50f && overlapY >= 50f)
                    return true;
            }

            return false;
        }

        private int MonitorUnderWindow()
        {
            Vector2 center = _uniWindowController.windowPosition + _uniWindowController.windowSize * 0.5f;

            int count = UniWindowController.GetMonitorCount();
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Rect rect = UniWindowController.GetMonitorRect(i);
                if (rect.Contains(center))
                    return i;

                float distance = (rect.center - center).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
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

        #region Drag and resize

        private void UpdateDrag(bool cursorValid, Vector2 screenPoint)
        {
            bool pressed = IsMousePressed();

            if (_drag != DragState.None)
            {
                if (!pressed)
                {
                    _drag = DragState.None;
                    SaveWindowState();
                }
                else
                {
                    ApplyDrag();
                }
            }
            else if (pressed && !_mouseWasPressed && cursorValid)
            {
                // Press started this frame over a handle — resize wins when they overlap.
                if (IsOverRect(_resizeHandle, screenPoint))
                    BeginDrag(DragState.Resize);
                else if (IsOverRect(_moveHandle, screenPoint))
                    BeginDrag(DragState.Move);
            }

            _mouseWasPressed = pressed;
        }

        private void BeginDrag(DragState state)
        {
            _drag = state;
            _dragStartCursor = _uniWindowController.cursorPosition;
            _dragStartPosition = _uniWindowController.windowPosition;
            _dragStartSize = _uniWindowController.windowSize;
        }

        private void ApplyDrag()
        {
            // The window follows the GLOBAL cursor — robust also when the cursor briefly leaves the window.
            Vector2 delta = _uniWindowController.cursorPosition - _dragStartCursor;

            if (_drag == DragState.Move)
            {
                _uniWindowController.windowPosition = _dragStartPosition + delta;
                return;
            }

            // Resize as a bottom-right grip: right = wider, down = taller. UniWinC coordinates have Y up
            // and windowPosition at the bottom-left, so the height change also shifts the position to keep
            // the TOP-left corner of the window in place.
            Vector2 size = ClampSize(new Vector2(_dragStartSize.x + delta.x, _dragStartSize.y - delta.y));
            _uniWindowController.windowSize = size;
            _uniWindowController.windowPosition = new Vector2(_dragStartPosition.x, _dragStartPosition.y + (_dragStartSize.y - size.y));
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

        #endregion

        #region Hit area

        private void InitializeHitArea()
        {
            RectTransform first = _moveHandle != null ? _moveHandle : _resizeHandle;
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
                    _onCursorEnter?.Invoke();
                else
                    _onCursorExit?.Invoke();
            }

            bool clickThrough = !over;
            if (clickThrough != _lastClickThrough)
            {
                _uniWindowController.isClickThrough = clickThrough;
                _lastClickThrough = clickThrough;
            }
        }

        /// <summary>
        /// The same conversion UniWinC does internally (GetClientCursorPosition): cursor and window
        /// are in UniWinC space (origin bottom-left of the monitor, Y up), cursor relative to the window
        /// rescaled to Screen pixels (matters with DPI != 100%). Borderless window → client area offset = 0.
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
            // Handles count as hit area automatically — otherwise the window would be
            // click-through over them and they could never be grabbed.
            if (IsOverRect(_moveHandle, screenPoint) || IsOverRect(_resizeHandle, screenPoint))
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

        #endregion
    }
}
