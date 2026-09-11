using System.Collections;
using System.Collections.Generic;
using Kirurobo;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
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

        public static readonly UserDataKey Saved = new UserDataKey("OVERLAY_SAVED");
        public static readonly UserDataKey PositionX = new UserDataKey("OVERLAY_POS_X");
        public static readonly UserDataKey PositionY = new UserDataKey("OVERLAY_POS_Y");
        public static readonly UserDataKey Scale = new UserDataKey("OVERLAY_SCALE");
    }

    /// <summary>
    /// The one overlay component.
    ///
    /// WINDOW — the OS window is an invisible layer automatically stretched over the ENTIRE virtual desktop
    /// (all monitors, kept in sync when the monitor layout changes). It never moves and never resizes,
    /// so there is no window flicker — what the player drags and scales is the CONTENT rect (your game panel),
    /// with pure uGUI: smoothly, across monitors, like moving a sticker over the desktop.
    /// The window starts parked off-screen (see OverlayBootstrap) and shows up only here, already transparent.
    ///
    /// CONTENT — on first launch the content aligns flush to Start Corner of the primary monitor's work area
    /// (above the taskbar). Corners are one-shot moves (<see cref="SnapContentToCorner(OverlayCorner)"/>) — no corner
    /// state is kept. After the player drags or scales the content, its position and scale persist through
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
        [FoldoutGroup("Content")]
#else
        [Header("Content")]
#endif
        [SerializeField, Tooltip("The draggable game panel. Use single-point anchors (anchorMin == anchorMax); any pivot works.")]
        private RectTransform _content;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Corner of the primary monitor's work area the content aligns to on first launch. One-shot — no corner state is kept afterwards.")]
        private OverlayCorner _startCorner = OverlayCorner.BottomRight;

#if ODIN_INSPECTOR
        [FoldoutGroup("Content")]
#endif
        [SerializeField, Tooltip("Grab this rect to drag the content around the desktop — can safely be the WHOLE content: a press on an interactive element (button, slider, anything with pointer/drag handlers, e.g. a chess piece) never starts the drag. Counts as hit area automatically.")]
        private RectTransform _moveHandle;

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

        private bool _dragging;
        private bool _mouseWasPressed;
        private Vector2 _dragOffset;
        private float _scale = 1f;
        private bool _stateDirty;

        /// <summary> Whether the global cursor (regardless of click-through) is over the hit area. </summary>
        public bool IsCursorOver => _cursorOver;

        /// <summary> Current content scale. </summary>
        public float ContentScale => _scale;

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
                // Without the controller we cannot manage the window through UniWinC,
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

            CoverVirtualDesktop();
            PositionContent();

            // Window style changes (borderless/layered) can move the window once more a moment later.
            yield return new WaitForSecondsRealtime(0.5f);
            CoverVirtualDesktop();

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
            if (!_hitAreaActive)
                return;

            bool cursorValid = TryGetCursorScreenPoint(out Vector2 screenPoint);

            UpdateHover(cursorValid, screenPoint);
            UpdateDrag(cursorValid, screenPoint);
            UpdateScrollScale();
        }

        #region Window

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
            CoverVirtualDesktop();

            if (_content == null || !TryGetContentScreenRect(out Rect contentRect))
                return;

            // When the monitor with the content got detached, pull the content back to Start Corner.
            if (!IsVisibleOnAnyMonitor(ScreenToUniWin(contentRect)))
                SnapContentToCorner(_startCorner, 0);
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

        /// <summary> Set content scale (clamped to Min/Max Scale). </summary>
        public void SetContentScale(float scale)
        {
            _scale = Mathf.Clamp(scale, _minScale, _maxScale);
            if (_content != null)
                _content.localScale = new Vector3(_scale, _scale, 1f);
            _stateDirty = true;
        }

        private void UpdateDrag(bool cursorValid, Vector2 screenPoint)
        {
            bool pressed = IsMousePressed();

            if (_dragging)
            {
                if (!pressed)
                {
                    _dragging = false;
                    SaveContentState();
                }
                else if (cursorValid && _content != null && _content.parent is RectTransform parent
                         && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPoint, _uiCamera, out Vector2 local))
                {
                    _content.anchoredPosition = local - _dragOffset;
                    _stateDirty = true;
                }
            }
            else if (pressed && !_mouseWasPressed && cursorValid
                     && _content != null && IsOverRect(_moveHandle, screenPoint)
                     && !IsOverInteractiveUi(screenPoint)
                     && _content.parent is RectTransform parent
                     && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPoint, _uiCamera, out Vector2 local))
            {
                _dragOffset = local - _content.anchoredPosition;
                _dragging = true;
            }

            _mouseWasPressed = pressed;
        }

        private void UpdateScrollScale()
        {
            // Step 0 = the game owns the wheel — do NOT touch the shared GlobalMouseScroll counter at all.
            if (_scrollScaleStep <= 0f || _content == null)
                return;

            // Drain the counter every frame; apply only when the cursor is over the overlay.
            float notches = GlobalMouseScroll.ConsumeNotches();
            if (!_cursorOver || notches == 0f)
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

            SnapContentToCorner(_startCorner, 0);
        }

        /// <summary> Persists content position and scale — restored on the next launch. Called automatically after drag/scale. </summary>
        public void SaveContentState()
        {
            if (Application.isEditor || _content == null || !_stateDirty)
                return;

            _stateDirty = false;
            Vector2 position = _content.anchoredPosition;
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

            bool clickThrough = !over;
            if (clickThrough != _lastClickThrough)
            {
                _uniWindowController.isClickThrough = clickThrough;
                _lastClickThrough = clickThrough;
            }
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
        /// True when the press landed on an interactive uGUI element (button, slider, draggable piece...) —
        /// anything with pointer-down/click/drag handlers on itself or a parent. Such elements win over
        /// the content drag, so the Move Handle can safely cover the whole content.
        /// </summary>
        private static bool IsOverInteractiveUi(Vector2 screenPoint)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                return false;

            var pointer = new PointerEventData(eventSystem) { position = screenPoint };
            RAYCAST_RESULTS.Clear();
            eventSystem.RaycastAll(pointer, RAYCAST_RESULTS);

            foreach (RaycastResult result in RAYCAST_RESULTS)
            {
                GameObject target = result.gameObject;
                if (ExecuteEvents.GetEventHandler<IPointerDownHandler>(target) != null
                    || ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) != null
                    || ExecuteEvents.GetEventHandler<IBeginDragHandler>(target) != null
                    || ExecuteEvents.GetEventHandler<IDragHandler>(target) != null)
                {
                    RAYCAST_RESULTS.Clear();
                    return true;
                }
            }

            RAYCAST_RESULTS.Clear();
            return false;
        }

        #endregion
    }
}
