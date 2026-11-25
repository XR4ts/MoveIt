using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace MoveIt.AR
{
    /// <summary>
    /// Lights virtual furniture like the real room. Each camera frame, ARCore estimates the
    /// main light's direction, colour and intensity plus ambient light as spherical harmonics;
    /// this copies them onto the scene's directional light and the ambient probe.
    /// </summary>
    /// <remarks>
    /// On ARCore, requesting the main light and spherical harmonics turns on Environmental HDR mode.
    /// When the platform only gives ambient intensity and colour, those are used instead.
    /// </remarks>
    public class RoomLightEstimation : MonoBehaviour
    {
        [Tooltip("Camera manager that delivers light estimates. Found automatically when empty.")]
        [SerializeField]
        ARCameraManager m_CameraManager;

        [Tooltip("Directional light to drive. Uses the scene's sun or first directional light when empty.")]
        [SerializeField]
        Light m_MainLight;

        [Tooltip("Multiplier for the estimated main light brightness.")]
        [SerializeField]
        float m_IntensityScale = 1f;

        float m_DefaultIntensity;
        Color m_DefaultColor;
        Quaternion m_DefaultRotation;
        AmbientMode m_DefaultAmbientMode;
        SphericalHarmonicsL2 m_DefaultAmbientProbe;

        /// <summary>
        /// Whether the light currently follows the estimate. When false, the scene's original lighting is used.
        /// </summary>
        public bool estimationEnabled
        {
            get => enabled;
            set => enabled = value;
        }

        void Awake()
        {
            if (m_CameraManager == null)
                m_CameraManager = FindAnyObjectByType<ARCameraManager>();

            if (m_MainLight == null)
                m_MainLight = FindDirectionalLight();
        }

        void OnEnable()
        {
            if (m_CameraManager == null)
            {
                Debug.LogWarning("RoomLightEstimation: no ARCameraManager in the scene.", this);
                enabled = false;
                return;
            }

            if (m_MainLight != null)
            {
                m_DefaultIntensity = m_MainLight.intensity;
                m_DefaultColor = m_MainLight.color;
                m_DefaultRotation = m_MainLight.transform.rotation;
            }

            m_DefaultAmbientMode = RenderSettings.ambientMode;
            m_DefaultAmbientProbe = RenderSettings.ambientProbe;

            m_CameraManager.requestedLightEstimation =
                LightEstimation.MainLightDirection |
                LightEstimation.MainLightIntensity |
                LightEstimation.AmbientSphericalHarmonics;

            m_CameraManager.frameReceived += OnFrameReceived;
        }

        void OnDisable()
        {
            if (m_CameraManager == null)
                return;

            m_CameraManager.frameReceived -= OnFrameReceived;
            m_CameraManager.requestedLightEstimation = LightEstimation.None;

            if (m_MainLight != null)
            {
                m_MainLight.intensity = m_DefaultIntensity;
                m_MainLight.color = m_DefaultColor;
                m_MainLight.transform.rotation = m_DefaultRotation;
            }

            RenderSettings.ambientMode = m_DefaultAmbientMode;
            RenderSettings.ambientProbe = m_DefaultAmbientProbe;
        }

        void OnFrameReceived(ARCameraFrameEventArgs args)
        {
            var estimate = args.lightEstimation;

            if (m_MainLight != null)
            {
                if (estimate.mainLightDirection.HasValue)
                    m_MainLight.transform.rotation = Quaternion.LookRotation(estimate.mainLightDirection.Value);

                if (estimate.mainLightColor.HasValue)
                    m_MainLight.color = estimate.mainLightColor.Value;
                else if (estimate.colorCorrection.HasValue)
                    m_MainLight.color = estimate.colorCorrection.Value;

                if (estimate.averageMainLightBrightness.HasValue)
                    m_MainLight.intensity = estimate.averageMainLightBrightness.Value * m_IntensityScale;
                else if (estimate.averageBrightness.HasValue)
                    m_MainLight.intensity = estimate.averageBrightness.Value * m_IntensityScale;
            }

            if (estimate.ambientSphericalHarmonics.HasValue)
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientProbe = estimate.ambientSphericalHarmonics.Value;
            }
        }

        static Light FindDirectionalLight()
        {
            if (RenderSettings.sun != null)
                return RenderSettings.sun;

            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                    return light;
            }

            return null;
        }
    }
}
