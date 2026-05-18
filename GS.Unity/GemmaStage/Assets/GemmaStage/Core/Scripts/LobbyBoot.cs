using Unity.XR.CoreUtils;
using UnityEngine;

namespace GemmaStage.Core
{
    public class LobbyBoot : MonoBehaviour
    {
        [SerializeField] Transform spawnPoint;

        void Start()
        {
            if (spawnPoint == null)
            {
                Debug.LogWarning("[LobbyBoot] spawnPoint not assigned; XR rig will not be repositioned.", this);
                return;
            }

            var origin = FindAnyObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogWarning("[LobbyBoot] XROrigin not found in loaded scenes; rig not repositioned.", this);
                return;
            }

            origin.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
        }
    }
}
