using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Drives drawing on the <see cref="DrawingBoard"/> while the marker is held. Detection is
    /// proximity-based: the marker tip's world position is projected onto the board's surface
    /// plane; drawing starts when the tip is within <see cref="drawDistance"/> of the plane and
    /// inside the board's UV bounds. This approach works regardless of the angle at which the
    /// marker is held — no directional raycast needed.
    ///
    /// Interaction model: GAME_DESIGN §5.1 — markers are grabbed (XRGrabInteractable, see
    /// Marker.prefab) and drawing happens passively while the tip touches the board surface.
    /// The owning board is captured at Awake via <c>GetComponentInParent</c>; markers are
    /// always spawned under their DrawingBoard (by MarkerTray) before they're ever grabbed.
    /// </summary>
    [RequireComponent(typeof(MarkerColor))]
    public class DrawingMarker : MonoBehaviour
    {
        [SerializeField] Transform markerTip;
        [SerializeField] XRGrabInteractable grabInteractable;
        [Tooltip("Distance in front of the surface plane (player side, +Z) at which drawing activates (metres). Tight value avoids premature drawing as the marker approaches the board.")]
        [SerializeField] float drawDistanceFront = 0.01f;
        [Tooltip("Distance behind the surface plane (-Z, penetration side) at which drawing remains active (metres). Loose value lets the user press the marker through the board without losing the drawing band.")]
        [SerializeField] float drawDistanceBack = 0.07f;

        MarkerColor markerColor;
        DrawingBoard board;
        bool drawing;

        void Awake()
        {
            markerColor = GetComponent<MarkerColor>();
            if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
            board = GetComponentInParent<DrawingBoard>();
        }

        void Update()
        {
            bool held = grabInteractable != null && grabInteractable.isSelected;
            if (!held)
            {
                if (drawing) FinishStroke();
                return;
            }

            if (markerTip == null || board == null) return;

            if (!board.TryWorldToUVWithProximity(markerTip.position, drawDistanceFront, drawDistanceBack, out var uv))
            {
                if (drawing) FinishStroke();
                return;
            }

            if (!drawing)
            {
                board.BeginStroke(markerColor.StrokeColor, uv);
                drawing = true;
            }
            else
            {
                board.ExtendStroke(uv);
            }
        }

        void OnDisable()
        {
            if (drawing) FinishStroke();
        }

        void FinishStroke()
        {
            if (board != null) board.EndStroke();
            drawing = false;
        }
    }
}
