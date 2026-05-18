using UnityEngine;

namespace GemmaStage.Core
{
    /// <summary>
    /// Aligns this transform to face the rendering camera each LateUpdate. Used by
    /// world-space VR popups (Live Q&A, Final Q&A, End Session, Evaluation) so the
    /// player can read them regardless of head orientation.
    ///
    /// LateUpdate is used so the camera's tracked-pose update has already landed
    /// before the canvas re-orients — otherwise the panel jitters by one frame
    /// when the player turns their head.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LookAtCamera : MonoBehaviour
    {
        [Tooltip("Lock pitch and roll; only yaw the canvas toward the camera. Avoids tilted panels when the player looks up/down.")]
        [SerializeField] private bool yawOnly = true;

        [Tooltip("Optional explicit camera target. If null, falls back to Camera.main each frame.")]
        [SerializeField] private Transform cameraOverride;

        private void LateUpdate()
        {
            var target = cameraOverride != null ? cameraOverride : (Camera.main != null ? Camera.main.transform : null);
            if (target == null) return;

            var toCamera = target.position - transform.position;
            if (yawOnly) toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f) return;

            // Canvas faces +Z by convention, so the panel should look *away* from the camera direction.
            transform.rotation = Quaternion.LookRotation(-toCamera, Vector3.up);
        }
    }
}
