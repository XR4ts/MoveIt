using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using Debug = UnityEngine.Debug;

namespace MoveIt.AR
{
    /// <summary>
    /// Gives an erased object a clean, still fill instead of one copied live every frame.
    /// </summary>
    /// <remarks>
    /// As soon as the user has picked an object, this takes one photo of the real camera image (no virtual content)
    /// and marks the erased object as a hole: pixels inside the erased box's outline whose measured depth puts them
    /// inside the box or on the floor right around it (its shadow), plus a margin. Things behind or in front of the
    /// box keep their pixels. It then fills the hole with texture from the rest of the photo
    /// (<see cref="PatchInpainter"/>) on a background thread: floor from floor (pixels whose measured depth agrees
    /// they are floor, so furniture isn't copied), and the rest (walls, cabinet fronts) from the rest. The result goes
    /// to the <see cref="ErasedRegion"/>, whose shader projects the floor and walls behind the object into the photo,
    /// so the fill is pinned to the room and doesn't shimmer. When the user changes the box, the same photo is
    /// inpainted again with the new box.
    /// Then it builds a top-down map of the floor around the object from the inpainted photo, filling in floor the
    /// photo didn't show (off its edges, or under other furniture). Seen from the side, the floor behind the object is
    /// often off the edge of the photo; there the shader uses this map, so the fill holds up from every side.
    /// </remarks>
    public class EraseInpainter : MonoBehaviour
    {
        // Photo width; full-resolution detail is copied at this size, the inpainting itself runs at most at
        // k_MaxInpaintSize on a crop around the hole. (At 1080, the full screen width of a Galaxy S24, the camera
        // image never arrived in the photo on that phone.)
        const int k_MaxPhotoWidth = 720;
        // A photo that fails its check is taken again on the following frames, this many times at most.
        const int k_PhotoAttempts = 30;
        // Cleared to this before the camera image is drawn, to notice when it wasn't.
        static readonly Color k_PhotoSentinel = new Color(1f, 0f, 1f, 1f);
        const int k_MaxInpaintSize = 640;
        // The hole reaches this far past the erased box (metres), and is grown by this fraction of the photo width,
        // so the object's edges and contact shadow are inside it even when the box or tracking is slightly off.
        const float k_HoleMargin = 0.05f;
        const float k_HoleGrow = 0.02f;
        // Depth noise allowed when deciding that a measured point belongs to the erased object (metres). Kept small
        // so things next to the object (a bed beside a stool) stay; the hole is widened in the image instead.
        const float k_ObjectSlack = 0.04f;
        // Floor further away than this is too small in the photo to be a useful source.
        const float k_MaxFloorSource = 6f;
        // Parts of the hole smaller than this many pixels aren't worth a pass of their own.
        const int k_MinPassPixels = 200;
        const float k_DepthWait = 1f;
        // The floor map reaches this far past the box and its shadow skirt (metres), with texels of at least
        // k_MapTexel metres and at most k_MapMaxSize texels a side.
        const float k_MapMargin = 1.2f;
        const float k_MapTexel = 0.01f;
        const int k_MapMaxSize = 384;
        // Depth on phones without a depth sensor is coarse and noisy; whether a pixel is floor is decided by the
        // majority within this fraction of the photo width, so the floor mask has no salt-and-pepper speckles.
        const float k_FloorMaskSmoothing = 0.02f;
        // With this file in the app's data folder, every run saves its images there (for debugging on a phone).
        const string k_DebugFlag = "inpaint-debug";

        // The inpainted photo, plus what the floor map is built from.
        sealed class PhotoResult
        {
            public Color32[] pixels;
            public float[] image;
            // Pixels showing floor: seen as floor (depth agrees), or painted in as floor.
            public bool[] floor;
        }

        sealed class MapResult
        {
            public Color32[] pixels;
            public int width;
            public int depth;
            public Matrix4x4 worldToMap;
        }

        struct View
        {
            public Matrix4x4 viewProjection;
            public Matrix4x4 inverseViewProjection;
            public Vector3 position;
            public Vector3 forward;
        }

        enum Surface : byte
        {
            None,
            Floor,
            Wall,
        }

        // One erased region's photo (with the depth image of the same moment) and its inpainting state.
        sealed class Job
        {
            public bool hasPhoto;
            public Color32[] photo;
            public int width;
            public int height;
            public View view;
            public DepthObjectFitter.DepthLookup depth;
            public bool depthDone;
            // The box changed since the last run (or there was none yet).
            public bool dirty;
            public bool running;
            // The user is done with this region: forget the photo once the last run is finished.
            public bool finished;
            public int photoAttempts;
        }

        readonly Dictionary<ErasedRegion, Job> m_Jobs = new Dictionary<ErasedRegion, Job>();
        readonly List<ErasedRegion> m_Regions = new List<ErasedRegion>();
        Camera m_Camera;
        ARCameraBackground m_Background;
        string m_DataFolder;
        RenderTexture m_PhotoTarget;
        Texture2D m_PhotoReadback;

        /// <summary>
        /// Whether any photo is waiting to be taken or being inpainted.
        /// </summary>
        public bool busy
        {
            get
            {
                foreach (var pair in m_Jobs)
                {
                    if (pair.Key != null && (pair.Value.dirty || pair.Value.running))
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Whether <paramref name="region"/> is waiting for its (updated) inpainted photo.
        /// </summary>
        public bool IsBusy(ErasedRegion region)
        {
            return region != null && m_Jobs.TryGetValue(region, out var job) && (job.dirty || job.running);
        }

        /// <summary>
        /// Inpaints <paramref name="region"/> with its current box. The first request takes the photo, as the next
        /// frame renders, so call it while the camera looks at the object; later ones reuse that photo.
        /// </summary>
        public void Request(ErasedRegion region)
        {
            if (region == null)
                return;

            if (!m_Jobs.TryGetValue(region, out var job))
            {
                job = new Job();
                m_Jobs[region] = job;
            }

            job.dirty = true;
        }

        /// <summary>
        /// The user is done with <paramref name="region"/>: finish any pending inpainting, then free the photo.
        /// </summary>
        public void Finish(ErasedRegion region)
        {
            if (region == null)
                return;

            if (!m_Jobs.ContainsKey(region))
                Request(region);
            m_Jobs[region].finished = true;
        }

        void Awake()
        {
            m_Camera = Camera.main;
            m_DataFolder = Application.persistentDataPath;
        }

        // Where to save debug images, or null when the debug flag file isn't there.
        string DebugFolder()
        {
            try
            {
                return File.Exists(Path.Combine(m_DataFolder, k_DebugFlag)) ? m_DataFolder : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        void OnDestroy()
        {
            if (m_PhotoTarget != null)
                m_PhotoTarget.Release();
            if (m_PhotoReadback != null)
                Destroy(m_PhotoReadback);
        }

        void Update()
        {
            m_Regions.Clear();
            m_Regions.AddRange(m_Jobs.Keys);
            foreach (var region in m_Regions)
            {
                var job = m_Jobs[region];

                // Undone or cancelled, or done and no longer needed.
                if (region == null || (job.finished && !job.dirty && !job.running))
                {
                    if (!job.running)
                        m_Jobs.Remove(region);
                    continue;
                }

                if (job.hasPhoto && job.dirty && !job.running)
                    StartCoroutine(Run(region, job));
            }
        }

        // The AR camera image is current here (XR Simulation only provides it while a frame is being processed).
        void OnBeforeRender()
        {
            m_Regions.Clear();
            m_Regions.AddRange(m_Jobs.Keys);
            foreach (var region in m_Regions)
            {
                var job = m_Jobs[region];
                if (region == null || job.hasPhoto || !job.dirty)
                    continue;

                if (m_Camera == null)
                    m_Camera = Camera.main;
                if (!TryTakePhoto(out job.photo, out job.width, out job.height, out var problem))
                {
                    job.photoAttempts++;
                    Debug.Log($"[Inpaint] photo attempt {job.photoAttempts} failed: {problem}");
                    if (job.photoAttempts >= k_PhotoAttempts || problem == "no camera image")
                    {
                        // Show the live fill rather than nothing.
                        region.allowLiveFill = true;
                        m_Jobs.Remove(region);
                    }

                    continue;
                }

                job.hasPhoto = true;
                var viewProjection = m_Camera.projectionMatrix * m_Camera.worldToCameraMatrix;
                job.view = new View
                {
                    viewProjection = viewProjection,
                    inverseViewProjection = viewProjection.inverse,
                    position = m_Camera.transform.position,
                    forward = m_Camera.transform.forward,
                };

                // Depth of the same moment tells floor that's really floor from furniture standing on it. Optional.
                var fitter = DepthObjectFitter.current;
                if (fitter == null || !fitter.TryReadDepth(lookup =>
                    {
                        job.depth = lookup;
                        job.depthDone = true;
                    }))
                    job.depthDone = true;
            }
        }

        bool TryTakePhoto(out Color32[] pixels, out int width, out int height, out string problem)
        {
            pixels = null;
            width = height = 0;
            problem = null;
            if (m_Background == null)
                m_Background = FindAnyObjectByType<ARCameraBackground>();
            if (m_Camera == null || m_Background == null || m_Background.material == null)
            {
                problem = "no camera image";
                return false;
            }

            width = Mathf.Min(k_MaxPhotoWidth, m_Camera.pixelWidth);
            height = Mathf.RoundToInt(width * (float)m_Camera.pixelHeight / m_Camera.pixelWidth);

            // One render target, kept: borrowing one from the pool each time is what failed on the phone.
            if (m_PhotoTarget == null || m_PhotoTarget.width != width || m_PhotoTarget.height != height)
            {
                if (m_PhotoTarget != null)
                    m_PhotoTarget.Release();
                if (m_PhotoReadback != null)
                    Destroy(m_PhotoReadback);
                m_PhotoTarget = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "Erase Photo",
                };
                m_PhotoTarget.Create();
                m_PhotoReadback = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            }

            var previous = RenderTexture.active;
            RenderTexture.active = m_PhotoTarget;
            GL.Clear(false, true, k_PhotoSentinel);
            RenderTexture.active = previous;

            // The AR background material draws the camera image as it appears on screen, without virtual content.
            Graphics.Blit(null, m_PhotoTarget, m_Background.material, 0);

            RenderTexture.active = m_PhotoTarget;
            m_PhotoReadback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = previous;

            pixels = m_PhotoReadback.GetPixels32();
            problem = CheckPhoto(pixels, width, height);
            return problem == null;
        }

        // Null when the photo looks like a camera image; otherwise what's wrong with it: still the clear colour (the
        // camera image wasn't drawn), or random static (whatever was in graphics memory).
        static string CheckPhoto(Color32[] pixels, int width, int height)
        {
            const int samples = 400;
            var sentinel = 0;
            var gradient = 0f;
            var random = new System.Random(1);
            for (var k = 0; k < samples; k++)
            {
                var x = random.Next(1, width - 1);
                var y = random.Next(1, height - 1);
                var p = pixels[y * width + x];
                if (p.r > 250 && p.g < 5 && p.b > 250)
                    sentinel++;

                var q = pixels[y * width + x + 1];
                var r = pixels[(y + 1) * width + x];
                gradient += Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b) +
                    Mathf.Abs(p.r - r.r) + Mathf.Abs(p.g - r.g) + Mathf.Abs(p.b - r.b);
            }

            if (sentinel > samples / 2)
                return "camera image not drawn";

            // Neighbouring pixels of a real photo are similar (average difference a few levels per channel); in
            // static they're unrelated (a broken photo on a Galaxy S24 measured 57).
            var meanDifference = gradient / (samples * 6f);
            if (meanDifference > 25f)
                return $"looks like static (neighbour difference {meanDifference:0})";

            return null;
        }

        IEnumerator Run(ErasedRegion region, Job job)
        {
            job.dirty = false;
            job.running = true;
            try
            {
                var giveUp = Time.realtimeSinceStartup + k_DepthWait;
                while (!job.depthDone && Time.realtimeSinceStartup < giveUp)
                    yield return null;

                if (region == null)
                    yield break;

                // The box as it is now; if it changes while this runs, the job is marked dirty and runs again.
                var geometry = region.GetGeometry();
                var depth = job.depth;
                var debugFolder = DebugFolder();
                var task = Task.Run(() => Process(job.photo, job.width, job.height, job.view, geometry, depth,
                    debugFolder));
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted)
                {
                    Debug.LogError($"[Inpaint] failed: {task.Exception?.GetBaseException()}");
                    if (region != null)
                        region.allowLiveFill = true;
                    yield break;
                }

                if (region == null || task.Result == null)
                    yield break;

                var photo = task.Result;
                region.SetInpainted(CreateTexture("Inpainted Background", photo.pixels, job.width, job.height),
                    job.view.viewProjection);

                // The box changed meanwhile: the next run builds the map.
                if (job.dirty)
                    yield break;

                var view = job.view;
                var mapTask = Task.Run(() => BuildFloorMap(photo, job.width, job.height, view, geometry, debugFolder));
                while (!mapTask.IsCompleted)
                    yield return null;

                if (mapTask.IsFaulted)
                {
                    Debug.LogError($"[Inpaint] floor map failed: {mapTask.Exception?.GetBaseException()}");
                    yield break;
                }

                var map = mapTask.Result;
                if (region != null && map != null)
                    region.SetFloorMap(CreateTexture("Floor Map", map.pixels, map.width, map.depth), map.worldToMap);
            }
            finally
            {
                job.running = false;
            }
        }

        static Texture2D CreateTexture(string textureName, Color32[] pixels, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                name = textureName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // Off the main thread: masks, inpainting, and back to pixels. Null when there's nothing to fill.
        static PhotoResult Process(Color32[] photo, int width, int height, View view, EraseGeometry geometry,
            DepthObjectFitter.DepthLookup depth, string debugFolder)
        {
            var timer = Stopwatch.StartNew();
            var count = width * height;
            var hole = new bool[count];
            var floorBehind = new bool[count];
            var floorSource = new bool[count];
            var otherSource = new bool[count];

            var holeHalf = geometry.halfExtents + Vector3.one * k_HoleMargin;
            var skirtCenter = new Vector3(0f, -geometry.halfExtents.y, 0f);
            var skirtHalf = new Vector3(
                geometry.halfExtents.x + geometry.skirt + k_HoleMargin,
                0.03f + k_HoleMargin,
                geometry.halfExtents.z + geometry.skirt + k_HoleMargin);
            var originBox = geometry.worldToBox.MultiplyPoint3x4(view.position);

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var u = (x + 0.5f) / width;
                var v = (y + 0.5f) / height;
                var far = view.inverseViewProjection.MultiplyPoint(new Vector3(2f * u - 1f, 2f * v - 1f, 1f));
                var dir = (far - view.position).normalized;

                // The hole: what the camera sees through the (enlarged) box or the floor slab around it, if depth
                // says it's the object (or its shadow) rather than something behind or in front of the box.
                var dirBox = geometry.worldToBox.MultiplyVector(dir);
                var cosine = Vector3.Dot(dir, view.forward);
                if (RayHitsBox(originBox, dirBox, Vector3.zero, holeHalf) ||
                    RayHitsBox(originBox, dirBox, skirtCenter, skirtHalf))
                {
                    // Without depth, the whole outline. With depth, a missing reading isn't taken as the object:
                    // phones miss many readings, and counting them would swallow everything around it.
                    var measured = depth != null ? depth(u, v) : 0f;
                    if (depth == null)
                        hole[i] = true;
                    else if (measured <= 0.05f)
                        hole[i] = false;
                    else
                    {
                        var p = originBox + dirBox * (measured / Mathf.Max(cosine, 1e-3f));
                        hole[i] = Inside(p, Vector3.zero, geometry.halfExtents + Vector3.one * k_ObjectSlack) ||
                            Inside(p, skirtCenter, skirtHalf);
                    }
                }

                // What this pixel would show if the room were only the floor and the walls.
                var surface = HitBackground(view.position, dir, geometry, out var distance);
                if (surface != Surface.Floor)
                {
                    otherSource[i] = true;
                    continue;
                }

                floorBehind[i] = true;
                var expected = distance * cosine;
                var isFloor = distance < k_MaxFloorSource;
                if (isFloor && depth != null)
                {
                    // Something nearer than the floor (a table, a cabinet) stands there.
                    var measured = depth(u, v);
                    if (measured > 0.05f && Mathf.Abs(measured - expected) > 0.1f + 0.08f * expected)
                        isFloor = false;
                }

                floorSource[i] = isFloor;
            }

            // Drop isolated noisy readings, then widen around what's left.
            if (depth != null)
                hole = Majority(hole, width, height, Mathf.Max(1, Mathf.RoundToInt(width * 0.005f)));
            hole = Grow(hole, width, height, Mathf.Max(2, Mathf.RoundToInt(width * k_HoleGrow)));
            if (depth != null)
                floorSource = Majority(floorSource, width, height, Mathf.Max(2, Mathf.RoundToInt(width * k_FloorMaskSmoothing)));

            var floorPixels = 0;
            var otherPixels = 0;
            var otherHole = new bool[count];
            for (var i = 0; i < count; i++)
            {
                if (!hole[i])
                    continue;
                if (floorBehind[i])
                    floorPixels++;
                else
                {
                    otherHole[i] = true;
                    otherPixels++;
                }
            }

            if (floorPixels + otherPixels == 0)
                return null;

            var image = new float[count * 3];
            for (var i = 0; i < count; i++)
            {
                image[i * 3] = photo[i].r / 255f;
                image[i * 3 + 1] = photo[i].g / 255f;
                image[i * 3 + 2] = photo[i].b / 255f;
            }

            var settings = PatchInpainter.Settings.Default;

            // Floor first, from floor only. It fills the whole hole; the part in front of walls is redone next.
            var floorPass = floorPixels >= k_MinPassPixels;
            if (floorPass)
                PatchInpainter.InpaintCropped(image, hole, width, height, settings, floorSource, k_MaxInpaintSize);

            // Walls, cabinet fronts and anything else above the floor, from what's above the floor, with the
            // freshly filled floor as context.
            if (!floorPass || otherPixels >= k_MinPassPixels)
                PatchInpainter.InpaintCropped(image, floorPass ? otherHole : hole, width, height, settings,
                    otherSource, k_MaxInpaintSize);

            var result = new Color32[count];
            var invalid = 0;
            var floorKnown = 0;
            for (var i = 0; i < count; i++)
            {
                result[i] = new Color32(
                    ToByte(image[i * 3], ref invalid),
                    ToByte(image[i * 3 + 1], ref invalid),
                    ToByte(image[i * 3 + 2], ref invalid),
                    // Alpha says what the photo shows: 255 painted in (the erased object was there), 160 floor,
                    // 0 anything else. The shader only uses floor from the photo where it really shows floor.
                    hole[i] ? (byte)255 : floorSource[i] ? (byte)160 : (byte)0);
                if (!hole[i] && floorSource[i])
                    floorKnown++;
            }

            Debug.Log($"[Inpaint] {width}x{height} photo, hole {floorPixels} floor + {otherPixels} other px, " +
                $"floor seen {floorKnown} px, invalid values {invalid}, depth {(depth != null ? "yes" : "no")}, " +
                $"{timer.ElapsedMilliseconds} ms");

            if (debugFolder != null)
            {
                SavePng(debugFolder, "inpaint-1-photo.png", photo, width, height);
                SavePng(debugFolder, "inpaint-2-result.png", result, width, height);
                var masks = new Color32[count];
                for (var i = 0; i < count; i++)
                    masks[i] = new Color32(hole[i] ? (byte)255 : (byte)0, floorSource[i] ? (byte)255 : (byte)0,
                        floorBehind[i] ? (byte)255 : (byte)0, 255);
                SavePng(debugFolder, "inpaint-3-masks.png", masks, width, height);
            }
            var floor = new bool[count];
            for (var i = 0; i < count; i++)
                floor[i] = hole[i] ? floorBehind[i] && floorPass : floorSource[i];
            return new PhotoResult { pixels = result, image = image, floor = floor };
        }

        // Off the main thread: a top-down texture of the floor around the box, aligned with it. Texels the photo
        // shows as floor (or painted in as floor) are copied from it; the rest (off the photo's edges, under other
        // furniture) are inpainted from those.
        static MapResult BuildFloorMap(PhotoResult photo, int width, int height, View view, EraseGeometry geometry,
            string debugFolder)
        {
            var timer = Stopwatch.StartNew();
            var boxToWorld = geometry.worldToBox.inverse;
            var center = boxToWorld.MultiplyPoint3x4(Vector3.zero);
            var origin = new Vector3(center.x, geometry.floorY, center.z);
            var right = Horizontal(boxToWorld.MultiplyVector(Vector3.right));
            var forward = Horizontal(boxToWorld.MultiplyVector(Vector3.forward));
            var sizeX = 2f * (geometry.halfExtents.x + geometry.skirt + k_MapMargin);
            var sizeZ = 2f * (geometry.halfExtents.z + geometry.skirt + k_MapMargin);
            var texel = Mathf.Max(k_MapTexel, Mathf.Max(sizeX, sizeZ) / k_MapMaxSize);
            var mapWidth = Mathf.CeilToInt(sizeX / texel);
            var mapDepth = Mathf.CeilToInt(sizeZ / texel);

            var count = mapWidth * mapDepth;
            var map = new float[count * 3];
            var unknown = new bool[count];
            var known = 0;
            var sample = new float[3];
            for (var j = 0; j < mapDepth; j++)
            for (var i = 0; i < mapWidth; i++)
            {
                var t = j * mapWidth + i;
                var p = origin + right * (((i + 0.5f) / mapWidth - 0.5f) * sizeX) +
                    forward * (((j + 0.5f) / mapDepth - 0.5f) * sizeZ);

                unknown[t] = true;
                var clip = view.viewProjection * new Vector4(p.x, p.y, p.z, 1f);
                if (clip.w <= 0.05f)
                    continue;

                var u = (clip.x / clip.w * 0.5f + 0.5f) * width - 0.5f;
                var v = (clip.y / clip.w * 0.5f + 0.5f) * height - 0.5f;
                if (u < 0f || v < 0f || u > width - 1f || v > height - 1f)
                    continue;

                var pixel = Mathf.RoundToInt(v) * width + Mathf.RoundToInt(u);
                if (!photo.floor[pixel])
                    continue;

                SampleBilinear(photo.image, width, height, u, v, sample);
                map[t * 3] = sample[0];
                map[t * 3 + 1] = sample[1];
                map[t * 3 + 2] = sample[2];
                unknown[t] = false;
                known++;
            }

            // Too little floor in the photo to build on.
            if (known < count / 20)
                return null;

            if (known < count)
                PatchInpainter.InpaintCropped(map, unknown, mapWidth, mapDepth, PatchInpainter.Settings.Default, null,
                    k_MaxInpaintSize);

            var pixels = new Color32[count];
            var invalid = 0;
            for (var t = 0; t < count; t++)
            {
                pixels[t] = new Color32(
                    ToByte(map[t * 3], ref invalid),
                    ToByte(map[t * 3 + 1], ref invalid),
                    ToByte(map[t * 3 + 2], ref invalid),
                    255);
            }

            if (debugFolder != null)
            {
                var knownMask = new Color32[count];
                for (var t = 0; t < count; t++)
                    knownMask[t] = unknown[t] ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
                SavePng(debugFolder, "inpaint-4-map.png", pixels, mapWidth, mapDepth);
                SavePng(debugFolder, "inpaint-5-map-known.png", knownMask, mapWidth, mapDepth);
            }

            // World point to map UV: u along right, v along forward, 0.5 at the box centre.
            var worldToMap = Matrix4x4.zero;
            worldToMap.SetRow(0, new Vector4(right.x / sizeX, 0f, right.z / sizeX,
                0.5f - Vector3.Dot(origin, right) / sizeX));
            worldToMap.SetRow(1, new Vector4(forward.x / sizeZ, 0f, forward.z / sizeZ,
                0.5f - Vector3.Dot(origin, forward) / sizeZ));
            worldToMap.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

            Debug.Log($"[Inpaint] floor map {mapWidth}x{mapDepth} ({texel * 100f:0.0} cm texels), " +
                $"{count - known} of {count} filled in, invalid values {invalid}, {timer.ElapsedMilliseconds} ms");
            return new MapResult { pixels = pixels, width = mapWidth, depth = mapDepth, worldToMap = worldToMap };
        }

        // Colour channel to a byte; NaN (which casts to anything on some CPUs) counts as invalid and becomes grey.
        static byte ToByte(float value, ref int invalid)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                invalid++;
                return 128;
            }

            return (byte)(Mathf.Clamp01(value) * 255f + 0.5f);
        }

        static void SavePng(string folder, string fileName, Color32[] pixels, int width, int height)
        {
            try
            {
                var bytes = new byte[pixels.Length * 4];
                for (var i = 0; i < pixels.Length; i++)
                {
                    bytes[i * 4] = pixels[i].r;
                    bytes[i * 4 + 1] = pixels[i].g;
                    bytes[i * 4 + 2] = pixels[i].b;
                    bytes[i * 4 + 3] = 255;
                }

                var png = ImageConversion.EncodeArrayToPNG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)width,
                    (uint)height);
                File.WriteAllBytes(Path.Combine(folder, fileName), png);
            }
            catch (Exception e)
            {
                Debug.Log($"[Inpaint] couldn't save {fileName}: {e.Message}");
            }
        }

        // Each pixel becomes what most pixels within radius are (a square window, via a summed-area table).
        static bool[] Majority(bool[] mask, int width, int height, int radius)
        {
            var sums = new int[(width + 1) * (height + 1)];
            for (var y = 0; y < height; y++)
            {
                var row = 0;
                for (var x = 0; x < width; x++)
                {
                    row += mask[y * width + x] ? 1 : 0;
                    sums[(y + 1) * (width + 1) + x + 1] = sums[y * (width + 1) + x + 1] + row;
                }
            }

            var result = new bool[mask.Length];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var x0 = Mathf.Max(0, x - radius);
                var y0 = Mathf.Max(0, y - radius);
                var x1 = Mathf.Min(width, x + radius + 1);
                var y1 = Mathf.Min(height, y + radius + 1);
                var total = sums[y1 * (width + 1) + x1] - sums[y0 * (width + 1) + x1] -
                    sums[y1 * (width + 1) + x0] + sums[y0 * (width + 1) + x0];
                result[y * width + x] = 2 * total > (x1 - x0) * (y1 - y0);
            }

            return result;
        }

        static Vector3 Horizontal(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.forward;
        }

        static void SampleBilinear(float[] image, int width, int height, float x, float y, float[] result)
        {
            var x0 = Mathf.Clamp((int)x, 0, width - 1);
            var y0 = Mathf.Clamp((int)y, 0, height - 1);
            var x1 = Mathf.Min(width - 1, x0 + 1);
            var y1 = Mathf.Min(height - 1, y0 + 1);
            var tx = Mathf.Clamp01(x - x0);
            var ty = Mathf.Clamp01(y - y0);
            for (var c = 0; c < 3; c++)
            {
                var a = Mathf.Lerp(image[(y0 * width + x0) * 3 + c], image[(y0 * width + x1) * 3 + c], tx);
                var b = Mathf.Lerp(image[(y1 * width + x0) * 3 + c], image[(y1 * width + x1) * 3 + c], tx);
                result[c] = Mathf.Lerp(a, b, ty);
            }
        }

        static bool Inside(Vector3 p, Vector3 center, Vector3 half)
        {
            var d = p - center;
            return Mathf.Abs(d.x) <= half.x && Mathf.Abs(d.y) <= half.y && Mathf.Abs(d.z) <= half.z;
        }

        static bool RayHitsBox(Vector3 origin, Vector3 dir, Vector3 center, Vector3 half)
        {
            var tNear = 0f;
            var tFar = float.MaxValue;
            for (var axis = 0; axis < 3; axis++)
            {
                var o = origin[axis] - center[axis];
                var d = dir[axis];
                if (Mathf.Abs(d) < 1e-6f)
                {
                    if (Mathf.Abs(o) > half[axis])
                        return false;
                    continue;
                }

                var t0 = (-half[axis] - o) / d;
                var t1 = (half[axis] - o) / d;
                if (t0 > t1)
                    (t0, t1) = (t1, t0);
                tNear = Mathf.Max(tNear, t0);
                tFar = Mathf.Min(tFar, t1);
                if (tNear > tFar)
                    return false;
            }

            return true;
        }

        // Same as HitBackground in Erase.hlsl: the first of the floor and walls the ray meets, on the room side.
        static Surface HitBackground(Vector3 origin, Vector3 dir, EraseGeometry g, out float distance)
        {
            distance = float.MaxValue;
            var surface = Surface.None;

            if (dir.y < -1e-4f)
            {
                var t = (g.floorY - origin.y) / dir.y;
                if (t > 0f && InRoom(origin + dir * t, g, Surface.Floor, false))
                {
                    distance = t;
                    surface = Surface.Floor;
                }
            }

            var tWall = IntersectWall(origin, dir, g.walls.wallPoint, g.walls.wallNormal);
            if (tWall < distance && InRoom(origin + dir * tWall, g, Surface.Wall, false))
            {
                distance = tWall;
                surface = Surface.Wall;
            }

            if (g.walls.hasSecondWall)
            {
                var tWall2 = IntersectWall(origin, dir, g.walls.secondWallPoint, g.walls.secondWallNormal);
                if (tWall2 < distance && InRoom(origin + dir * tWall2, g, Surface.Wall, true))
                {
                    distance = tWall2;
                    surface = Surface.Wall;
                }
            }

            return surface;
        }

        static bool InRoom(Vector3 p, EraseGeometry g, Surface surface, bool secondWall)
        {
            const float tolerance = 1e-3f;
            var onFirstWall = surface == Surface.Wall && !secondWall;
            var onSecondWall = surface == Surface.Wall && secondWall;
            if (!onFirstWall && Vector3.Dot(p - g.walls.wallPoint, g.walls.wallNormal) < -tolerance)
                return false;
            if (g.walls.hasSecondWall && !onSecondWall &&
                Vector3.Dot(p - g.walls.secondWallPoint, g.walls.secondWallNormal) < -tolerance)
                return false;
            if (surface != Surface.Floor && p.y < g.floorY - tolerance)
                return false;
            return true;
        }

        static float IntersectWall(Vector3 origin, Vector3 dir, Vector3 point, Vector3 normal)
        {
            var dn = Vector3.Dot(dir, normal);
            if (dn > -1e-4f)
                return float.MaxValue;
            var t = Vector3.Dot(point - origin, normal) / dn;
            return t > 0f ? t : float.MaxValue;
        }

        // Square dilation by radius, as two running-window passes.
        static bool[] Grow(bool[] mask, int width, int height, int radius)
        {
            var rows = new bool[mask.Length];
            for (var y = 0; y < height; y++)
            {
                var inWindow = 0;
                for (var x = -radius; x < width; x++)
                {
                    var add = x + radius;
                    if (add < width && mask[y * width + add])
                        inWindow++;
                    var remove = x - radius - 1;
                    if (remove >= 0 && mask[y * width + remove])
                        inWindow--;
                    if (x >= 0)
                        rows[y * width + x] = inWindow > 0;
                }
            }

            var result = new bool[mask.Length];
            for (var x = 0; x < width; x++)
            {
                var inWindow = 0;
                for (var y = -radius; y < height; y++)
                {
                    var add = y + radius;
                    if (add < height && rows[add * width + x])
                        inWindow++;
                    var remove = y - radius - 1;
                    if (remove >= 0 && rows[remove * width + x])
                        inWindow--;
                    if (y >= 0)
                        result[y * width + x] = inWindow > 0;
                }
            }

            return result;
        }
    }
}
