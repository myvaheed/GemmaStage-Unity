using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Audience
{
    [DisallowMultipleComponent]
    public class SeatLayout : MonoBehaviour
    {
        [SerializeField] List<Transform> seats = new();

        public IReadOnlyList<Transform> Seats => seats;

        void OnValidate()
        {
            // Direct children only — matches the authored convention (Seat_00..Seat_19 as
            // children of the Seats parent). Recursive walks would pick up sub-rigs of any
            // future placeholder NPCs parented under a seat.
            seats.Clear();
            foreach (Transform child in transform)
                seats.Add(child);
        }
    }
}
