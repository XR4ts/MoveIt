using UnityEngine;
using MoveIt.Furniture;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

public class FurnitureCatalogSpawner : MonoBehaviour
{
    [SerializeField] FurnitureCatalog catalog;
    [SerializeField] ObjectSpawner objectSpawner;

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

        if (objectSpawner.objectPrefabs.Count > 0)
            objectSpawner.SetSpawnObjectIndex(0);
        else
            Debug.LogWarning("The furniture catalogue is empty.");
    }

    public void SelectFurniture(int index)
    {
        if (index < 0 || index >= objectSpawner.objectPrefabs.Count)
        {
            Debug.LogWarning("Invalid furniture selection index.");
            return;
        }

        objectSpawner.SetSpawnObjectIndex(index);
    }
}
