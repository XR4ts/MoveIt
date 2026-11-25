using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MoveIt.AR
{
    /// <summary>
    /// Finds the real object under a screen tap and fits an upright box around it, using the environment depth
    /// image ARCore makes for occlusion (from camera motion on phones without a depth sensor; XR Simulation
    /// provides one too).
    /// </summary>
    /// <remarks>
    /// 1. The depth image is read back from the GPU and turned into a grid of 3D points covering the screen.
    /// 2. Points on the floor or on a detected wall are removed; what's left belongs to objects.
    /// 3. Starting at the tapped point, neighbouring points are collected (region growing) while they are close in
    ///    3D and the surface between them bends outwards (convex). An object's own edges are convex; the seam where
    ///    it touches another object or a wall is concave, so growing stops there and touching objects stay apart.
    /// 4. An upright box is fitted: squared to the wall behind it if there is one, otherwise turned to the tightest
    ///    footprint. The camera only sees the front of the object, so the box is extended back to that wall, or by
    ///    a guess. Things standing on top of the object (a lamp on a chest) are then included in its height.
    /// </remarks>
    public class DepthObjectFitter : MonoBehaviour
    {
        /// <summary>
        /// A fitted box: centre of its front edge on the floor, yaw in degrees (forward points away from the
        /// user) and size (width, height, depth) in metres.
        /// </summary>
        public struct Fit
        {
            public Vector3 front;
            public float yaw;
            public Vector3 size;
        }

        // Floor points within this horizontal distance of the object set its local floor height.
        const float k_LocalFloorRadius = 0.8f;
        const int k_MinLocalFloorPoints = 20;

        // Screen sampling grid, columns across the screen; rows follow the aspect ratio.
        const int k_GridColumns = 90;
        const float k_MaxRange = 5f;
        const float k_FloorBand = 0.05f;
        const float k_WallBand = 0.07f;
        const float k_MaxHeightAboveFloor = 2.3f;
        const float k_NeighbourDistance = 0.045f;
        const float k_NeighbourDistancePerMetre = 0.025f;
        const float k_MaxObjectRadius = 2f;
        const int k_MinPoints = 12;
        const int k_SeedSearchRadius = 6;
        const float k_Padding = 0.08f;
        const float k_WallAlignDistance = 0.8f;
        const float k_ReachWallDistance = 0.8f;
        const float k_GuessedExtraDepth = 0.15f;
        // Keep the box this far from other objects next to it (the erase fill and its depth checks reach a few cm
        // past the box), so a bed right beside a stool isn't erased with it.
        const float k_NeighbourGap = 0.1f;
        const float k_MinDepth = 0.3f;
        // Undetected parts of a wall (low down, behind furniture) still belong to it, so walls count as infinite.
        const float k_WallLateralTolerance = 10f;
        // How much a seam may bend inwards and still count as part of the same object, for depth noise.
        const float k_ConcaveTolerance = 0.2f;
        const float k_SameSurfaceCos = 0.9f;

        AROcclusionManager m_Occlusion;
        ARCameraManager m_CameraManager;
        Camera m_Camera;
        Matrix4x4 m_DisplayMatrix = Matrix4x4.identity;
        Texture m_DepthTexture;
        bool m_Busy;

        readonly List<Wall> m_Walls = new List<Wall>();

        /// <summary>
        /// The active instance, for other components that need the depth image (the erase fill).
        /// </summary>
        public static DepthObjectFitter current { get; private set; }

        /// <summary>
        /// The latest environment depth texture, or null.
        /// </summary>
        public Texture depthTexture => m_DepthTexture;

        /// <summary>
        /// Maps screen UVs onto <see cref="depthTexture"/>; use as the AR background shader does.
        /// </summary>
        public Matrix4x4 displayMatrix => m_DisplayMatrix;

        /// <summary>
        /// Multiply sampled depth values by this to get metres.
        /// </summary>
        public float depthScale => m_DepthTexture != null && m_DepthTexture.graphicsFormat == GraphicsFormat.R16_UNorm ? 65.535f : 1f;

        /// <summary>
        /// True when depth is available, so <see cref="TryFit"/> can be used.
        /// </summary>
        public bool available => m_DepthTexture != null && m_Occlusion != null && m_Occlusion.enabled;

        /// <summary>
        /// Whether to keep the latest depth texture up to date. Only needed while erasing or while erased objects
        /// are shown, so it's off otherwise to save work every frame.
        /// </summary>
        public bool captureDepth { get; set; }

        /// <summary>
        /// Same as <see cref="captureDepth"/>, requested by <see cref="BackgroundMemory"/> while it takes snapshots.
        /// </summary>
        public bool captureForMemory { get; set; }

        void Awake()
        {
            m_Camera = Camera.main;
            m_Occlusion = FindAnyObjectByType<AROcclusionManager>();
            m_CameraManager = FindAnyObjectByType<ARCameraManager>();
        }

        void OnEnable()
        {
            current = this;
            if (m_CameraManager != null)
                m_CameraManager.frameReceived += OnCameraFrame;
            if (m_Occlusion != null)
                m_Occlusion.frameReceived += OnOcclusionFrame;

            // Registered after the occlusion manager's own handler, so the depth info for this frame exists by then.
            Application.onBeforeRender += OnBeforeRender;
        }

        void OnDisable()
        {
            if (current == this)
                current = null;
            if (m_CameraManager != null)
                m_CameraManager.frameReceived -= OnCameraFrame;
            if (m_Occlusion != null)
                m_Occlusion.frameReceived -= OnOcclusionFrame;
            Application.onBeforeRender -= OnBeforeRender;
        }

        // XR Simulation doesn't send occlusion frame events, but fills in the depth info just before rendering.
        void OnBeforeRender()
        {
            if (!(captureDepth || captureForMemory) || m_Occlusion == null || !m_Occlusion.enabled)
                return;

            try
            {
                if (m_Occlusion.TryGetEnvironmentDepthTexture(out var texture) && texture != null)
                    SetDepthTexture(texture, "before render");
                else if (m_DepthTexture == null && Time.frameCount % 200 == 0)
                    Debug.Log($"[DepthFit] no depth yet: supported {m_Occlusion.descriptor?.environmentDepthImageSupported}, " +
                        $"running {m_Occlusion.subsystem?.running}, mode {m_Occlusion.currentEnvironmentDepthMode} (requested {m_Occlusion.requestedEnvironmentDepthMode})");
            }
            catch (ObjectDisposedException)
            {
                // No depth info this frame.
            }
        }

        void SetDepthTexture(Texture texture, string source)
        {
            if (m_DepthTexture == null)
                Debug.Log($"[DepthFit] depth texture from {source}: {texture.width}x{texture.height} {texture.graphicsFormat}");
            m_DepthTexture = texture;
        }

        // The depth texture can only be looked up while AR Foundation processes a frame (XR Simulation frees its
        // texture info right after), so grab it here. The texture object itself stays valid and is updated in place.
        void OnOcclusionFrame(AROcclusionFrameEventArgs args)
        {
            if (!(captureDepth || captureForMemory) || !m_Occlusion.enabled)
                return;

            Texture texture = null;
            try
            {
                if (!m_Occlusion.TryGetEnvironmentDepthTexture(out texture))
                    texture = null;
            }
            catch (ObjectDisposedException)
            {
                // The manager also sends an empty frame event when it's being disabled; nothing to read then.
            }

            // With human occlusion off, the environment depth image is the only texture in the frame.
            if (texture == null && args.externalTextures.Count > 0)
                texture = args.externalTextures[0].texture;

            if (texture != null)
                SetDepthTexture(texture, "frame event");
        }

        // The depth image is in the camera sensor's orientation; this matrix maps screen UVs onto it,
        // the same way the AR background shader does.
        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (args.displayMatrix.HasValue)
                m_DisplayMatrix = args.displayMatrix.Value;
        }

        /// <summary>
        /// Starts fitting a box to the object at <paramref name="screenPosition"/>. Calls <paramref name="done"/>
        /// on the main thread a frame or two later with whether it worked.
        /// </summary>
        public bool TryFit(Vector2 screenPosition, float floorY, Action<bool, Fit> done)
        {
            if (m_Busy || m_Occlusion == null || !m_Occlusion.enabled)
                return false;

            if (TryReadCpuDepthAndFit(screenPosition, floorY, done))
                return true;

            if (!available)
            {
                Debug.Log($"[DepthFit] not available: busy {m_Busy}, texture {(m_DepthTexture != null ? m_DepthTexture.name : "none")}, occlusion {(m_Occlusion != null && m_Occlusion.enabled)}");
                return false;
            }

            var texture = m_DepthTexture;

            // Capture the camera now: the user may move before the depth image comes back.
            var capture = CaptureCamera(screenPosition, floorY, depthScale);

            m_Busy = true;
            AsyncGPUReadback.Request(texture, 0, TextureFormat.RFloat, request =>
            {
                m_Busy = false;
                if (request.hasError || this == null)
                {
                    Debug.Log("[DepthFit] depth readback failed");
                    done(false, default);
                    return;
                }

                var depth = request.GetData<float>();
                var ok = Compute(depth, request.width, request.height, capture, out var fit);
                done(ok, fit);
            });
            return true;
        }

        /// <summary>
        /// Measured depth at a screen position (0..1, origin bottom left), in metres along the camera axis;
        /// 0 where there's no measurement. Safe to call from any thread.
        /// </summary>
        public delegate float DepthLookup(float u, float v);

        /// <summary>
        /// Reads the current depth image, for one-off uses such as the inpainting mask. Calls
        /// <paramref name="done"/> on the main thread with a lookup, or with null if the read fails. Returns false
        /// when there's no depth to read.
        /// </summary>
        public bool TryReadDepth(Action<DepthLookup> done)
        {
            if (m_Occlusion == null || !m_Occlusion.enabled)
                return false;

            var display = m_DisplayMatrix;
            if (TryReadCpuDepth(out var cpu, out var cpuWidth, out var cpuHeight))
            {
                var values = cpu.ToArray();
                cpu.Dispose();
                done(MakeLookup(values, cpuWidth, cpuHeight, display, 1f));
                return true;
            }

            if (!available || m_Busy)
                return false;

            var scale = depthScale;
            m_Busy = true;
            AsyncGPUReadback.Request(m_DepthTexture, 0, TextureFormat.RFloat, request =>
            {
                m_Busy = false;
                if (request.hasError || this == null)
                {
                    done(null);
                    return;
                }

                done(MakeLookup(request.GetData<float>().ToArray(), request.width, request.height, display, scale));
            });
            return true;
        }

        static DepthLookup MakeLookup(float[] values, int width, int height, Matrix4x4 m, float scale)
        {
            // Test harness: -moveitNoisyDepth makes depth as coarse and noisy as a phone's without a depth sensor.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-moveitNoisyDepth") >= 0)
                values = MakeNoisy(values, width, height, scale, out width, out height, out scale);

            return (u, v) =>
            {
                // Same mapping as SampleDepth.
                var tu = u * m.m00 + v * m.m10 + m.m20;
                var tv = u * m.m01 + v * m.m11 + m.m21;
                var x = Mathf.Clamp((int)(tu * width), 0, width - 1);
                var y = Mathf.Clamp((int)(tv * height), 0, height - 1);
                return values[y * width + x] * scale;
            };
        }

        // 160x90 like a Galaxy S24, noise growing with distance, and some missing readings.
        static float[] MakeNoisy(float[] values, int width, int height, float scale, out int newWidth,
            out int newHeight, out float newScale)
        {
            newWidth = 160;
            newHeight = 90;
            newScale = 1f;
            var random = new System.Random(7);
            var result = new float[newWidth * newHeight];
            for (var y = 0; y < newHeight; y++)
            for (var x = 0; x < newWidth; x++)
            {
                var d = values[(y * height / newHeight) * width + x * width / newWidth] * scale;
                if (d <= 0f || random.NextDouble() < 0.1)
                {
                    result[y * newWidth + x] = 0f;
                    continue;
                }

                // Roughly Gaussian.
                var n = (float)(random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 2f;
                result[y * newWidth + x] = Mathf.Max(0.05f, d + n * (0.03f + 0.03f * d * d));
            }

            return result;
        }

        // On a phone, read ARCore's depth image on the CPU: reading the depth texture back from the GPU fails
        // on OpenGL ES (GL_INVALID_OPERATION). XR Simulation has no CPU depth image, so it uses the GPU path.
        bool TryReadCpuDepthAndFit(Vector2 screenPosition, float floorY, Action<bool, Fit> done)
        {
            if (!TryReadCpuDepth(out var depth, out var width, out var height))
                return false;

            var capture = CaptureCamera(screenPosition, floorY, 1f);
            var ok = Compute(depth, width, height, capture, out var fit);
            depth.Dispose();
            done(ok, fit);
            return true;
        }

        Capture CaptureCamera(Vector2 screenPosition, float floorY, float scale)
        {
            return new Capture
            {
                cameraToWorld = m_Camera.cameraToWorldMatrix,
                projection = m_Camera.projectionMatrix,
                cameraPosition = m_Camera.transform.position,
                display = m_DisplayMatrix,
                // Camera pixels rather than Screen, so it also works when the camera renders to a texture.
                tap = new Vector2(screenPosition.x / m_Camera.pixelWidth, screenPosition.y / m_Camera.pixelHeight),
                aspect = (float)m_Camera.pixelHeight / m_Camera.pixelWidth,
                floorY = floorY,
                depthScale = scale,
            };
        }

        // ARCore's depth image as metres, row by row in the same layout as the depth texture.
        bool TryReadCpuDepth(out NativeArray<float> depth, out int width, out int height)
        {
            depth = default;
            width = height = 0;

            XRCpuImage image;
            try
            {
                if (!m_Occlusion.TryAcquireEnvironmentDepthCpuImage(out image))
                    return false;
            }
            catch (Exception)
            {
                return false;
            }

            using (image)
            {
                if (image.planeCount < 1 ||
                    (image.format != XRCpuImage.Format.DepthFloat32 && image.format != XRCpuImage.Format.DepthUint16))
                {
                    Debug.Log($"[DepthFit] unexpected CPU depth format {image.format}");
                    return false;
                }

                var plane = image.GetPlane(0);
                width = image.width;
                height = image.height;
                depth = new NativeArray<float>(width * height, Allocator.Temp);

                if (image.format == XRCpuImage.Format.DepthFloat32)
                {
                    var values = plane.data.Reinterpret<float>(1);
                    var stride = plane.rowStride / 4;
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        depth[y * width + x] = values[y * stride + x];
                }
                else
                {
                    // Millimetres.
                    var values = plane.data.Reinterpret<ushort>(1);
                    var stride = plane.rowStride / 2;
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        depth[y * width + x] = values[y * stride + x] * 0.001f;
                }

                Debug.Log($"[DepthFit] CPU depth {width}x{height} {image.format}");
                return true;
            }
        }

        struct Capture
        {
            public Matrix4x4 cameraToWorld;
            public Matrix4x4 projection;
            public Vector3 cameraPosition;
            public Matrix4x4 display;
            public Vector2 tap;
            public float aspect;
            public float floorY;
            public float depthScale;
        }

        enum Kind : byte
        {
            None,
            Floor,
            Wall,
            Object,
        }

        bool Compute(NativeArray<float> depth, int width, int height, Capture c, out Fit fit)
        {
            fit = default;
            var columns = k_GridColumns;
            var rows = Mathf.Max(1, Mathf.RoundToInt(columns * c.aspect));
            var count = columns * rows;
            var points = new Vector3[count];
            var distances = new float[count];
            var kinds = new Kind[count];

            RoomWalls.GetAll(m_Walls);

            // 1-2. Grid of 3D points, classified as floor, wall or object.
            for (var gy = 0; gy < rows; gy++)
            {
                for (var gx = 0; gx < columns; gx++)
                {
                    var i = gy * columns + gx;
                    var u = (gx + 0.5f) / columns;
                    var v = (gy + 0.5f) / rows;
                    var d = SampleDepth(depth, width, height, c, u, v);
                    if (d < 0.1f || d > k_MaxRange)
                        continue;

                    var p = Unproject(c, u, v, d);
                    points[i] = p;
                    distances[i] = d;

                    if (p.y < c.floorY + k_FloorBand)
                        kinds[i] = Kind.Floor;
                    else if (p.y > c.floorY + k_MaxHeightAboveFloor || RoomWalls.IsOnWall(p, m_Walls, k_WallBand, k_WallLateralTolerance))
                        kinds[i] = Kind.Wall;
                    else
                        kinds[i] = Kind.Object;
                }
            }

            var normals = ComputeNormals(points, kinds, columns, rows, c.cameraPosition);

            // 3. Seed at the tap (or the nearest object point to it), then grow the region.
            var seedX = Mathf.Clamp((int)(c.tap.x * columns), 0, columns - 1);
            var seedY = Mathf.Clamp((int)(c.tap.y * rows), 0, rows - 1);
            var tapIndex = seedY * columns + seedX;
            Debug.Log($"[DepthFit] depth {width}x{height}, scale {c.depthScale}, grid {columns}x{rows}, " +
                $"floor {Count(kinds, Kind.Floor)}, wall {Count(kinds, Kind.Wall)}, object {Count(kinds, Kind.Object)}, none {Count(kinds, Kind.None)}, " +
                $"tap point {points[tapIndex]} depth {distances[tapIndex]:0.00} kind {kinds[tapIndex]}, floorY {c.floorY:0.00}");
            if (!TryFindSeed(kinds, columns, rows, seedX, seedY, out var seed))
                return false;

            var inRegion = new bool[count];
            var queue = new Queue<int>();
            var region = new List<Vector3>();
            inRegion[seed] = true;
            queue.Enqueue(seed);
            var seedPoint = points[seed];

            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                region.Add(points[i]);
                var x = i % columns;
                var y = i / columns;

                for (var n = 0; n < 4; n++)
                {
                    var nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                    var ny = y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= columns || ny >= rows)
                        continue;

                    var j = ny * columns + nx;
                    if (inRegion[j] || kinds[j] != Kind.Object)
                        continue;

                    var limit = k_NeighbourDistance + k_NeighbourDistancePerMetre * distances[j];
                    if ((points[j] - points[i]).sqrMagnitude > limit * limit)
                        continue;

                    if ((points[j] - seedPoint).sqrMagnitude > k_MaxObjectRadius * k_MaxObjectRadius)
                        continue;

                    if (!SameObject(points[i], normals[i], points[j], normals[j]))
                        continue;

                    inRegion[j] = true;
                    queue.Enqueue(j);
                }
            }

            Debug.Log($"[DepthFit] region {region.Count} points from seed {seedPoint}");
            if (region.Count < k_MinPoints)
                return false;

            // 4. Fit the box, then include things standing on top of the object.
            // ARCore's floor plane is often a few cm off the real floor; measure the floor right around the
            // object instead, so the box reaches down to it (thin legs are otherwise left sticking out).
            c.floorY = LocalFloorY(points, kinds, region, c.floorY);
            fit = FitBox(region, c);
            fit = AvoidNeighbours(fit, region, points, kinds, inRegion, c.floorY);
            fit = IncludeThingsOnTop(fit, points, kinds, inRegion, c.floorY);
            Debug.Log($"[DepthFit] fit front {fit.front}, yaw {fit.yaw:0}, size {fit.size}");
            return true;
        }

        static float LocalFloorY(Vector3[] points, Kind[] kinds, List<Vector3> region, float planeFloorY)
        {
            var centroid = Vector3.zero;
            foreach (var p in region)
                centroid += p;
            centroid /= region.Count;

            var heights = new List<float>();
            for (var i = 0; i < points.Length; i++)
            {
                if (kinds[i] != Kind.Floor)
                    continue;

                var offset = points[i] - centroid;
                offset.y = 0f;
                if (offset.sqrMagnitude <= k_LocalFloorRadius * k_LocalFloorRadius)
                    heights.Add(points[i].y);
            }

            if (heights.Count < k_MinLocalFloorPoints)
                return planeFloorY;

            var local = Percentile(heights, 0.5f);
            Debug.Log($"[DepthFit] local floor {local:0.000} (plane {planeFloorY:0.000}, {heights.Count} points)");
            return local;
        }

        // Surface normals from neighbouring grid points, facing the camera.
        static Vector3[] ComputeNormals(Vector3[] points, Kind[] kinds, int columns, int rows, Vector3 cameraPosition)
        {
            var normals = new Vector3[points.Length];
            for (var y = 1; y < rows - 1; y++)
            {
                for (var x = 1; x < columns - 1; x++)
                {
                    var i = y * columns + x;
                    if (kinds[i] == Kind.None)
                        continue;

                    var left = y * columns + x - 1;
                    var right = y * columns + x + 1;
                    var down = (y - 1) * columns + x;
                    var up = (y + 1) * columns + x;
                    if (kinds[left] == Kind.None || kinds[right] == Kind.None || kinds[down] == Kind.None || kinds[up] == Kind.None)
                        continue;

                    var n = Vector3.Cross(points[right] - points[left], points[up] - points[down]);
                    if (n.sqrMagnitude < 1e-10f)
                        continue;

                    n.Normalize();
                    if (Vector3.Dot(n, cameraPosition - points[i]) < 0f)
                        n = -n;
                    normals[i] = n;
                }
            }

            return normals;
        }

        // Convexity test between neighbouring points: on a convex edge the normals turn away from each other
        // ((nA - nB) . (pA - pB) > 0); on a concave seam they turn towards each other. Flat surfaces always pass.
        static bool SameObject(Vector3 pointA, Vector3 normalA, Vector3 pointB, Vector3 normalB)
        {
            if (normalA == Vector3.zero || normalB == Vector3.zero)
                return true;

            if (Vector3.Dot(normalA, normalB) > k_SameSurfaceCos)
                return true;

            var between = pointA - pointB;
            var length = between.magnitude;
            if (length < 1e-5f)
                return true;

            return Vector3.Dot(normalA - normalB, between / length) > -k_ConcaveTolerance;
        }

        // Raises the box to cover object points standing within its footprint, like a lamp on a chest, which the
        // convexity test keeps out of the region (the seam under them is concave).
        // Pulls in sides of the (padded) box that reach into another object next to it: each side keeps at least
        // k_NeighbourGap from the neighbour, but never cuts into the object itself.
        static Fit AvoidNeighbours(Fit fit, List<Vector3> region, Vector3[] points, Kind[] kinds, bool[] inRegion,
            float floorY)
        {
            var rotation = Quaternion.Euler(0f, fit.yaw, 0f);
            var toLocal = Quaternion.Inverse(rotation);

            // The object's own extent, as in FitBox (x across, z along from the front edge).
            var across = new List<float>(region.Count);
            var along = new List<float>(region.Count);
            foreach (var p in region)
            {
                var local = toLocal * (p - fit.front);
                across.Add(local.x);
                along.Add(local.z);
            }

            var minX = Percentile(across, 0.005f);
            var maxX = Percentile(across, 0.995f);
            var minZ = Percentile(along, 0.005f);
            var maxZ = Percentile(along, 0.995f);

            var x0 = -0.5f * fit.size.x;
            var x1 = 0.5f * fit.size.x;
            var z0 = 0f;
            var z1 = fit.size.z;
            var top = floorY + fit.size.y;
            var changed = false;

            for (var i = 0; i < points.Length; i++)
            {
                if (inRegion[i] || kinds[i] != Kind.Object || points[i].y < floorY + 0.03f || points[i].y > top)
                    continue;

                // Inside the box, or just outside it (the fill reaches a few cm past the box).
                var local = toLocal * (points[i] - fit.front);
                if (local.x < x0 - k_NeighbourGap || local.x > x1 + k_NeighbourGap ||
                    local.z < z0 - k_NeighbourGap || local.z > z1 + k_NeighbourGap)
                    continue;

                // Above or inside the object's footprint: something standing on it, which IncludeThingsOnTop handles.
                var besideX = local.x < minX || local.x > maxX;
                var besideZ = local.z < minZ || local.z > maxZ;
                if (!besideX && !besideZ)
                    continue;

                // Pull in the side facing it the least distance needed.
                if (besideX && (!besideZ || Mathf.Min(Mathf.Abs(local.x - minX), Mathf.Abs(local.x - maxX)) <=
                        Mathf.Min(Mathf.Abs(local.z - minZ), Mathf.Abs(local.z - maxZ))))
                {
                    if (local.x < minX)
                        x0 = Mathf.Min(Mathf.Max(x0, local.x + k_NeighbourGap), minX);
                    else
                        x1 = Mathf.Max(Mathf.Min(x1, local.x - k_NeighbourGap), maxX);
                }
                else if (local.z < minZ)
                    z0 = Mathf.Min(Mathf.Max(z0, local.z + k_NeighbourGap), minZ);
                else
                    z1 = Mathf.Max(Mathf.Min(z1, local.z - k_NeighbourGap), maxZ);

                changed = true;
            }

            if (!changed)
                return fit;

            Debug.Log($"[DepthFit] pulled in from neighbours: x {-0.5f * fit.size.x:0.00}..{0.5f * fit.size.x:0.00} -> " +
                $"{x0:0.00}..{x1:0.00}, z 0..{fit.size.z:0.00} -> {z0:0.00}..{z1:0.00}");
            fit.front += rotation * new Vector3(0.5f * (x0 + x1), 0f, z0);
            fit.size.x = x1 - x0;
            fit.size.z = z1 - z0;
            return fit;
        }

        static Fit IncludeThingsOnTop(Fit fit, Vector3[] points, Kind[] kinds, bool[] inRegion, float floorY)
        {
            var toLocal = Quaternion.Inverse(Quaternion.Euler(0f, fit.yaw, 0f));
            var halfWidth = 0.5f * fit.size.x;
            var top = floorY + fit.size.y;
            var raised = false;

            for (var i = 0; i < points.Length; i++)
            {
                if (inRegion[i] || kinds[i] != Kind.Object)
                    continue;

                var local = toLocal * (points[i] - fit.front);
                if (Mathf.Abs(local.x) > halfWidth || local.z < 0f || local.z > fit.size.z)
                    continue;

                if (points[i].y + k_Padding > top)
                {
                    top = points[i].y + k_Padding;
                    raised = true;
                }
            }

            if (raised)
                fit.size.y = top - floorY;

            return fit;
        }

        static int Count(Kind[] kinds, Kind kind)
        {
            var n = 0;
            foreach (var k in kinds)
            {
                if (k == kind)
                    n++;
            }

            return n;
        }

        static bool TryFindSeed(Kind[] kinds, int columns, int rows, int x, int y, out int seed)
        {
            for (var radius = 0; radius <= k_SeedSearchRadius; radius++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != radius)
                            continue;

                        var sx = x + dx;
                        var sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= columns || sy >= rows)
                            continue;

                        var i = sy * columns + sx;
                        if (kinds[i] == Kind.Object)
                        {
                            seed = i;
                            return true;
                        }
                    }
                }
            }

            seed = -1;
            return false;
        }

        static float SampleDepth(NativeArray<float> depth, int width, int height, Capture c, float u, float v)
        {
            // Same mapping as the AR background shader: (u, v, 1, 0) as a row vector times the display matrix.
            var m = c.display;
            var tu = u * m.m00 + v * m.m10 + m.m20;
            var tv = u * m.m01 + v * m.m11 + m.m21;
            var x = Mathf.Clamp((int)(tu * width), 0, width - 1);
            var y = Mathf.Clamp((int)(tv * height), 0, height - 1);
            return depth[y * width + x] * c.depthScale;
        }

        // Viewport position plus depth along the camera axis to a world-space point.
        static Vector3 Unproject(Capture c, float u, float v, float d)
        {
            var p = c.projection;
            var ndcX = 2f * u - 1f;
            var ndcY = 2f * v - 1f;
            var view = new Vector3(d * (ndcX + p.m02) / p.m00, d * (ndcY + p.m12) / p.m11, -d);
            return c.cameraToWorld.MultiplyPoint3x4(view);
        }

        Fit FitBox(List<Vector3> region, Capture c)
        {
            var centroid = Vector3.zero;
            foreach (var p in region)
                centroid += p;
            centroid /= region.Count;

            // Direction: square to the wall behind the object (seen from the user) when it's close to one, else to the
            // nearest wall, else the tightest footprint.
            var away = centroid - c.cameraPosition;
            away.y = 0f;
            away.Normalize();
            var forward = Vector3.zero;
            var bestScore = float.MaxValue;
            foreach (var wall in m_Walls)
            {
                var distance = Mathf.Abs(wall.SignedDistance(centroid));
                if (distance > k_WallAlignDistance)
                    continue;

                // Walls facing the user count as much nearer than side walls.
                var behind = Vector3.Dot(wall.normal, -away) > 0.5f;
                var score = behind ? distance : distance + k_WallAlignDistance;
                if (score < bestScore)
                {
                    bestScore = score;
                    forward = -wall.normal;
                }
            }

            if (forward == Vector3.zero)
                forward = TightestForward(region);

            // Forward points away from the user.
            var toObject = centroid - c.cameraPosition;
            toObject.y = 0f;
            if (Vector3.Dot(forward, toObject) < 0f)
                forward = -forward;
            forward.y = 0f;
            forward.Normalize();
            var right = new Vector3(forward.z, 0f, -forward.x);

            var across = new List<float>(region.Count);
            var along = new List<float>(region.Count);
            var heights = new List<float>(region.Count);
            foreach (var p in region)
            {
                across.Add(Vector3.Dot(p, right));
                along.Add(Vector3.Dot(p, forward));
                heights.Add(p.y);
            }

            // Percentiles instead of min/max, so a few noisy depth points don't blow up the box.
            // Narrow percentiles: thin parts such as chair and stool legs give only a few points, and they are
            // often the widest part of the object, so they must not be dropped as noise.
            var s0 = Percentile(across, 0.005f) - k_Padding;
            var s1 = Percentile(across, 0.995f) + k_Padding;
            var t0 = Percentile(along, 0.005f) - k_Padding;
            var t1 = Percentile(along, 0.995f);
            var top = Percentile(heights, 0.995f) + k_Padding;

            // The camera only sees the front: reach back to the wall behind, or add a guess.
            var reachedWall = false;
            foreach (var wall in m_Walls)
            {
                if (Vector3.Dot(wall.normal, -forward) < 0.9f)
                    continue;

                var wallT = Vector3.Dot(wall.point, forward);
                if (wallT > t1 - 0.05f && wallT - t1 < k_ReachWallDistance)
                {
                    t1 = wallT;
                    reachedWall = true;
                }
            }

            if (!reachedWall)
                t1 += k_GuessedExtraDepth;

            t1 = Mathf.Max(t1, t0 + k_MinDepth);

            // In a corner, reach sideways to the side wall too.
            foreach (var wall in m_Walls)
            {
                var side = Vector3.Dot(wall.normal, right);
                if (Mathf.Abs(side) < 0.9f)
                    continue;

                var wallS = Vector3.Dot(wall.point, right);
                if (side < 0f && wallS > s1 - 0.05f && wallS - s1 < 0.3f)
                    s1 = wallS;
                else if (side > 0f && wallS < s0 + 0.05f && s0 - wallS < 0.3f)
                    s0 = wallS;
            }

            var frontCenter = right * (0.5f * (s0 + s1)) + forward * t0;
            frontCenter.y = c.floorY;

            return new Fit
            {
                front = frontCenter,
                yaw = Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y,
                size = new Vector3(s1 - s0, Mathf.Max(top - c.floorY, 0.1f), t1 - t0),
            };
        }

        // Tries yaw angles over a quarter turn and returns the forward direction with the smallest footprint.
        static Vector3 TightestForward(List<Vector3> region)
        {
            var bestArea = float.MaxValue;
            var best = Vector3.forward;
            var across = new List<float>(region.Count);
            var along = new List<float>(region.Count);

            for (var angle = 0; angle < 90; angle += 3)
            {
                var forward = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                var right = new Vector3(forward.z, 0f, -forward.x);
                across.Clear();
                along.Clear();
                foreach (var p in region)
                {
                    across.Add(Vector3.Dot(p, right));
                    along.Add(Vector3.Dot(p, forward));
                }

                var area = (Percentile(across, 0.97f) - Percentile(across, 0.03f)) *
                    (Percentile(along, 0.97f) - Percentile(along, 0.03f));
                if (area < bestArea)
                {
                    bestArea = area;
                    best = forward;
                }
            }

            return best;
        }

        static float Percentile(List<float> values, float fraction)
        {
            values.Sort();
            var index = Mathf.Clamp(Mathf.RoundToInt(fraction * (values.Count - 1)), 0, values.Count - 1);
            return values[index];
        }
    }
}
