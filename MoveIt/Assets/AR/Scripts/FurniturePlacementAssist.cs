using MoveIt.Furniture;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace MoveIt.AR
{
    /// <summary>
    /// Adds wall snapping and the footprint/size display to every furniture item the
    /// <see cref="ObjectSpawner"/> places, and keeps furniture at its real-world size.
    /// </summary>
    /// <remarks>
    /// If the scene has an <see cref="ObjectSpawner"/> but no instance of this component, one is created at
    /// load time together with <see cref="RoomLightEstimation"/>, so the features work without editing the scene.
    /// Add the components to the scene yourself to change their settings.
    /// </remarks>
    public class FurniturePlacementAssist : MonoBehaviour
    {
        [Tooltip("Spawner whose furniture gets the placement features. Found automatically when empty.")]
        [SerializeField]
        ObjectSpawner m_ObjectSpawner;

        [Tooltip("Stop pinch-to-scale on furniture, so every item keeps its real-world size.")]
        [SerializeField]
        bool m_LockToRealScale = true;

        [Tooltip("Snap furniture to walls when it is let go near one.")]
        [SerializeField]
        bool m_WallSnapping = true;

        /// <summary>
        /// Whether furniture snaps to walls on release. Applies to items already placed too.
        /// </summary>
        public bool wallSnapping
        {
            get => m_WallSnapping;
            set
            {
                m_WallSnapping = value;
                foreach (var snap in FindObjectsByType<FurnitureWallSnap>(FindObjectsSortMode.None))
                    snap.snapEnabled = value;
            }
        }

        void Awake()
        {
            if (m_ObjectSpawner == null)
                m_ObjectSpawner = FindAnyObjectByType<ObjectSpawner>();
        }

        void OnEnable()
        {
            if (m_ObjectSpawner != null)
                m_ObjectSpawner.objectSpawned += OnObjectSpawned;
        }

        void OnDisable()
        {
            if (m_ObjectSpawner != null)
                m_ObjectSpawner.objectSpawned -= OnObjectSpawned;
        }

        void Start()
        {
            // Furniture placed in the scene by hand gets the same features.
            foreach (var item in FindObjectsByType<FurnitureItem>(FindObjectsSortMode.None))
                Setup(item.gameObject);
        }

        void OnObjectSpawned(GameObject spawned)
        {
            Setup(spawned);
        }

        void Setup(GameObject target)
        {
            // Template shapes and other non-furniture objects are left alone.
            if (target.GetComponent<FurnitureItem>() == null)
                return;

            if (m_LockToRealScale)
            {
                target.transform.localScale = Vector3.one;
                var transformer = target.GetComponent<ARTransformer>();
                if (transformer != null)
                {
                    transformer.minScale = 1f;
                    transformer.maxScale = 1f;
                }
            }

            var snap = target.GetComponent<FurnitureWallSnap>();
            if (snap == null)
                snap = target.AddComponent<FurnitureWallSnap>();
            snap.snapEnabled = m_WallSnapping;

            if (target.GetComponent<FurnitureFootprint>() == null)
                target.AddComponent<FurnitureFootprint>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RegisterSceneHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (FindAnyObjectByType<ObjectSpawner>() == null || FindAnyObjectByType<FurniturePlacementAssist>() != null)
                return;

            var features = new GameObject("MoveIt AR Features");
            SceneManager.MoveGameObjectToScene(features, scene);
            features.AddComponent<FurniturePlacementAssist>();

            if (FindAnyObjectByType<RoomLightEstimation>() == null)
                features.AddComponent<RoomLightEstimation>();
        }
    }
}
