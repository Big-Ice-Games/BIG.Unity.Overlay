using Kirurobo;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BIG.Unity.Overlay
{
    /// <summary>
    /// Drag handle for the overlay window: press and hold to move the whole OS window with the cursor;
    /// on release the window snaps to the nearest corner of the monitor it was dropped on
    /// (via <see cref="OverlayWindow.SnapToNearestCorner"/> — persisted and broadcast as usual).
    ///
    /// NOTE: the handle's RectTransform must be included in the <see cref="OverlayWindow"/> Hit Area rect list,
    /// otherwise the window is click-through over the handle and it can never be grabbed.
    /// Does nothing in the editor.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class OverlayDragHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField, Tooltip("Snap to the nearest screen corner on release. Off = window stays where dropped until the next ApplyPlacement.")]
        private bool _snapToNearestCorner = true;

        private UniWindowController _window;
        private bool _dragging;
        private Vector2 _cursorOffset;

        private void Start()
        {
            if (Application.isEditor)
            {
                enabled = false;
                return;
            }

            _window = FindAnyObjectByType<UniWindowController>();
            if (_window == null)
            {
                this.Log("No UniWindowController — drag handle disabled.", LogLevel.Error);
                enabled = false;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!enabled)
                return;

            _cursorOffset = _window.cursorPosition - _window.windowPosition;
            _dragging = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_dragging)
                return;

            _dragging = false;
            if (_snapToNearestCorner)
                OverlayWindow.Instance?.SnapToNearestCorner();
        }

        private void LateUpdate()
        {
            if (_dragging)
                _window.windowPosition = _window.cursorPosition - _cursorOffset;
        }
    }
}
