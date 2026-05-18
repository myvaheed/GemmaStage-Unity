using UnityEngine;

namespace GemmaStage.Core
{
    public class SpawnPoint : MonoBehaviour
    {
        void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(transform.position, 0.1f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
        }
    }
}
