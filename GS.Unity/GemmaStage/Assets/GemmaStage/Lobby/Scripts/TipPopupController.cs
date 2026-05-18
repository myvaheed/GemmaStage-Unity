using System.Collections;
using GemmaStage.Core;
using TMPro;
using UnityEngine;

namespace GemmaStage.Lobby
{
    public sealed class TipPopupController : MonoBehaviour
    {
        public static TipPopupController Instance { get; private set; }

        [Header("Popup")]
        [SerializeField] Canvas popupCanvas;
        [SerializeField] RectTransform popupRect;
        [SerializeField] TextMeshProUGUI tipLabel;

        [Header("Camera")]
        [Tooltip("XR camera the popup orients toward. Falls back to Camera.main when unset.")]
        [SerializeField] Camera xrCamera;

        [Header("Placement (world units)")]
        [Tooltip("Gap between the row's top edge and the popup's bottom edge.")]
        [SerializeField] float aboveOffset = 0.015f;
        [Tooltip("How far the popup floats off the panel surface toward the camera.")]
        [SerializeField] float forwardOffset = 0.015f;

        [Header("Bounce (Show animation)")]
        [Tooltip("Total appear duration in seconds. EaseOutBack from scale 0 → resting with overshoot.")]
        [SerializeField, Range(0.05f, 0.6f)] float bounceDurationSeconds = 0.22f;
        [Tooltip("Overshoot amount of the EaseOutBack curve. Higher = more bouncy.")]
        [SerializeField, Range(0f, 4f)] float bounceOvershoot = 1.70158f;

        TipHoverGroup currentOwner;
        Coroutine showAnim;
        // Captured at Awake so the bounce animates relative to the prefab's
        // authored canvas scale (0.001) instead of stomping it with Vector3.one
        // — that bug grew the popup to ~220 m wide in the headset.
        Vector3 restingScale = Vector3.one;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (popupRect != null)
            {
                restingScale = popupRect.localScale;
                popupRect.localScale = Vector3.zero;
            }
            if (popupCanvas != null)
                popupCanvas.enabled = false;

            // StylesBootstrap (Shared scene, DefaultExecutionOrder -1000) has
            // populated Styles.Type.Inter by now. Apply it so the popup picks
            // up the project font instead of TMP's default.
            if (tipLabel != null && Styles.Type.Inter != null)
                tipLabel.font = Styles.Type.Inter;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void Show(TipHoverGroup requester, string text, Transform anchor)
        {
            if (popupCanvas == null || popupRect == null || anchor == null)
                return;

            currentOwner = requester;
            if (tipLabel != null)
                tipLabel.text = text;

            // Force ContentSizeFitter / VerticalLayoutGroup to recompute *before*
            // the first frame so PlaceAt can read an up-to-date popupRect.rect.
            Canvas.ForceUpdateCanvases();

            PlaceAt(anchor);
            popupCanvas.enabled = true;

            if (showAnim != null) StopCoroutine(showAnim);
            showAnim = StartCoroutine(BounceIn());
        }

        public void Hide(TipHoverGroup requester)
        {
            if (currentOwner != requester)
                return;

            currentOwner = null;
            if (showAnim != null)
            {
                StopCoroutine(showAnim);
                showAnim = null;
            }
            if (popupCanvas != null)
                popupCanvas.enabled = false;
            if (popupRect != null)
                popupRect.localScale = Vector3.zero;
        }

        public bool IsShowingFor(TipHoverGroup requester) => currentOwner == requester;

        // Hosted-on-controller grace coroutine. The controller's GameObject is
        // always active, so this can be called safely from a row's OnDisable
        // cascade where the row's own MonoBehaviour can't host coroutines.
        Coroutine pendingHide;
        TipHoverGroup pendingHideOwner;

        public void RequestDelayedHide(TipHoverGroup requester, float delaySeconds)
        {
            if (currentOwner != requester) return;

            CancelPendingHide();
            pendingHideOwner = requester;
            // If the GO became inactive between request and now, fall back.
            if (!isActiveAndEnabled || delaySeconds <= 0f)
            {
                Hide(requester);
                pendingHideOwner = null;
                return;
            }
            pendingHide = StartCoroutine(DelayedHide(requester, delaySeconds));
        }

        public void CancelPendingHide(TipHoverGroup requester)
        {
            if (pendingHideOwner != requester) return;
            CancelPendingHide();
        }

        void CancelPendingHide()
        {
            if (pendingHide != null) StopCoroutine(pendingHide);
            pendingHide = null;
            pendingHideOwner = null;
        }

        IEnumerator DelayedHide(TipHoverGroup requester, float delaySeconds)
        {
            float t = 0f;
            while (t < delaySeconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            pendingHide = null;
            pendingHideOwner = null;
            Hide(requester);
        }

        void LateUpdate()
        {
            if (popupCanvas == null || !popupCanvas.enabled || currentOwner == null)
                return;

            var anchor = currentOwner.AnchorTransform;
            if (anchor == null)
                return;

            PlaceAt(anchor);
        }

        IEnumerator BounceIn()
        {
            float t = 0f;
            while (t < bounceDurationSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / bounceDurationSeconds);
                float eased = EaseOutBack(k, bounceOvershoot);
                popupRect.localScale = restingScale * eased;
                yield return null;
            }
            popupRect.localScale = restingScale;
            showAnim = null;
        }

        // Penner's ease-out-back: overshoots past 1 then settles, for an arcade
        // pop-in feel. s=1.70158 is the canonical default.
        static float EaseOutBack(float t, float s)
        {
            t -= 1f;
            return t * t * ((s + 1f) * t + s) + 1f;
        }

        void PlaceAt(Transform anchor)
        {
            var cam = xrCamera != null ? xrCamera : Camera.main;
            Vector3 toCamera = cam != null
                ? (cam.transform.position - anchor.position).normalized
                : -anchor.forward;

            // Find the anchor's TOP-CENTER in world space. Falls back to its
            // pivot when the anchor isn't a RectTransform (rare — rows are UGUI).
            Vector3 anchorTop = anchor.position;
            if (anchor is RectTransform anchorRect)
            {
                var corners = new Vector3[4];
                anchorRect.GetWorldCorners(corners);   // 0=BL, 1=TL, 2=TR, 3=BR
                anchorTop = (corners[1] + corners[2]) * 0.5f;
            }

            // Offset upward by aboveOffset PLUS the popup's own half-height so
            // the popup's bottom edge sits exactly aboveOffset above the row's
            // top edge. Pivot is centered, hence the half-height.
            float popupHalfHeightWorld = popupRect.rect.height * 0.5f * popupRect.lossyScale.y;

            popupRect.position = anchorTop
                                 + anchor.up * (aboveOffset + popupHalfHeightWorld)
                                 + toCamera * forwardOffset;

            if (cam != null)
            {
                Vector3 awayFromCamera = popupRect.position - cam.transform.position;
                if (awayFromCamera.sqrMagnitude > 1e-6f)
                    popupRect.rotation = Quaternion.LookRotation(awayFromCamera, anchor.up);
            }
            else
            {
                popupRect.rotation = anchor.rotation;
            }
        }
    }
}
