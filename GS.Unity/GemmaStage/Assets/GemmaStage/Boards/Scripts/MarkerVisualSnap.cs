using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Visual decoupling for the marker: when the held marker's tip penetrates (or is very
    /// close to) the owning <see cref="DrawingBoard"/>'s surface plane, the visible
    /// <see cref="model"/> is offset so its tip-end visually rests on the surface, even
    /// though the grabbed root keeps following the controller. Mirrors the VRPen
    /// <c>modelParent</c> snap-in-LateUpdate pattern (see NOTES_FROM_VR_REPO §2).
    ///
    /// We do not add board collision to stop the marker physically — XRGrabInteractable's
    /// Instantaneous movement bypasses physics, and switching to VelocityTracking trades
    /// snappy grab feel for mushy collisions. Visual-only snap keeps the hand pose
    /// instant while the brush appears to ride the surface.
    /// </summary>
    [DisallowMultipleComponent]
    public class MarkerVisualSnap : MonoBehaviour
    {
        [Tooltip("The visible mesh transform that gets visually pinned to the surface. Typically the Body child.")]
        [SerializeField] Transform model;
        [Tooltip("Empty transform at the marker tip. Used as the proximity probe.")]
        [SerializeField] Transform markerTip;
        [Tooltip("Optional. If unset, resolved via GetComponentInParent at Awake.")]
        [SerializeField] DrawingBoard board;
        [Tooltip("Optional. If unset, resolved via GetComponent at Awake.")]
        [SerializeField] XRGrabInteractable grabInteractable;

        [Tooltip("Distance in front of the surface plane (player side, +Z) at which the visual starts snapping. Match DrawingMarker.drawDistanceFront so the visual lands exactly when drawing activates.")]
        [SerializeField] float snapDistanceFront = 0.01f;
        [Tooltip("Distance behind the surface plane (-Z, penetration side) within which the visual stays snapped. Match DrawingMarker.drawDistanceBack so the visual remains pinned while the marker is pressed through.")]
        [SerializeField] float snapDistanceBack = 0.07f;

        Vector3 modelDefaultLocalPos;
        Quaternion modelDefaultLocalRot;
        bool wired;

        void Awake()
        {
            if (model == null)
            {
                Debug.LogError("[MarkerVisualSnap] model unwired.", this);
                enabled = false;
                return;
            }
            if (markerTip == null)
            {
                Debug.LogError("[MarkerVisualSnap] markerTip unwired.", this);
                enabled = false;
                return;
            }

            modelDefaultLocalPos = model.localPosition;
            modelDefaultLocalRot = model.localRotation;

            if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
            if (board == null) board = GetComponentInParent<DrawingBoard>();
            wired = true;
        }

        void LateUpdate()
        {
            if (!wired) return;

            if (board == null || grabInteractable == null || !grabInteractable.isSelected)
            {
                ResetModel();
                return;
            }

            var surface = board.SurfaceTransform;
            if (surface == null) { ResetModel(); return; }

            // Project the marker tip into surface-local space. Z is signed depth from
            // the surface plane; X/Y are in-plane. Asymmetric tolerance: tight on the
            // approach side (+Z) so the visual doesn't snap prematurely, loose on the
            // penetration side (-Z) so it stays pinned while the player presses through.
            Vector3 localTip = surface.InverseTransformPoint(markerTip.position);
            if (localTip.z > snapDistanceFront || localTip.z < -snapDistanceBack) { ResetModel(); return; }

            // Only snap when the tip is over the drawable region. This keeps the
            // visual from "sticking" off the side of the board.
            Vector2 sz = board.SurfaceLocalSize;
            float u = localTip.x / sz.x + 0.5f;
            float v = localTip.y / sz.y + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) { ResetModel(); return; }

            // Contact point: project the tip onto the surface plane (Z = 0 in surface
            // local space), convert back to world.
            Vector3 contactWorld = surface.TransformPoint(new Vector3(localTip.x, localTip.y, 0f));
            Vector3 displacement = contactWorld - markerTip.position;

            // Apply displacement to the model. We do this in the marker root's local
            // frame so the visible offset is independent of how the controller is
            // oriented — the mesh keeps its rotation and just slides along the
            // controller-to-surface axis.
            Vector3 modelDefaultWorld = transform.TransformPoint(modelDefaultLocalPos);
            Vector3 modelWorld = modelDefaultWorld + displacement;
            model.localPosition = transform.InverseTransformPoint(modelWorld);
            model.localRotation = modelDefaultLocalRot;
        }

        void OnDisable()
        {
            ResetModel();
        }

        void ResetModel()
        {
            if (model == null) return;
            model.localPosition = modelDefaultLocalPos;
            model.localRotation = modelDefaultLocalRot;
        }
    }
}
