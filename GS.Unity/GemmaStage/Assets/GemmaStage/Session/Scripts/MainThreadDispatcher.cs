using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace GemmaStage.Session
{
    // Pumps queued Actions on Unity's main thread. SessionLayer's worker thread
    // calls Enqueue to marshal native-session events (OpenConcernsUpdated,
    // ClarificationCompleted, evaluation stage completions, SessionFailed)
    // back onto the main thread before invoking the public Unity event surface.
    //
    // Lives on the same GameObject as SessionLayer in Shared.unity. The static
    // Instance pointer is set in Awake so worker code can post without holding
    // a MonoBehaviour reference.
    [DisallowMultipleComponent]
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        static MainThreadDispatcher s_instance;

        readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Debug.LogWarning("[MainThreadDispatcher] Duplicate instance — destroying the extra one.");
                Destroy(this);
                return;
            }
            s_instance = this;
        }

        void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        void Update()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex)
                {
                    // Never break the pump on a bad subscriber — a thrown event
                    // handler would otherwise silently strand every later action.
                    Debug.LogException(ex);
                }
            }
        }

        // Post work to the Unity main thread. Safe to call from any thread.
        // Returns false if the dispatcher hasn't woken yet (action dropped) so
        // callers can decide whether to fall back to direct invocation; in
        // practice SessionLayer guarantees the dispatcher is alive before the
        // worker thread starts emitting events.
        public static bool Enqueue(Action action)
        {
            if (action == null) return false;
            var dispatcher = s_instance;
            if (dispatcher == null) return false;
            dispatcher._queue.Enqueue(action);
            return true;
        }
    }
}
