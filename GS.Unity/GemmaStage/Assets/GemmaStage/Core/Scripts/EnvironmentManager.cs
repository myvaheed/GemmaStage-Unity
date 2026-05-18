using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemmaStage.Core
{
    public class EnvironmentManager : MonoBehaviour
    {
        const string LobbySceneName = "Lobby";

        static readonly Dictionary<EnvironmentId, string> SceneNames = new()
        {
            { EnvironmentId.Stage, "Stage" },
            { EnvironmentId.Classroom, "Classroom" },
        };

        public event Action<EnvironmentId, Scene> OnEnvironmentLoaded;
        public event Action<EnvironmentId> OnEnvironmentUnloading;

        public EnvironmentId? CurrentEnvironment { get; private set; }

        public async Task LoadEnvironment(EnvironmentId env)
        {
            if (CurrentEnvironment.HasValue)
                await UnloadCurrentEnvironment();

            if (!SceneNames.TryGetValue(env, out var sceneName))
                throw new ArgumentException($"No scene mapping for environment {env}", nameof(env));

            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op == null)
                throw new InvalidOperationException(
                    $"Scene '{sceneName}' not found in Build Settings. Add it via File → Build Settings.");
            await op.AsTask();

            var scene = SceneManager.GetSceneByName(sceneName);
            SceneManager.SetActiveScene(scene);
            await UnloadLobbyIfLoaded();

            MoveXrRigToSpawnPoint(scene);

            CurrentEnvironment = env;
            OnEnvironmentLoaded?.Invoke(env, scene);
        }

        public async Task UnloadCurrentEnvironment()
        {
            if (!CurrentEnvironment.HasValue) return;

            var env = CurrentEnvironment.Value;
            OnEnvironmentUnloading?.Invoke(env);

            var sceneName = SceneNames[env];
            var op = SceneManager.UnloadSceneAsync(sceneName);
            if (op != null) await op.AsTask();

            CurrentEnvironment = null;
        }

        // Tears down the active environment and loads the Lobby scene additively,
        // then sets it active and moves the XR rig to the Lobby spawn point.
        // Called by SessionManager.CancelEvaluation to return the player to the
        // pre-session state (see PHASE_5_TASKS.md §5.11.4).
        public async Task ReturnToLobby()
        {
            if (CurrentEnvironment.HasValue)
                await UnloadCurrentEnvironment();

            var op = SceneManager.LoadSceneAsync(LobbySceneName, LoadSceneMode.Additive);
            if (op != null) await op.AsTask();

            var lobby = SceneManager.GetSceneByName(LobbySceneName);
            if (lobby.IsValid()) SceneManager.SetActiveScene(lobby);
            MoveXrRigToSpawnPoint(lobby);
        }

        static async Task UnloadLobbyIfLoaded()
        {
            var lobby = SceneManager.GetSceneByName(LobbySceneName);
            if (!lobby.IsValid() || !lobby.isLoaded)
                return;

            var op = SceneManager.UnloadSceneAsync(lobby);
            if (op != null)
                await op.AsTask();
        }

        static void MoveXrRigToSpawnPoint(Scene scene)
        {
            var spawn = FindSpawnPointInScene(scene);
            if (spawn == null) return;

            var origin = FindAnyObjectByType<XROrigin>();
            if (origin == null) return;

            origin.transform.SetPositionAndRotation(spawn.transform.position, spawn.transform.rotation);
        }

        static SpawnPoint FindSpawnPointInScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var sp = root.GetComponentInChildren<SpawnPoint>(true);
                if (sp != null) return sp;
            }
            return null;
        }
    }

    static class AsyncOperationExtensions
    {
        public static Task AsTask(this AsyncOperation op)
        {
            var tcs = new TaskCompletionSource<bool>();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
