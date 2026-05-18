using GemmaStage.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemmaStage.Audience
{
    // Thin shim over the Rocketbox-backed audience: tracks the active env's SeatLayout
    // so LiveQaController has a stable RandomSeatIndex() / PlayReaction() API.
    // PlayReaction(seatIndex, HandRaise) walks seat -> spawned avatar child ->
    // IAudienceReactor and calls PlayHandRaise() on it. Other reactions are not
    // animated yet.
    public class AudienceManager : MonoBehaviour
    {
        EnvironmentManager environmentManager;
        SeatLayout activeSeatLayout;
        int activeSeatCount;

        void OnEnable()
        {
            environmentManager = FindAnyObjectByType<EnvironmentManager>();
            if (environmentManager == null)
            {
                Debug.LogWarning("[AudienceManager] No EnvironmentManager found in loaded scenes.");
                return;
            }

            environmentManager.OnEnvironmentLoaded += HandleEnvironmentLoaded;
            environmentManager.OnEnvironmentUnloading += HandleEnvironmentUnloading;
        }

        void OnDisable()
        {
            if (environmentManager == null) return;
            environmentManager.OnEnvironmentLoaded -= HandleEnvironmentLoaded;
            environmentManager.OnEnvironmentUnloading -= HandleEnvironmentUnloading;
        }

        void HandleEnvironmentLoaded(EnvironmentId env, Scene scene)
        {
            activeSeatLayout = FindSeatLayoutInScene(scene);
            activeSeatCount = activeSeatLayout != null ? activeSeatLayout.Seats.Count : 0;
            if (activeSeatLayout == null)
                Debug.LogWarning($"[AudienceManager] No SeatLayout found in '{scene.name}'; RandomSeatIndex will return -1.");
        }

        void HandleEnvironmentUnloading(EnvironmentId env)
        {
            activeSeatLayout = null;
            activeSeatCount = 0;
        }

        public void PlayReaction(int seatIndex, AudienceReaction reaction)
        {
            if (reaction != AudienceReaction.HandRaise) return; // only HandRaise is wired
            var reactor = GetReactorAtSeat(seatIndex);
            if (reactor != null) reactor.PlayHandRaise();
        }

        public void ResetReaction(int seatIndex) { }

        public void SetReactionLocked(int seatIndex, bool locked) { }

        public int RandomSeatIndex()
        {
            if (activeSeatCount <= 0) return -1;
            return Random.Range(0, activeSeatCount);
        }

        public Transform GetSeatTransform(int seatIndex)
        {
            if (activeSeatLayout == null) return null;
            var seats = activeSeatLayout.Seats;
            if (seatIndex < 0 || seatIndex >= seats.Count) return null;
            return seats[seatIndex];
        }

        public void ClearAllReactions() { }

        IAudienceReactor GetReactorAtSeat(int seatIndex)
        {
            if (activeSeatLayout == null) return null;
            var seats = activeSeatLayout.Seats;
            if (seatIndex < 0 || seatIndex >= seats.Count) return null;
            var seat = seats[seatIndex];
            if (seat == null) return null;
            // The NPC avatar is spawned as a child of the seat transform; its
            // reactor component (e.g. AudienceAnimationController) sits on the avatar root.
            return seat.GetComponentInChildren<IAudienceReactor>(includeInactive: false);
        }

        static SeatLayout FindSeatLayoutInScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var sl = root.GetComponentInChildren<SeatLayout>(includeInactive: true);
                if (sl != null) return sl;
            }
            return null;
        }
    }
}
