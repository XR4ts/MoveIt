using UnityEngine;
using UnityEngine.Rendering;

namespace MoveIt.AR
{
    /// <summary>
    /// Makes virtual furniture cast shadows onto the real floor. A large invisible surface follows the floor
    /// height and only draws the main light's shadows (see the MoveIt/Shadow Catcher shader). With
    /// <see cref="RoomLightEstimation"/>, the shadows fall in the direction of the room's real light.
    /// </summary>
    public class FloorShadows : MonoBehaviour
    {
        const float k_Size = 30f;
        const float k_AboveFloor = 0.002f;

        [Range(0f, 1f)]
        [SerializeField]
        float m_ShadowStrength = 0.55f;

        Transform m_Surface;
        Renderer m_Renderer;
        Transform m_Camera;
        bool m_HasFurniture;
        float m_NextFurnitureCheck;

        void Start()
        {
            var shader = Resources.Load<Shader>("MoveIt/ShadowCatcher");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("FloorShadows: the shadow catcher shader is missing or not supported.", this);
                enabled = false;
                return;
            }

            var surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            surface.name = "Floor Shadows";
            Destroy(surface.GetComponent<Collider>());
            surface.transform.SetParent(transform, false);
            surface.transform.localScale = new Vector3(k_Size, k_Size, 1f);

            m_Renderer = surface.GetComponent<Renderer>();
            m_Renderer.sharedMaterial = new Material(shader) { name = "Floor Shadows" };
            m_Renderer.sharedMaterial.SetFloat("_ShadowStrength", m_ShadowStrength);
            m_Renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Renderer.receiveShadows = true;
            m_Renderer.enabled = false;

            m_Surface = surface.transform;
            if (Camera.main != null)
                m_Camera = Camera.main.transform;
        }

        void LateUpdate()
        {
            if (m_Surface == null || m_Camera == null)
                return;

            // The shadow surface covers the whole screen, so only draw it when there's furniture to cast shadows.
            if (Time.time >= m_NextFurnitureCheck)
            {
                m_NextFurnitureCheck = Time.time + 0.5f;
                m_HasFurniture = FindAnyObjectByType<MoveIt.Furniture.FurnitureItem>() != null;
            }

            var hasFloor = RoomFloor.TryGetY(out var floorY);
            m_Renderer.enabled = hasFloor && m_HasFurniture;
            if (!m_Renderer.enabled)
                return;

            // Lie flat at floor height under the user; the surface itself is invisible, so it can be big.
            var position = m_Camera.position;
            position.y = floorY + k_AboveFloor;
            m_Surface.SetPositionAndRotation(position, Quaternion.Euler(90f, 0f, 0f));
        }
    }
}
