using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MoveIt.AR.Harness.Editor
{
    /// <summary>
    /// Starts an automated harness run (see HarnessDriver) from the command line:
    /// <c>Unity -batchmode -projectPath ... -executeMethod MoveIt.AR.Harness.Editor.HarnessLauncher.Run -moveitHarness
    /// -moveitScenario erase -logFile ...</c> (no <c>-quit</c>; the driver exits when it's done).
    /// </summary>
    [InitializeOnLoad]
    public static class HarnessLauncher
    {
        const string k_StartKey = "MoveItHarnessStart";
        const double k_TimeoutSeconds = 600;

        // Runs after every domain reload, including the one when entering Play mode, to keep the watchdog alive.
        static HarnessLauncher()
        {
            if (Environment.GetCommandLineArgs().Contains("-moveitHarness"))
                EditorApplication.update += Watchdog;
        }

        public static void Run()
        {
            SessionState.SetFloat(k_StartKey, (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            EditorApplication.EnterPlaymode();
        }

        static void Watchdog()
        {
            var start = SessionState.GetFloat(k_StartKey, -1f);
            if (start >= 0f && EditorApplication.timeSinceStartup - start > k_TimeoutSeconds)
            {
                Debug.LogError("[Harness] timed out");
                EditorApplication.Exit(2);
            }
        }
    }
}
