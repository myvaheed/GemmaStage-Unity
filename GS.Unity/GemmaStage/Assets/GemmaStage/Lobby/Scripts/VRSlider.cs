using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemmaStage.Lobby
{
    // Stock UnityEngine.UI.Slider has three VR-hostile behaviors when driven by
    // XRI's XRUIInputModule:
    //
    // 1. ProcessNavigationState dispatches IMoveHandler.OnMove every frame from
    //    the controller's 2D Move axis. Slider.OnMove steps value by stepSize on
    //    each Left/Right, so sub-deadzone thumbstick noise alternates value
    //    +/-1 forever the moment the slider becomes the EventSystem's selected
    //    element (e.g. the first time it goes interactable). VR ray UI has no
    //    notion of "move focus by axis", so we suppress OnMove entirely.
    //
    // 2. Slider.UpdateDrag reads eventData.position (a 2D screen coordinate).
    //    For tracked devices the input module rebuilds that each frame from the
    //    ray's current hit point — which jitters as the ray drifts off the
    //    slider's plane. Worked around by Unity staff in
    //    https://discussions.unity.com/t/vr-ui-slider-issues/821416/15 by
    //    replacing UpdateDrag with an explicit world-space ray vs slider-plane
    //    intersection. We do the same as an OnDrag override.
    //
    // 3. On trigger release, XRUIInputModule delivers PointerUp to whatever the
    //    ray currently points at — not the original press target. If the ray
    //    drifts off the slider before release, the slider can miss PointerUp
    //    and EventSystem keeps it as pointerDrag. We poll XR controller trigger
    //    state and force-end the drag if no trigger is held.
    public class VRSlider : Slider
    {
        static readonly List<InputDevice> DeviceBuffer = new List<InputDevice>();
        static readonly Vector3[] CornerBuffer = new Vector3[4];

        PointerEventData _activePointer;

        public override void OnMove(AxisEventData eventData)
        {
            // VR ray UI doesn't use axis-based focus navigation. Stock OnMove
            // steps the slider value on Left/Right, which thumbstick deadzone
            // noise drives into a perpetual +/-1 oscillation as soon as the
            // slider is the selected Selectable.
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            _activePointer = eventData;
        }

        public override void OnPointerUp(PointerEventData eventData)
        {
            base.OnPointerUp(eventData);
            if (ReferenceEquals(_activePointer, eventData))
                _activePointer = null;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _activePointer = null;
        }

        public override void OnDrag(PointerEventData eventData)
        {
            if (eventData is TrackedDeviceEventData tracked
                && IsActive() && IsInteractable()
                && eventData.button == PointerEventData.InputButton.Left)
            {
                UpdateDragFromRay(tracked);
                return;
            }
            base.OnDrag(eventData);
        }

        void UpdateDragFromRay(TrackedDeviceEventData tracked)
        {
            var clickRect = ResolveClickRect();
            if (clickRect == null) return;

            clickRect.GetWorldCorners(CornerBuffer);
            var plane = new Plane(CornerBuffer[0], CornerBuffer[1], CornerBuffer[2]);

            var rayPoints = tracked.rayPoints;
            if (rayPoints == null || rayPoints.Count < 2) return;

            Vector3 hitWorld = default;
            bool gotHit = false;
            for (int i = 1; i < rayPoints.Count; i++)
            {
                var from = rayPoints[i - 1];
                var to = rayPoints[i];
                var segLen = Vector3.Distance(to, from);
                if (segLen <= 0f) continue;

                var ray = new Ray(from, (to - from) / segLen);
                if (plane.Raycast(ray, out float dist) && dist <= segLen)
                {
                    hitWorld = ray.GetPoint(dist);
                    gotHit = true;
                    break;
                }
            }
            if (!gotHit) return;

            var local = clickRect.InverseTransformPoint(hitWorld);
            var rect = clickRect.rect;

            bool horizontal = direction == Direction.LeftToRight || direction == Direction.RightToLeft;
            bool reverse = direction == Direction.RightToLeft || direction == Direction.TopToBottom;

            float t = horizontal
                ? Mathf.InverseLerp(rect.xMin, rect.xMax, local.x)
                : Mathf.InverseLerp(rect.yMin, rect.yMax, local.y);

            normalizedValue = reverse ? 1f - t : t;
        }

        RectTransform ResolveClickRect()
        {
            if (handleRect != null && handleRect.parent is RectTransform handleParent)
                return handleParent;
            if (fillRect != null && fillRect.parent is RectTransform fillParent)
                return fillParent;
            return null;
        }

        protected override void Update()
        {
            base.Update();

            if (_activePointer == null) return;
            if (AnyControllerTriggerHeld()) return;

            var pointer = _activePointer;
            _activePointer = null;

            if (pointer.pointerDrag == gameObject)
                pointer.pointerDrag = null;
            if (pointer.pointerPress == gameObject)
                pointer.pointerPress = null;
            pointer.dragging = false;
            pointer.eligibleForClick = false;

            base.OnPointerUp(pointer);
        }

        static bool AnyControllerTriggerHeld()
        {
            DeviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller, DeviceBuffer);
            for (int i = 0; i < DeviceBuffer.Count; i++)
            {
                if (DeviceBuffer[i].TryGetFeatureValue(
                        CommonUsages.triggerButton, out bool pressed) && pressed)
                    return true;
            }
            return false;
        }
    }
}
