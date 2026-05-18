using UnityEngine;

namespace GemmaStage.Environments.Stage
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class BillboardToCamera : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var dir = transform.position - cam.transform.position;
            if (dir.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }
    }
}
