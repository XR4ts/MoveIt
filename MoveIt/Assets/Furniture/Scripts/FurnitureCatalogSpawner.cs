using UnityEngine;
using UnityEngine.Events;
using MoveIt.Furniture;
using UnityEngine.XR.Interaction.Toolkit.Samples.ARStarterAssets;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

/// <summary>
/// Fills the object spawner from the furniture catalog and places furniture only after the user has picked
/// a piece in the menu. Each pick places one piece; tapping the floor again does nothing until the next pick.
/// </summary>
public class FurnitureCatalogSpawner : MonoBehaviour
{
    [SerializeField] FurnitureCatalog catalog;
    [SerializeField] ObjectSpawner objectSpawner;

    int m_Selected = -1;

    /// <summary>
    /// Whether a piece has been picked and the next tap on the floor will place it.
    /// </summary>
    public bool hasSelection => m_Selected >= 0;

    private void Awake()
    {
        if (catalog == null || objectSpawner == null)
        {
            Debug.LogError(
                "Assign the FurnitureCatalog and ObjectSpawner.");
            enabled = false;
            return;
        }

        objectSpawner.objectPrefabs.Clear();

        foreach (FurnitureItem item in catalog.items)
        {
            if (item == null)
                continue;

            objectSpawner.objectPrefabs.Add(item.gameObject);
        }

        if (objectSpawner.objectPrefabs.Count == 0)
            Debug.LogWarning("The furniture catalogue is empty.");
    }

    private void Start()
    {
        if (!enabled)
            return;

        // The spawn triggers call the spawner directly, which would place something (a random piece, if none is
        // picked) on every tap. Send their taps through Place instead, which checks that a piece was picked.
        foreach (var trigger in FindObjectsByType<ARInteractorSpawnTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var spawnTriggered = trigger.objectSpawnTriggered;
            for (var i = 0; i < spawnTriggered.GetPersistentEventCount(); i++)
            {
                if (spawnTriggered.GetPersistentTarget(i) == objectSpawner)
                    spawnTriggered.SetPersistentListenerState(i, UnityEventCallState.Off);
            }

            spawnTriggered.AddListener(Place);
        }
    }

    public void SelectFurniture(int index)
    {
        if (index < 0 || index >= objectSpawner.objectPrefabs.Count)
        {
            Debug.LogWarning("Invalid furniture selection index.");
            return;
        }

        m_Selected = index;
        objectSpawner.SetSpawnObjectIndex(index);
    }

    void Place(Vector3 point, Vector3 normal)
    {
        if (!hasSelection)
            return;

        objectSpawner.SetSpawnObjectIndex(m_Selected);
        if (objectSpawner.TrySpawnObject(point, normal))
            m_Selected = -1;
    }
}
