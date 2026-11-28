using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

namespace MoveIt.AR.Harness
{
    /// <summary>
    /// Automated visual test run in XR Simulation. Only active when Unity is started with <c>-moveitHarness</c>
    /// (see HarnessLauncher). It flies the simulated camera around a room to scan it, runs a scenario (erase an
    /// object, place furniture) and saves screenshots, so changes can be checked without a person at the editor.
    /// </summary>
    /// <remarks>
    /// Command line: <c>-moveitScenario name</c> (discover, erase), <c>-moveitTarget text</c> (part of the name of the
    /// environment object to erase). Screenshots go to the folder in the MOVEIT_HARNESS_OUT environment variable.
    /// </remarks>
    public class HarnessDriver : MonoBehaviour
    {
        const int k_Width = 720;
        const int k_Height = 1560;

        string m_OutputDir;
        string m_Scenario;
        string m_Target;
        bool m_QuickScan;
        bool m_SpawnStool;
        Camera m_Camera;
        RenderTexture m_RenderTarget;
        Transform m_PoseProvider;
        Scene m_EnvironmentScene;
        readonly StringBuilder m_Log = new StringBuilder();
        int m_ShotIndex;
        readonly System.Collections.Generic.List<Texture2D> m_Thumbnails = new System.Collections.Generic.List<Texture2D>();
        const int k_ThumbWidth = 240;
        const int k_ThumbHeight = 520;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("-moveitHarness"))
                return;

            var driver = new GameObject("Harness Driver").AddComponent<HarnessDriver>();
            driver.m_Scenario = Arg(args, "-moveitScenario") ?? "discover";
            driver.m_Target = Arg(args, "-moveitTarget") ?? "table";
            driver.m_QuickScan = Arg(args, "-moveitScan") == "quick";
            driver.m_SpawnStool = Array.IndexOf(args, "-moveitStool") >= 0;
            DontDestroyOnLoad(driver.gameObject);
        }

        static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // In batch mode there is no window, so Unity skips its normal "before render" step and only renders when
        // asked. AR Foundation's occlusion manager does its per-frame work in that step, so run it and render the
        // camera each frame, like a real frame on a phone or in the editor.
        static System.Reflection.MethodInfo s_InvokeBeforeRender;

        void LateUpdate()
        {
            if (!Application.isBatchMode || m_Camera == null)
                return;

            if (s_InvokeBeforeRender == null)
            {
                var helper = typeof(Application).Assembly.GetType("UnityEngine.BeforeRenderHelper");
                s_InvokeBeforeRender = helper?.GetMethod("Invoke",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (s_InvokeBeforeRender == null)
                    Log("BeforeRenderHelper.Invoke not found");
            }

            s_InvokeBeforeRender?.Invoke(null, null);
            m_Camera.Render();
        }

        IEnumerator Start()
        {
            m_OutputDir = Environment.GetEnvironmentVariable("MOVEIT_HARNESS_OUT");
            if (string.IsNullOrEmpty(m_OutputDir))
                m_OutputDir = Path.Combine(Application.dataPath, "..", "HarnessOutput");
            Directory.CreateDirectory(m_OutputDir);

            Log($"scenario {m_Scenario}, target '{m_Target}', output {m_OutputDir}");

            yield return Setup();

            try
            {
                Log($"session state {ARSession.state}");
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }

            IEnumerator scenario = m_Scenario switch
            {
                "erase" => EraseScenario(),
                _ => DiscoverScenario(),
            };

            yield return RunSafely(scenario);

            WriteContactSheet();
            Log("done");
            File.WriteAllText(Path.Combine(m_OutputDir, "log.txt"), m_Log.ToString());
            Quit(0);
        }

        // Runs a coroutine, logging any exception instead of hanging the run.
        IEnumerator RunSafely(IEnumerator routine)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!routine.MoveNext())
                        yield break;
                    current = routine.Current;
                }
                catch (Exception e)
                {
                    Log("EXCEPTION " + e);
                    yield break;
                }

                yield return current;
            }
        }

        IEnumerator Setup()
        {
            // Let XR start and the simulation environment load.
            var timeout = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < timeout && (FindPoseProvider() == null || ARSession.state < ARSessionState.SessionTracking))
                yield return null;

            m_Camera = Camera.main;
            m_PoseProvider = FindPoseProvider();
            Log($"camera {(m_Camera ? m_Camera.name : "none")}, pose provider {(m_PoseProvider ? m_PoseProvider.name : "none")}, session {ARSession.state}");

            // Render the AR camera, with the UI, into a phone-shaped texture.
            m_RenderTarget = new RenderTexture(k_Width, k_Height, 24, RenderTextureFormat.ARGB32);
            m_Camera.targetTexture = m_RenderTarget;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas.isRootCanvas && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = m_Camera;
                    canvas.planeDistance = 0.5f;
                }
            }

            // Rebuild text for the new render mode.
            Canvas.ForceUpdateCanvases();
            foreach (var text in FindObjectsByType<TMPro.TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                text.SetAllDirty();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name.StartsWith("Simulated Environment"))
                    m_EnvironmentScene = scene;
            }

            yield return null;
        }

        static Transform FindPoseProvider()
        {
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour.GetType().Name == "SimulationCameraPoseProvider")
                    return behaviour.transform;
            }

            return null;
        }

        // ----- Scenarios -----

        IEnumerator DiscoverScenario()
        {
            DumpEnvironment();
            Log($"start pose {m_PoseProvider.position} {m_PoseProvider.eulerAngles}, app camera {m_Camera.transform.position}");

            yield return Scan();

            for (var yaw = 0; yaw < 360; yaw += 90)
            {
                SetPose(RoomCenter() + Vector3.up * 1.4f, yaw, 25f);
                yield return Frames(10);
                yield return Shot($"view-{yaw}");
            }
        }

        IEnumerator EraseScenario()
        {
            if (m_SpawnStool)
                SpawnTestStool();

            DumpEnvironment();
            yield return Scan();

            var surfaceDots = FindAnyObjectByType<SurfaceDots>();
            if (surfaceDots != null)
                surfaceDots.SetScanning(false);

            if (!TryFindTarget(out var target))
            {
                Log($"no environment object matching '{m_Target}'");
                yield break;
            }

            Log($"target {target.name} bounds {target.bounds.center} size {target.bounds.size}");

            // Look at the object from the room side, at standing height. For the test stool, aim at its middle.
            var center = target.bounds.center;
            if (target.name.StartsWith("TestStool"))
                center = target.transform.parent.position + Vector3.up * 0.3f;
            var fromRoom = RoomCenter() - center;
            fromRoom.y = 0f;
            fromRoom = fromRoom.sqrMagnitude > 0.01f ? fromRoom.normalized : Vector3.back;
            var viewDistance = 2.3f;
            if (target.name.StartsWith("TestStool"))
            {
                // Free-standing in the middle of the room: look from the open side.
                fromRoom = Vector3.forward;
                viewDistance = 1.6f;
            }

            var eye = center + fromRoom * viewDistance;
            eye.y = 1.45f;
            LookFrom(eye, center);

            // Small sideways moves so camera depth has parallax, as a user would.
            for (var i = 0; i < 30; i++)
            {
                LookFrom(eye + Vector3.Cross(Vector3.up, fromRoom) * (0.15f * Mathf.Sin(i * 0.3f)), center);
                yield return null;
            }

            LookFrom(eye, center);
            yield return Frames(10);
            yield return Shot("erase-1-before");

            var eraser = FindAnyObjectByType<ObjectEraser>();
            var screen = m_Camera.WorldToScreenPoint(center);
            Log($"tap at {screen}");
            eraser.EraseAt(screen);

            var wait = Time.realtimeSinceStartup + 5f;
            while (eraser.isMeasuring && Time.realtimeSinceStartup < wait)
                yield return null;

            Log($"adjusting {eraser.isAdjusting}, box {eraser.boxDescription}");
            yield return Frames(5);
            yield return Shot("erase-2-fitted");

            // While adjusting, the real object stays visible; the clean (inpainted) fill is computed in the background.
            var previewWait = Time.realtimeSinceStartup + 60f;
            var previewStart = Time.realtimeSinceStartup;
            while (eraser.isInpainting && Time.realtimeSinceStartup < previewWait)
                yield return null;
            Log($"preview took {Time.realtimeSinceStartup - previewStart:0.0} s");
            yield return Frames(5);
            yield return Shot("erase-2-preview");

            eraser.Confirm();
            yield return Frames(5);
            yield return Shot("erase-3-done");

            // The clean (inpainted) fill takes a few seconds on a background thread.
            var inpaintWait = Time.realtimeSinceStartup + 60f;
            var inpaintStart = Time.realtimeSinceStartup;
            while (eraser.isInpainting && Time.realtimeSinceStartup < inpaintWait)
                yield return null;
            Log($"inpainting took {Time.realtimeSinceStartup - inpaintStart:0.0} s, still busy {eraser.isInpainting}");
            yield return Frames(5);
            yield return Shot("erase-3-inpainted");

            var memory = FindAnyObjectByType<BackgroundMemory>();
            Log($"snapshots {(memory ? memory.count : -1)}");

            // Other angles: the fill must hold up as the user walks around, including from above.
            var views = new[]
            {
                (angle: -55f, height: 1.45f, radius: 2.0f),
                (angle: -25f, height: 1.45f, radius: 2.3f),
                (angle: 25f, height: 1.45f, radius: 2.3f),
                (angle: 55f, height: 1.45f, radius: 2.0f),
                (angle: 0f, height: 2.2f, radius: 1.5f),
                (angle: 40f, height: 2.0f, radius: 1.6f),
            };
            var room = EnvironmentBounds();
            foreach (var view in views)
            {
                var orbit = Quaternion.Euler(0f, view.angle, 0f) * (fromRoom * view.radius);
                var orbitEye = center + orbit;
                orbitEye.x = Mathf.Clamp(orbitEye.x, room.min.x + 0.2f, room.max.x - 0.2f);
                orbitEye.z = Mathf.Clamp(orbitEye.z, room.min.z + 0.2f, room.max.z - 0.2f);
                orbitEye.y = view.height;
                LookFrom(orbitEye, center);
                yield return Frames(10);
                yield return Shot($"erase-4-angle{view.angle:+0;-0}-h{view.height:0.0}");

                // The same view without the erase, to compare against.
                eraser.SetShowOriginal(true);
                yield return Frames(5);
                yield return Shot($"erase-4-angle{view.angle:+0;-0}-h{view.height:0.0}-original");
                eraser.SetShowOriginal(false);
            }

            // Close up: right next to where the object was, then standing inside it.
            var near = center + fromRoom * (0.5f * Mathf.Max(target.bounds.size.x, target.bounds.size.z) + 0.4f);
            near.y = 1.3f;
            LookFrom(near, center - Vector3.up * 0.3f);
            yield return Frames(10);
            yield return Shot("erase-6-close");

            var inside = center;
            inside.y = 1.2f;
            LookFrom(inside, inside + fromRoom + Vector3.down * 0.3f);
            yield return Frames(10);
            yield return Shot("erase-7-inside");

            LookFrom(eye, center);
            eraser.SetShowOriginal(true);
            yield return Frames(5);
            yield return Shot("erase-5-original");
            eraser.SetShowOriginal(false);
        }

        // Turns around slowly in the room, looking down and level, so floor and walls get detected. XR Simulation
        // discovers surfaces over real time, like a phone, so this is paced in seconds, not frames.
        IEnumerator Scan()
        {
            // A quick scan is one slow turn from one spot, like a user in a hurry; the full scan covers more viewpoints.
            var spots = m_QuickScan
                ? new[] { RoomCenter() + new Vector3(0.6f, 0f, 0.6f) }
                : new[] { RoomCenter(), RoomCenter() + new Vector3(0.6f, 0f, 0.6f) };
            var pitches = m_QuickScan ? new[] { 30f } : new[] { 40f, 15f };
            const float degreesPerSecond = 25f;
            foreach (var spot in spots)
            {
                var eye = spot + Vector3.up * 1.4f;
                foreach (var pitch in pitches)
                {
                    var start = Time.realtimeSinceStartup;
                    float yaw;
                    do
                    {
                        yaw = (Time.realtimeSinceStartup - start) * degreesPerSecond;
                        SetPose(eye, yaw, pitch);
                        yield return null;
                    }
                    while (yaw < 360f);
                }
            }

            var planes = FindAnyObjectByType<ARPlaneManager>();
            var walls = new System.Collections.Generic.List<Wall>();
            RoomWalls.GetAll(walls);
            Log($"scanned, planes {(planes ? planes.trackables.count : -1)}, walls {walls.Count}, floor {(RoomFloor.TryGetY(out var y) ? y.ToString("0.00") : "none")}");
        }

        // A step stool standing free in the room: seat, step and four thin, slightly splayed legs. Thin legs are
        // the hard case for the depth fit, and with no wall behind it the fill has to rebuild the floor.
        void SpawnTestStool()
        {
            var reference = EnvironmentRenderers().FirstOrDefault(r => r.name.StartsWith("Chest"));
            if (reference == null)
            {
                Log("no reference renderer for the test stool");
                return;
            }

            var root = new GameObject("TestStool");
            SceneManager.MoveGameObjectToScene(root, m_EnvironmentScene);
            root.layer = reference.gameObject.layer;
            root.transform.position = RoomCenter() + new Vector3(0.3f, 0f, 0.1f);
            root.transform.rotation = Quaternion.Euler(0f, 20f, 0f);

            void Part(string partName, Vector3 position, Vector3 size, Vector3 euler)
            {
                var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
                part.name = "TestStool " + partName;
                part.layer = root.layer;
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = position;
                part.transform.localRotation = Quaternion.Euler(euler);
                part.transform.localScale = size;
                part.GetComponent<Renderer>().sharedMaterial = reference.sharedMaterial;
            }

            Part("Seat", new Vector3(0f, 0.5f, 0f), new Vector3(0.43f, 0.03f, 0.3f), Vector3.zero);
            Part("Step", new Vector3(0f, 0.24f, 0.22f), new Vector3(0.43f, 0.03f, 0.18f), Vector3.zero);
            foreach (var x in new[] { -0.19f, 0.19f })
            {
                foreach (var z in new[] { -0.12f, 0.12f })
                {
                    // Splay outwards towards the floor, like a real step stool.
                    Part($"Leg {x} {z}", new Vector3(x * 1.08f, 0.25f, z * 1.15f), new Vector3(0.035f, 0.5f, 0.035f),
                        new Vector3(z > 0 ? 4f : -4f, 0f, x > 0 ? -4f : 4f));
                }
            }

            Log($"spawned test stool at {root.transform.position}");
        }

        // ----- Helpers -----

        void SetPose(Vector3 position, float yaw, float pitch)
        {
            m_PoseProvider.SetPositionAndRotation(position, Quaternion.Euler(pitch, yaw, 0f));
        }

        void LookFrom(Vector3 eye, Vector3 target)
        {
            m_PoseProvider.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
        }

        // Middle of the room at floor level (environments have their floor at y = 0).
        Vector3 RoomCenter()
        {
            var center = EnvironmentBounds().center;
            center.y = 0f;
            return center;
        }

        Bounds EnvironmentBounds()
        {
            var bounds = new Bounds();
            var first = true;
            foreach (var renderer in EnvironmentRenderers())
            {
                if (first)
                    bounds = renderer.bounds;
                else
                    bounds.Encapsulate(renderer.bounds);
                first = false;
            }

            return bounds;
        }

        Renderer[] EnvironmentRenderers()
        {
            if (!m_EnvironmentScene.IsValid())
                return Array.Empty<Renderer>();

            return m_EnvironmentScene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Renderer>()).ToArray();
        }

        bool TryFindTarget(out Renderer target)
        {
            target = EnvironmentRenderers()
                .Where(r => r.name.IndexOf(m_Target, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(r => r.bounds.size.sqrMagnitude)
                .FirstOrDefault();
            return target != null;
        }

        void DumpEnvironment()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"environment scene '{m_EnvironmentScene.name}', bounds {EnvironmentBounds()}");
            foreach (var renderer in EnvironmentRenderers().OrderBy(r => r.name))
                sb.AppendLine($"{renderer.name}\tcenter {renderer.bounds.center}\tsize {renderer.bounds.size}");
            File.WriteAllText(Path.Combine(m_OutputDir, "environment.txt"), sb.ToString());
        }

        static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; i++)
                yield return null;
        }

        IEnumerator Shot(string shotName)
        {
            yield return null;
            m_Camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = m_RenderTarget;
            var texture = new Texture2D(k_Width, k_Height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, k_Width, k_Height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;

            var file = Path.Combine(m_OutputDir, $"{++m_ShotIndex:00}-{shotName}.png");
            File.WriteAllBytes(file, texture.EncodeToPNG());
            Destroy(texture);

            // Small copy for the contact sheet.
            var small = RenderTexture.GetTemporary(k_ThumbWidth, k_ThumbHeight, 0);
            Graphics.Blit(m_RenderTarget, small);
            RenderTexture.active = small;
            var thumb = new Texture2D(k_ThumbWidth, k_ThumbHeight, TextureFormat.RGB24, false);
            thumb.ReadPixels(new Rect(0, 0, k_ThumbWidth, k_ThumbHeight), 0, 0);
            thumb.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(small);
            m_Thumbnails.Add(thumb);
            Log($"shot {file}");
        }

        // All shots side by side in one image, in order.
        void WriteContactSheet()
        {
            if (m_Thumbnails.Count == 0)
                return;

            var sheet = new Texture2D(k_ThumbWidth * m_Thumbnails.Count, k_ThumbHeight, TextureFormat.RGB24, false);
            for (var i = 0; i < m_Thumbnails.Count; i++)
                sheet.SetPixels(i * k_ThumbWidth, 0, k_ThumbWidth, k_ThumbHeight, m_Thumbnails[i].GetPixels());
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(m_OutputDir, "sheet.png"), sheet.EncodeToPNG());
        }

        void Log(string message)
        {
            m_Log.AppendLine(message);
            Debug.Log("[Harness] " + message);
        }

        static void Quit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }
}
