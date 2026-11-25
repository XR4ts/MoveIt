using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MoveIt.AR
{
    /// <summary>
    /// Remembers what the room looked like from a few viewpoints, so an erased object can be filled with real
    /// pixels of what's behind it instead of guesses.
    /// </summary>
    /// <remarks>
    /// While the user scans or erases, it keeps up to <see cref="maxSnapshots"/> snapshots taken from viewpoints
    /// that differ enough from each other. Each holds the real camera image (no virtual content), ARCore's depth
    /// image, and the camera's view-projection matrix. The erase fill shader (Erase.hlsl) projects each point of
    /// the floor or wall behind the object into every snapshot, uses the depth to check that the point was actually
    /// visible from there (not hidden by the object), and copies the pixel from the best such view.
    /// Everything is exposed as global shader properties named _Memory*.
    /// </remarks>
    public class BackgroundMemory : MonoBehaviour
    {
        /// <summary>
        /// Must match MEMORY_SNAPSHOTS in Erase.hlsl.
        /// </summary>
        public const int maxSnapshots = 8;

        const int k_ColorWidth = 480;
        const float k_MinInterval = 0.3f;
        const float k_MinMove = 0.3f;
        const float k_MinTurn = 20f;
        // While the camera moves fast, the depth image lags the camera image slightly and they don't line up.
        const float k_MaxTurnSpeed = 30f;
        const float k_MaxMoveSpeed = 0.5f;

        static readonly int s_Colors = Shader.PropertyToID("_MemoryColors");
        static readonly int s_Depths = Shader.PropertyToID("_MemoryDepths");
        static readonly int s_ViewProj = Shader.PropertyToID("_MemoryViewProj");
        static readonly int s_Display = Shader.PropertyToID("_MemoryDisplay");
        static readonly int s_Positions = Shader.PropertyToID("_MemoryPositions");
        static readonly int s_Count = Shader.PropertyToID("_MemoryCount");

        readonly Matrix4x4[] m_ViewProj = new Matrix4x4[maxSnapshots];
        readonly Matrix4x4[] m_Display = new Matrix4x4[maxSnapshots];
        readonly Vector4[] m_Positions = new Vector4[maxSnapshots];
        readonly Pose[] m_Poses = new Pose[maxSnapshots];

        Camera m_Camera;
        ARCameraBackground m_Background;
        DepthObjectFitter m_Depth;
        RenderTexture m_Colors;
        RenderTexture m_Depths;
        int m_Count;
        float m_LastCapture;
        Pose m_PreviousPose;
        bool m_HasPreviousPose;

        /// <summary>
        /// Whether snapshots are being taken: while the user scans the room or erases something, which is when they
        /// look at the room from different sides.
        /// </summary>
        public bool capturing { get; private set; }

        SurfaceDots m_Scanner;
        ObjectEraser m_Eraser;

        void Update()
        {
            if (m_Scanner == null)
                m_Scanner = FindAnyObjectByType<SurfaceDots>();
            if (m_Eraser == null)
                m_Eraser = FindAnyObjectByType<ObjectEraser>();

            // Also while erased objects are shown: every new viewpoint the user walks to can reveal more of what's
            // behind them, so the fill gets better as they look around.
            capturing = (m_Scanner != null && m_Scanner.scanning) ||
                (m_Eraser != null && (m_Eraser.isEditing || m_Eraser.hasErasedObjects));
        }

        /// <summary>
        /// Number of snapshots taken so far (at most <see cref="maxSnapshots"/>).
        /// </summary>
        public int count => m_Count;

        void Start()
        {
            m_Camera = Camera.main;
            m_Background = FindAnyObjectByType<ARCameraBackground>();
            m_Depth = FindAnyObjectByType<DepthObjectFitter>();

            var height = Mathf.RoundToInt(k_ColorWidth * (float)m_Camera.pixelHeight / m_Camera.pixelWidth);
            m_Colors = CreateArray(k_ColorWidth, height, RenderTextureFormat.ARGB32);
            Publish();
        }

        void OnEnable()
        {
            // After the depth fitter's handler, so this frame's depth texture is already there.
            Application.onBeforeRender += OnBeforeRender;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        void OnDestroy()
        {
            if (m_Colors != null)
                m_Colors.Release();
            if (m_Depths != null)
                m_Depths.Release();
        }

        /// <summary>
        /// Forgets all snapshots, e.g. when the room is scanned again.
        /// </summary>
        public void Clear()
        {
            m_Count = 0;
            Publish();
        }

        void OnBeforeRender()
        {
            if (m_Depth == null)
                m_Depth = FindAnyObjectByType<DepthObjectFitter>();

            var depthNeeded = capturing;
            if (m_Depth != null)
                m_Depth.captureForMemory = depthNeeded;

            if (!capturing || m_Camera == null || m_Background == null || m_Depth == null || m_Depth.depthTexture == null)
                return;

            if (Time.time - m_LastCapture < k_MinInterval)
                return;

            var pose = new Pose(m_Camera.transform.position, m_Camera.transform.rotation);
            var steady = IsSteady(pose);
            m_PreviousPose = pose;
            m_HasPreviousPose = true;
            if (!steady || !IsNewViewpoint(pose))
                return;

            Capture(pose);
        }

        bool IsSteady(Pose pose)
        {
            if (!m_HasPreviousPose || Time.deltaTime <= 0f)
                return false;

            var turnSpeed = Quaternion.Angle(m_PreviousPose.rotation, pose.rotation) / Time.deltaTime;
            var moveSpeed = Vector3.Distance(m_PreviousPose.position, pose.position) / Time.deltaTime;
            return turnSpeed <= k_MaxTurnSpeed && moveSpeed <= k_MaxMoveSpeed;
        }

        // A snapshot is only worth keeping if it sees the room from somewhere else than the ones already kept.
        bool IsNewViewpoint(Pose pose)
        {
            for (var i = 0; i < m_Count; i++)
            {
                var moved = Vector3.Distance(m_Poses[i].position, pose.position);
                var turned = Quaternion.Angle(m_Poses[i].rotation, pose.rotation);
                if (moved < k_MinMove && turned < k_MinTurn)
                    return false;
            }

            return true;
        }

        void Capture(Pose pose)
        {
            var depthTexture = m_Depth.depthTexture;
            if (m_Depths == null || m_Depths.width != depthTexture.width || m_Depths.height != depthTexture.height)
            {
                // First snapshot, or the depth resolution changed: older snapshots don't match any more.
                if (m_Depths != null)
                    m_Depths.Release();

                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
                    ? RenderTextureFormat.RFloat
                    : RenderTextureFormat.RHalf;
                m_Depths = CreateArray(depthTexture.width, depthTexture.height, format);
                m_Count = 0;
                }

            var slice = m_Count < maxSnapshots ? m_Count : MostRedundantSnapshot(pose);

            // The AR background material draws the camera image as it appears on screen, without virtual content.
            Graphics.Blit(null, m_Colors, m_Background.material, 0, slice);
            Graphics.Blit(depthTexture, m_Depths, 0, slice);

            m_ViewProj[slice] = m_Camera.projectionMatrix * m_Camera.worldToCameraMatrix;
            m_Display[slice] = m_Depth.displayMatrix;
            m_Positions[slice] = new Vector4(pose.position.x, pose.position.y, pose.position.z, m_Depth.depthScale);
            m_Poses[slice] = pose;

            m_Count = Mathf.Min(m_Count + 1, maxSnapshots);
            m_LastCapture = Time.time;
            Publish();
        }

        // When memory is full, the new snapshot replaces the one whose viewpoint is closest to it, so the kept
        // snapshots stay spread around the room instead of only covering the latest few seconds.
        int MostRedundantSnapshot(Pose pose)
        {
            var best = 0;
            var bestScore = float.MaxValue;
            for (var i = 0; i < m_Count; i++)
            {
                var score = Vector3.Distance(m_Poses[i].position, pose.position) +
                    Quaternion.Angle(m_Poses[i].rotation, pose.rotation) / 60f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best;
        }

        void Publish()
        {
            if (m_Colors != null)
                Shader.SetGlobalTexture(s_Colors, m_Colors);
            if (m_Depths != null)
                Shader.SetGlobalTexture(s_Depths, m_Depths);
            else if (m_Colors != null)
                Shader.SetGlobalTexture(s_Depths, m_Colors);

            Shader.SetGlobalMatrixArray(s_ViewProj, m_ViewProj);
            Shader.SetGlobalMatrixArray(s_Display, m_Display);
            Shader.SetGlobalVectorArray(s_Positions, m_Positions);
            Shader.SetGlobalFloat(s_Count, m_Depths != null ? m_Count : 0);
        }

        static RenderTexture CreateArray(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = maxSnapshots,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "Background Memory",
            };
            texture.Create();
            return texture;
        }
    }
}
