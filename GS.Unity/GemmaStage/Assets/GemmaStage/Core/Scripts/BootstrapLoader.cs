using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemmaStage.Core
{
    // Tiny startup loader: bring up Shared first, then either Lobby or the
    // editor shortcut environment, and finally unload Bootstrap itself.
    public class BootstrapLoader : MonoBehaviour
    {
        [SerializeField] string sharedSceneName = "Shared";
        [SerializeField] string lobbySceneName = "Lobby";

        IEnumerator Start()
        {
            if (!SceneManager.GetSceneByName(sharedSceneName).isLoaded)
            {
                var op = SceneManager.LoadSceneAsync(sharedSceneName, LoadSceneMode.Additive);
                if (op == null)
                {
                    Debug.LogError($"[BootstrapLoader] Scene '{sharedSceneName}' not found in Build Settings.");
                    yield break;
                }
                yield return op;
            }

            var gameManager = GameManager.Instance ?? FindAnyObjectByType<GameManager>();
            if (gameManager == null || !gameManager.TryResolveEnvironmentManager())
                yield break;

#if UNITY_EDITOR
            if (gameManager.SkipLobbyToEnvironmentInEditor)
            {
                var envTask = gameManager.LoadEnvironment(gameManager.DefaultEnvironmentInEditor);
                yield return new WaitUntil(() => envTask.IsCompleted);
                if (envTask.IsFaulted)
                    Debug.LogException(envTask.Exception?.GetBaseException(), this);

                yield return UnloadBootstrapScene();
                yield break;
            }
#endif

            if (!SceneManager.GetSceneByName(lobbySceneName).isLoaded)
            {
                var op = SceneManager.LoadSceneAsync(lobbySceneName, LoadSceneMode.Additive);
                if (op == null)
                {
                    Debug.LogError($"[BootstrapLoader] Scene '{lobbySceneName}' not found in Build Settings.");
                    yield break;
                }
                yield return op;
            }

            var lobbyScene = SceneManager.GetSceneByName(lobbySceneName);
            if (lobbyScene.IsValid() && lobbyScene.isLoaded)
                SceneManager.SetActiveScene(lobbyScene);

            yield return UnloadBootstrapScene();
        }

        IEnumerator UnloadBootstrapScene()
        {
            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                yield break;

            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null)
                yield return op;
        }
    }
}
