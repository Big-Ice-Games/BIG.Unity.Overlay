using System;
using System.Collections;
using System.Collections.Generic;
using Kirurobo;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// Overlay keys for <see cref="IUserData"/>. Run BIG > Generate User Keys to access them as Keys.Overlay.*.
    /// Key strings intentionally match the legacy Snap Chess PlayerPrefs keys, so players keep their settings
    /// after migrating to this plugin.
    /// </summary>
    public class OverlayUserDataKeysProvider : IUserDataKeysProvider
    {
        public string Name => "Overlay";

        public static readonly UserDataKey Corner = new UserDataKey("WINDOW_CORNER");
        public static readonly UserDataKey Monitor = new UserDataKey("WINDOW_MONITOR");
    }

    /// <summary>
    /// Heart of the overlay — one component owning the whole window:
    ///
    /// PLACEMENT — puts the window in the chosen corner of the chosen monitor's work area (above the taskbar)
    /// and keeps it there through resizes and monitor layout changes. Corner and monitor are persisted through
    /// <see cref="IUserData"/> and broadcast with BIG events (<see cref="OverlayCornerChanged"/>,
    /// <see cref="OverlayMonitorChanged"/>). Requires "Fit to monitor" DISABLED on UniWindowController.
    /// The window starts parked off-screen (see OverlayBootstrap) and shows up only here, after transparency
    /// is configured and the position computed.
    ///
    /// HIT AREA — click-through controlled by GEOMETRY instead of UniWinC's per-pixel opacity test
    /// (with per-pixel testing a fully hidden overlay becomes a permanent "hole" that can never detect hover).
    /// Cursor over any active hit rect → window clickable, outside → clicks fall through to the desktop.
    /// Hover changes are broadcast with <see cref="OverlayHoverChanged"/>.
    ///
    /// CORNER LAYOUT — listed UI elements re-anchor whenever the corner changes: to the window corner matching
    /// the screen corner, or to the inner horizontal/vertical side (e.g. a settings bar that always faces
    /// the screen center). Game-specific reactions subscribe to <see cref="OverlayCornerChanged"/> instead.
    /// </summary>
    public sealed class OverlayWindow : BaseBehaviour
    {
        [Serializable]
        public sealed class CornerLayoutElement
        {
            public enum Mode
            {
                WindowCorner,
                InnerHorizontal,
                InnerVertical,
            }

            public RectTransform Rect;
            public Mode Anchoring;
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
        [SerializeField, Tooltip("Fixed window size in pixels. Content should scale INSIDE the window — resizing the OS window causes flicker.")]
        private Vector2 _windowSize = new Vector2(1100f, 900f);

#if ODIN_INSPECTOR
        [FoldoutGroup("Placement")]
#else
        [Header("Placement")]
#endif
        [SerializeField] private OverlayCorner _corner = OverlayCorner.BottomRight;

#if ODIN_INSPECTOR
        [FoldoutGroup("Placement")]
#endif
        [SerializeField, Tooltip("Monitor index (0 = first per UniWinC). Overwritten by the persisted user choice.")]
        private int _monitorIndex;

#if ODIN_INSPECTOR
        [FoldoutGroup("Placement")]
#endif
        [SerializeField, Tooltip("Window offset from the work area edges, in pixels. (0,0) = window flush with the taskbar and screen edge.")]
        private Vector2 _margin = Vector2.zero;

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#else
        [Header("Hit Area")]
#endif
        [SerializeField, Tooltip("Cursor over ANY of these rects = window clickable, outside = click-through. Inactive objects are skipped. Include the drag handle rect! Empty list = click-through stays in UniWinC mode.")]
        private List<RectTransform> _hitRects = new List<RectTransform>();

#if ODIN_INSPECTOR
        [FoldoutGroup("Hit Area")]
#endif
        [SerializeField, Tooltip("Extra margin around the hit rects in screen pixels — easier to hit a hidden overlay with the cursor.")]
        private float _hitPadding = 8f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Corner Layout")]
#else
        [Header("Corner Layout")]
#endif
        [SerializeField, Tooltip("UI elements re-anchored on corner change: WindowCorner sticks to the matching window corner, Inner* sticks to the side opposite to the screen edge.")]
        private List<CornerLayoutElement> _cornerLayout = new List<CornerLayoutElement>();

        private Camera _uiCamera;
        private bool _hitAreaActive;
        private bool _lastClickThrough = true;
        private bool _cursorOver;

        /// <summary> Monitor count — for building selection UI. </summary>
        public static int MonitorCount => Application.isEditor ? 1 : UniWindowController.GetMonitorCount();

        public OverlayCorner CurrentCorner => _corner;
        public int CurrentMonitorIndex => _monitorIndex;

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
            _corner = (OverlayCorner)_userData.GetInt(OverlayUserDataKeysProvider.Corner, (int)_corner);
            _monitorIndex = _userData.GetInt(OverlayUserDataKeysProvider.Monitor, _monitorIndex);

            // Layout must match the restored corner also in the editor.
            ApplyCornerLayout(_corner);
            Events.Raise(new OverlayCornerChanged(_corner));

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

            _uniWindowController.windowSize = _windowSize;
            ApplyPlacement();

            // Window style changes (borderless/layered) can move the window once more a moment later.
            yield return new WaitForSecondsRealtime(0.5f);
            ApplyPlacement();

            _uniWindowController.OnMonitorChanged += ApplyPlacement;
        }

        public override void OnDestroy()
        {
            if (_uniWindowController != null)
                _uniWindowController.OnMonitorChanged -= ApplyPlacement;

            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        #region Placement

        /// <summary> Int overload for wiring directly into UnityEvents (0=BottomRight, 1=BottomLeft, 2=TopRight, 3=TopLeft). </summary>
        public void SetCorner(int cornerIndex) => SetCorner((OverlayCorner)Mathf.Clamp(cornerIndex, 0, 3));

        /// <summary> Moves the window to the corner, persists the choice, re-anchors the layout and raises <see cref="OverlayCornerChanged"/>. </summary>
        public void SetCorner(OverlayCorner corner)
        {
            _corner = corner;
            _userData.Set(OverlayUserDataKeysProvider.Corner, (int)corner);
            ApplyCornerLayout(corner);
            ApplyPlacement();
            Events.Raise(new OverlayCornerChanged(corner));
        }

        /// <summary> Moves the window to the monitor, persists the choice and raises <see cref="OverlayMonitorChanged"/>. </summary>
        public void SetMonitor(int monitorIndex)
        {
            _monitorIndex = Mathf.Max(0, monitorIndex);
            _userData.Set(OverlayUserDataKeysProvider.Monitor, _monitorIndex);
            ApplyPlacement();
            Events.Raise(new OverlayMonitorChanged(_monitorIndex));
        }

        /// <summary>
        /// Snaps to the nearest corner of the monitor the window currently sits on —
        /// called by <see cref="OverlayDragHandle"/> on drag release. Persists monitor and corner.
        /// </summary>
        public void SnapToNearestCorner()
        {
            if (Application.isEditor || _uniWindowController == null)
                return;

            Vector2 center = _uniWindowController.windowPosition + _uniWindowController.windowSize * 0.5f;

            // Monitor under the window center (or the nearest one when dropped between monitors).
            int count = UniWindowController.GetMonitorCount();
            int best = 0;
            float bestDistance = float.MaxValue;
            Rect bestRect = default;
            for (int i = 0; i < count; i++)
            {
                Rect rect = UniWindowController.GetMonitorRect(i);
                if (rect.Contains(center))
                {
                    best = i;
                    bestRect = rect;
                    break;
                }

                float distance = (rect.center - center).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    bestRect = rect;
                }
            }

            if (best != _monitorIndex)
            {
                _monitorIndex = best;
                _userData.Set(OverlayUserDataKeysProvider.Monitor, best);
                Events.Raise(new OverlayMonitorChanged(best));
            }

            // Nearest corner = which quadrant of the monitor the window center landed in.
            bool left = center.x < bestRect.center.x;
            bool bottom = center.y < bestRect.center.y;
            OverlayCorner corner = left
                ? (bottom ? OverlayCorner.BottomLeft : OverlayCorner.TopLeft)
                : (bottom ? OverlayCorner.BottomRight : OverlayCorner.TopRight);

            SetCorner(corner);
        }

        public void ApplyPlacement()
        {
            if (Application.isEditor || _uniWindowController == null)
                return;

            Rect workArea = ResolveTargetWorkArea();

            // workArea is in UniWinC space (origin bottom-left of the primary monitor, Y up);
            // windowPosition is the bottom-left corner of the window.
            Vector2 size = _uniWindowController.windowSize;

            float x = _corner is OverlayCorner.BottomLeft or OverlayCorner.TopLeft
                ? workArea.xMin + _margin.x
                : workArea.xMax - size.x - _margin.x;

            float y = _corner is OverlayCorner.BottomRight or OverlayCorner.BottomLeft
                ? workArea.yMin + _margin.y
                : workArea.yMax - size.y - _margin.y;

            _uniWindowController.windowPosition = new Vector2(x, y);
        }

        /// <summary>
        /// Work area of the selected monitor. A detached monitor (index out of range) silently falls back
        /// to monitor 0 — the preference stays, so the overlay returns to the chosen screen once it is reattached.
        /// </summary>
        private Rect ResolveTargetWorkArea()
        {
            int count = UniWindowController.GetMonitorCount();
            int index = _monitorIndex < count ? _monitorIndex : 0;

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

        #region Corner layout

        private void ApplyCornerLayout(OverlayCorner corner)
        {
            bool left = corner is OverlayCorner.BottomLeft or OverlayCorner.TopLeft;
            bool top = corner is OverlayCorner.TopRight or OverlayCorner.TopLeft;

            foreach (CornerLayoutElement element in _cornerLayout)
            {
                if (element?.Rect == null)
                    continue;

                RectTransform rect = element.Rect;
                switch (element.Anchoring)
                {
                    case CornerLayoutElement.Mode.WindowCorner:
                        var anchor = new Vector2(left ? 0f : 1f, top ? 1f : 0f);
                        rect.anchorMin = anchor;
                        rect.anchorMax = anchor;
                        rect.pivot = anchor;
                        rect.anchoredPosition = Vector2.zero;
                        break;

                    case CornerLayoutElement.Mode.InnerHorizontal:
                        float x = left ? 1f : 0f;
                        rect.anchorMin = new Vector2(x, 0f);
                        rect.anchorMax = new Vector2(x, 1f);
                        break;

                    case CornerLayoutElement.Mode.InnerVertical:
                        float y = top ? 0f : 1f;
                        rect.anchorMin = new Vector2(0f, y);
                        rect.anchorMax = new Vector2(1f, y);
                        break;
                }
            }
        }

        #endregion

        #region Hit area

        private void InitializeHitArea()
        {
            RectTransform first = null;
            foreach (RectTransform rect in _hitRects)
            {
                if (rect != null)
                {
                    first = rect;
                    break;
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

        private void Update()
        {
            if (!_hitAreaActive)
                return;

            bool over = IsCursorOverHitRect();
            if (over != _cursorOver)
            {
                _cursorOver = over;
                Events.Raise(new OverlayHoverChanged(over));
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
        private bool IsCursorOverHitRect()
        {
            Vector2 cursorInWindow = _uniWindowController.cursorPosition - _uniWindowController.windowPosition;
            Vector2 client = _uniWindowController.clientSize;
            if (client.x <= 0f || client.y <= 0f)
                return false;

            if (cursorInWindow.x < 0f || cursorInWindow.y < 0f || cursorInWindow.x > client.x || cursorInWindow.y > client.y)
                return false;

            var screenPoint = new Vector2(
                cursorInWindow.x * Screen.width / client.x,
                cursorInWindow.y * Screen.height / client.y);

            foreach (RectTransform rect in _hitRects)
            {
                // An inactive object (hidden panel) must not catch the mouse —
                // otherwise an invisible rect would block the desktop under it.
                if (rect == null || !rect.gameObject.activeInHierarchy)
                    continue;

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, _uiCamera, out Vector2 local))
                    continue;

                Rect r = rect.rect;
                if (local.x >= r.xMin - _hitPadding && local.x <= r.xMax + _hitPadding
                    && local.y >= r.yMin - _hitPadding && local.y <= r.yMax + _hitPadding)
                {
                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}
