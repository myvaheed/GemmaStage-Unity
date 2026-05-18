using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace GemmaStage.Core
{
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

#if UNITY_EDITOR
        [Header("Editor-only dev shortcut")]
        [Tooltip("If enabled, the chosen environment is loaded instead of staying in the Lobby. Editor-only — ignored in builds.")]
        [SerializeField] bool skipLobbyToEnvironment = false;
        [SerializeField] EnvironmentId defaultEnvironment = EnvironmentId.Stage;
#endif

        public EnvironmentManager Environment { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            TryResolveEnvironmentManager();
        }

        IEnumerator Start()
        {
            if (Instance != this) yield break;
            if (!TryResolveEnvironmentManager())
            {
                yield break;
            }
        }

        public Task LoadEnvironment(EnvironmentId env)
        {
            if (Environment == null) return Task.CompletedTask;
            return Environment.LoadEnvironment(env);
        }

        public Task UnloadEnvironment()
        {
            if (Environment == null) return Task.CompletedTask;
            return Environment.UnloadCurrentEnvironment();
        }

        public Task ReturnToLobby()
        {
            if (Environment == null) return Task.CompletedTask;
            return Environment.ReturnToLobby();
        }

        internal bool TryResolveEnvironmentManager()
        {
            if (Environment != null)
                return true;

            Environment = FindAnyObjectByType<EnvironmentManager>();
            if (Environment == null)
            {
                Debug.LogError("[GameManager] EnvironmentManager not found in Shared scene.");
                return false;
            }

            return true;
        }

#if UNITY_EDITOR
        internal bool SkipLobbyToEnvironmentInEditor => skipLobbyToEnvironment;
        internal EnvironmentId DefaultEnvironmentInEditor => defaultEnvironment;
#endif
    }
}
