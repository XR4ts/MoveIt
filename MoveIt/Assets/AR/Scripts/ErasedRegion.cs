using UnityEngine;

namespace MoveIt.AR
{
    /// <summary>
    /// The walls around an erased object that the erase fill copies from: the wall behind it (or a stand-in
    /// backdrop when there is none) and, for an object in a corner, the side wall. Normals are horizontal and
    /// point into the room.
    /// </summary>
    public struct EraseWalls
    {
        public Vector3 wallPoint;
        public Vector3 wallNormal;
        public bool hasSecondWall;
        public Vector3 secondWallPoint;
        public Vector3 secondWallNormal;
    }

    /// <summary>
    /// One erased real object: a box around it, drawn with the erase shaders so the floor and wall
    /// behind it show through. The GameObject's pivot is the bottom centre of the box, its forward
    /// axis points away from the user (towards the wall), and it isn't scaled.
    /// </summary>
    public class ErasedRegion : MonoBehaviour
    {
        // Grows the box a little so edges of the real object don't peek out.
        const float k_Padding = 0.03f;
        const float k_SampleMargin = 0.06f;

        // The floor this far around the box is replaced too, to remove the object's contact shadow.
        const float k_ShadowSkirt = 0.12f;

        static readonly int s_WorldToBox = Shader.PropertyToID("_EraseWorldToBox");
        static readonly int s_HalfExtents = Shader.PropertyToID("_EraseHalfExtents");
        static readonly int s_FloorY = Shader.PropertyToID("_EraseFloorY");
        static readonly int s_WallPoint = Shader.PropertyToID("_EraseWallPoint");
        static readonly int s_WallNormal = Shader.PropertyToID("_EraseWallNormal");
        static readonly int s_Wall2Point = Shader.PropertyToID("_EraseWall2Point");
        static readonly int s_Wall2Normal = Shader.PropertyToID("_EraseWall2Normal");
        static readonly int s_Margin = Shader.PropertyToID("_EraseMargin");
        static readonly int s_Skirt = Shader.PropertyToID("_EraseSkirt");
        static readonly int s_DepthTexture = Shader.PropertyToID("_EraseDepthTexture");
        static readonly int s_DisplayMatrix = Shader.PropertyToID("_EraseDisplayMatrix");
        static readonly int s_DepthScale = Shader.PropertyToID("_EraseDepthScale");

        static Material s_DepthMaterial;
        static Material s_FillMaterial;

        Vector3 m_Size;
        EraseWalls m_LocalWalls;
        MeshRenderer m_Renderer;
        MaterialPropertyBlock m_Properties;

        /// <summary>
        /// Width (x), height (y) and depth (z) of the erased box in metres.
        /// </summary>
        public Vector3 size => m_Size;

        /// <summary>
        /// Shows the real object again (false) or hides it (true).
        /// </summary>
        public bool erased
        {
            get => m_Renderer != null && m_Renderer.enabled;
            set
            {
                if (m_Renderer != null)
                    m_Renderer.enabled = value;
            }
        }

        /// <summary>
        /// Creates an erased region.
        /// </summary>
        /// <param name="pose">Bottom centre of the box; forward points away from the user.</param>
        /// <param name="size">Width, height and depth in metres.</param>
        /// <param name="walls">The walls around the object, in world space.</param>
        public static ErasedRegion Create(Pose pose, Vector3 size, EraseWalls walls)
        {
            if (!TryLoadMaterials())
                return null;

            var root = new GameObject("Erased Object");
            var region = root.AddComponent<ErasedRegion>();

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Erase Box";
            Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(root.transform, false);

            region.m_Renderer = box.GetComponent<MeshRenderer>();
            region.m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            region.m_Renderer.receiveShadows = false;
            // One submesh and two materials: Unity draws the mesh once with each.
            region.m_Renderer.sharedMaterials = new[] { s_DepthMaterial, s_FillMaterial };
            region.m_Properties = new MaterialPropertyBlock();

            region.SetBox(pose, size, walls);
            return region;
        }

        /// <summary>
        /// Moves and resizes the erased box, for live editing. Parameters as in <see cref="Create"/>.
        /// </summary>
        public void SetBox(Pose pose, Vector3 size, EraseWalls walls)
        {
            var root = transform;
            root.SetPositionAndRotation(pose.position, pose.rotation);
            m_Size = size;

            // Stored relative to the box, so they follow it when an AR anchor moves it.
            m_LocalWalls = new EraseWalls
            {
                wallPoint = root.InverseTransformPoint(walls.wallPoint),
                wallNormal = root.InverseTransformDirection(walls.wallNormal),
                hasSecondWall = walls.hasSecondWall,
                secondWallPoint = root.InverseTransformPoint(walls.secondWallPoint),
                secondWallNormal = root.InverseTransformDirection(walls.secondWallNormal),
            };

            var box = m_Renderer.transform;
            box.localPosition = new Vector3(0f, 0.5f * size.y, 0f);
            box.localRotation = Quaternion.identity;
            // The drawn box also covers the shadow skirt on the floor; the shader leaves pixels that need no change as they are.
            box.localScale = size + 2f * k_Padding * Vector3.one + new Vector3(2f * k_ShadowSkirt, 0f, 2f * k_ShadowSkirt);

            UpdateShaderProperties();
        }

        void LateUpdate()
        {
            // The region may follow an AR anchor, so recompute world-space values every frame.
            UpdateShaderProperties();
        }

        void UpdateShaderProperties()
        {
            if (m_Renderer == null)
                return;

            var root = transform;
            var center = root.TransformPoint(new Vector3(0f, 0.5f * m_Size.y, 0f));
            var worldToBox = Matrix4x4.TRS(center, root.rotation, Vector3.one).inverse;

            m_Properties.SetMatrix(s_WorldToBox, worldToBox);
            m_Properties.SetVector(s_HalfExtents, 0.5f * m_Size + k_Padding * Vector3.one);
            m_Properties.SetFloat(s_FloorY, root.position.y);
            m_Properties.SetVector(s_WallPoint, root.TransformPoint(m_LocalWalls.wallPoint));
            m_Properties.SetVector(s_WallNormal, HorizontalWorld(m_LocalWalls.wallNormal));

            var wall2Point = (Vector4)root.TransformPoint(m_LocalWalls.secondWallPoint);
            wall2Point.w = m_LocalWalls.hasSecondWall ? 1f : 0f;
            m_Properties.SetVector(s_Wall2Point, wall2Point);
            m_Properties.SetVector(s_Wall2Normal, HorizontalWorld(m_LocalWalls.secondWallNormal));
            m_Properties.SetFloat(s_Margin, k_SampleMargin);
            m_Properties.SetFloat(s_Skirt, k_ShadowSkirt);

            // The depth image lets the fill skip copying from spots where something else stands in front of the
            // floor or wall. A scale of 0 tells the shader there's no depth.
            var depth = DepthObjectFitter.current;
            if (depth != null && depth.depthTexture != null)
            {
                m_Properties.SetTexture(s_DepthTexture, depth.depthTexture);
                m_Properties.SetMatrix(s_DisplayMatrix, depth.displayMatrix);
                m_Properties.SetFloat(s_DepthScale, depth.depthScale);
            }
            else
            {
                m_Properties.SetTexture(s_DepthTexture, Texture2D.blackTexture);
                m_Properties.SetFloat(s_DepthScale, 0f);
            }
            m_Renderer.SetPropertyBlock(m_Properties);
        }

        Vector3 HorizontalWorld(Vector3 localDirection)
        {
            var direction = transform.TransformDirection(localDirection);
            direction.y = 0f;
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.forward;
        }

        static bool TryLoadMaterials()
        {
            if (s_DepthMaterial != null && s_FillMaterial != null)
                return true;

            // Shaders under a Resources folder are always included in builds.
            var depthShader = Resources.Load<Shader>("MoveIt/EraseDepth");
            var fillShader = Resources.Load<Shader>("MoveIt/EraseFill");
            if (depthShader == null || fillShader == null || !depthShader.isSupported || !fillShader.isSupported)
            {
                Debug.LogError("ErasedRegion: the erase shaders are missing or not supported on this device.");
                return false;
            }

            s_DepthMaterial = new Material(depthShader) { name = "Erase Depth" };
            s_FillMaterial = new Material(fillShader) { name = "Erase Fill" };
            return true;
        }
    }
}
