using MoveIt.Furniture;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace MoveIt.AR
{
    /// <summary>
    /// While a furniture item is held, outlines its footprint on the floor and shows its real size.
    /// The colour tells the user what will happen on release: white = free, blue = will snap to a wall,
    /// red = overlaps a wall or other furniture.
    /// </summary>
    [RequireComponent(typeof(FurnitureItem))]
    public class FurnitureFootprint : MonoBehaviour
    {
        static readonly Color k_FreeColor = new Color(1f, 1f, 1f, 0.9f);
        static readonly Color k_SnapColor = new Color(0.25f, 0.75f, 1f, 0.95f);
        static readonly Color k_BlockedColor = new Color(1f, 0.3f, 0.25f, 0.95f);

        const float k_LineWidth = 0.012f;
        const float k_FloorOffset = 0.005f;
        const float k_LabelHeightAboveItem = 0.12f;

        static Material s_LineMaterial;

        XRGrabInteractable m_Grab;
        FurnitureWallSnap m_WallSnap;
        BoxCollider m_Box;
        LineRenderer m_Outline;
        TextMeshPro m_Label;
        Transform m_Camera;
        string m_SizeText;

        void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_WallSnap = GetComponent<FurnitureWallSnap>();
            m_Box = GetComponent<BoxCollider>();

            CreateOutline();
            CreateLabel();
            SetVisible(false);
        }

        void LateUpdate()
        {
            var visible = m_Grab != null && m_Grab.isSelected;
            SetVisible(visible);
            if (!visible)
                return;

            var blocked = m_WallSnap != null && m_WallSnap.IsBlocked();
            var snaps = !blocked && m_WallSnap != null && m_WallSnap.snapEnabled &&
                m_WallSnap.TryGetSnapPose(out _, out _, out _);

            var color = blocked ? k_BlockedColor : snaps ? k_SnapColor : k_FreeColor;
            m_Outline.startColor = color;
            m_Outline.endColor = color;
            m_Label.color = color;
            m_Label.text = blocked ? $"{m_SizeText}\nDoesn't fit here" : m_SizeText;

            FaceCamera();
        }

        void SetVisible(bool visible)
        {
            if (m_Outline.enabled == visible)
                return;

            m_Outline.enabled = visible;
            m_Label.gameObject.SetActive(visible);

            if (visible)
                m_SizeText = FormatSize(m_WallSnap != null ? m_WallSnap.worldSize : Vector3.Scale(m_Box.size, transform.lossyScale));
        }

        void CreateOutline()
        {
            var outlineObject = new GameObject("Footprint Outline");
            outlineObject.transform.SetParent(transform, false);

            m_Outline = outlineObject.AddComponent<LineRenderer>();
            m_Outline.useWorldSpace = false;
            m_Outline.loop = true;
            m_Outline.widthMultiplier = k_LineWidth;
            m_Outline.numCornerVertices = 2;
            m_Outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Outline.receiveShadows = false;
            m_Outline.alignment = LineAlignment.TransformZ;
            m_Outline.sharedMaterial = GetLineMaterial();

            // The prefab pivot is at the bottom of the model, so local y = 0 is the floor.
            // Lay the line flat: TransformZ alignment faces the line along the child's z axis, which points up here.
            outlineObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var c = m_Box.center;
            var h = 0.5f * m_Box.size;
            // The child is turned 90 degrees about x, so its point (a, b, c) is the parent's (a, -c, b):
            // child y holds the parent's z, and child z = -height above the floor.
            m_Outline.positionCount = 4;
            m_Outline.SetPositions(new[]
            {
                new Vector3(c.x - h.x, c.z - h.z, -k_FloorOffset),
                new Vector3(c.x + h.x, c.z - h.z, -k_FloorOffset),
                new Vector3(c.x + h.x, c.z + h.z, -k_FloorOffset),
                new Vector3(c.x - h.x, c.z + h.z, -k_FloorOffset),
            });
        }

        void CreateLabel()
        {
            var labelObject = new GameObject("Size Label");
            labelObject.transform.SetParent(transform, false);
            labelObject.transform.localPosition = new Vector3(m_Box.center.x, m_Box.center.y + 0.5f * m_Box.size.y + k_LabelHeightAboveItem, m_Box.center.z);

            m_Label = labelObject.AddComponent<TextMeshPro>();
            m_Label.fontSize = 0.6f;
            m_Label.alignment = TextAlignmentOptions.Center;
            m_Label.textWrappingMode = TextWrappingModes.NoWrap;
            m_Label.outlineWidth = 0.2f;
            m_Label.outlineColor = new Color32(0, 0, 0, 200);
        }

        void FaceCamera()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null)
                    return;

                m_Camera = Camera.main.transform;
            }

            var label = m_Label.transform;
            label.rotation = Quaternion.LookRotation(label.position - m_Camera.position, Vector3.up);
        }

        static string FormatSize(Vector3 size)
        {
            return $"{size.x * 100f:0} × {size.z * 100f:0} × {size.y * 100f:0} cm";
        }

        static Material GetLineMaterial()
        {
            if (s_LineMaterial == null)
            {
                // Sprites/Default is in the project's Always Included Shaders, so it exists in builds.
                // Queue 3100 draws it after erased-object fills, which would otherwise paint over it.
                s_LineMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
            }

            return s_LineMaterial;
        }
    }
}
