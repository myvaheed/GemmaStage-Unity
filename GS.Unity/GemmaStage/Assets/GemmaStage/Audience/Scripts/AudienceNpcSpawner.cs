using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GemmaStage.Audience
{
    public class AudienceNpcSpawner : MonoBehaviour
    {
        [Serializable]
        public struct AvatarEntry
        {
            public GameObject prefab;
            public Avatar avatar;
        }

        [SerializeField] List<AvatarEntry> avatarPool = new();
        [SerializeField] RuntimeAnimatorController animatorController;
        [SerializeField] SeatLayout seatLayout;
        [SerializeField] bool spawnOnStart = true;
        [SerializeField, Min(0)] int minNpcs = 25;
        [SerializeField, Min(0)] int maxNpcs = 30;
        [SerializeField] Vector3 spawnEulerOffset = new Vector3(0f, 0f, 0f);
        [SerializeField] Vector3 spawnScale = Vector3.one;

        // Sitting-pose hips sit well above the avatar root; drop Y so they land on the chair, not above it.
        const float SeatYOffset = 0.69f;

        readonly List<GameObject> _spawned = new();
        readonly UniTaskCompletionSource _completion = new();

        public bool IsSpawnComplete { get; private set; }

        void Start()
        {
            if (spawnOnStart) SpawnAll();
        }

        public void SpawnAll()
        {
            if (IsSpawnComplete) return;

            if (seatLayout == null) seatLayout = GetComponentInChildren<SeatLayout>(includeInactive: true);
            if (seatLayout == null) seatLayout = FindAnyObjectByType<SeatLayout>(FindObjectsInactive.Include);
            if (seatLayout == null)
            {
                Debug.LogWarning("[AudienceNpcSpawner] No SeatLayout found in scene; nothing to populate.");
                MarkComplete();
                return;
            }
            if (avatarPool == null || avatarPool.Count == 0)
            {
                Debug.LogWarning("[AudienceNpcSpawner] avatarPool is empty; no NPCs will spawn.");
                MarkComplete();
                return;
            }
            if (animatorController == null)
                Debug.LogWarning("[AudienceNpcSpawner] animatorController is null; NPCs will spawn without animation.");

            var seats = seatLayout.Seats;

            int lo = Mathf.Min(minNpcs, maxNpcs);
            int hi = Mathf.Max(minNpcs, maxNpcs);
            int target = UnityEngine.Random.Range(lo, hi + 1);
            target = Mathf.Min(target, seats.Count, avatarPool.Count);

            var seatOrder = BuildShuffledRange(seats.Count);
            var avatarOrder = BuildShuffledRange(avatarPool.Count);

            int placed = 0;
            for (int i = 0; i < target; i++)
            {
                var seat = seats[seatOrder[i]];
                if (seat == null) continue;

                var entry = avatarPool[avatarOrder[i]];
                if (entry.prefab == null) continue;

                var spawnPos = seat.position;
                spawnPos.y -= SeatYOffset;
                var npc = Instantiate(entry.prefab, spawnPos, seat.rotation, seat);
                npc.name = $"AudienceNpc_{seatOrder[i]:D2}";
                npc.transform.localRotation = Quaternion.Euler(spawnEulerOffset) * npc.transform.localRotation;
                npc.transform.localScale = spawnScale;

                var animator = npc.GetComponent<Animator>();
                if (animator == null) animator = npc.AddComponent<Animator>();
                animator.runtimeAnimatorController = animatorController;
                animator.avatar = entry.avatar;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullCompletely;

                foreach (var smr in npc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    smr.receiveShadows = false;
                    smr.skinnedMotionVectors = false;
                    smr.updateWhenOffscreen = false;
                }
                foreach (var mr in npc.GetComponentsInChildren<MeshRenderer>(true))
                {
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                npc.AddComponent<AudienceAnimationController>();

                _spawned.Add(npc);
                placed++;
            }

            Debug.Log($"[AudienceNpcSpawner] Placed {placed}/{seats.Count} audience NPCs (target {target}).");
            MarkComplete();
        }

        void MarkComplete()
        {
            IsSpawnComplete = true;
            _completion.TrySetResult();
        }

        public UniTask WaitUntilSpawnCompletedAsync(CancellationToken ct)
        {
            if (IsSpawnComplete) return UniTask.CompletedTask;
            return _completion.Task.AttachExternalCancellation(ct);
        }

        static List<int> BuildShuffledRange(int count)
        {
            var list = new List<int>(count);
            for (int i = 0; i < count; i++) list.Add(i);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }
    }
}
