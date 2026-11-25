
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using MoveIt.Furniture;

public class FurnitureSelectorUI : MonoBehaviour
{
    private class FurnitureEntry
    {
        public FurnitureItem item;
        public GameObject buttonObject;
        public int spawnerIndex;
    }

    [Header("Furniture")]
    [SerializeField] private FurnitureCatalog catalog;
    [SerializeField] private FurnitureCatalogSpawner catalogSpawner;

    [Header("UI")]
    [SerializeField] private Transform content;
    [SerializeField] private FurnitureItemButton buttonPrefab;
    [SerializeField] private TMP_InputField searchField;
    [SerializeField] private GameObject furnitureMenu;
    [SerializeField] private Button closeButton;

    private readonly List<FurnitureEntry> entries = new();

    private void Start()
    {
        PopulateFurniture();

        searchField.onValueChanged.AddListener(FilterFurniture);
        closeButton.onClick.AddListener(CloseMenu);
    }

    private void PopulateFurniture()
    {
        foreach (Transform child in content)
            Destroy(child.gameObject);

        entries.Clear();

        int spawnerIndex = 0;

        foreach (FurnitureItem item in catalog.items)
        {
            if (item == null)
                continue;

            int selectedIndex = spawnerIndex;

            FurnitureItemButton button =
                Instantiate(buttonPrefab, content);

            button.Setup(item, () =>
            {
                catalogSpawner.SelectFurniture(selectedIndex);
            });

            entries.Add(new FurnitureEntry
            {
                item = item,
                buttonObject = button.gameObject,
                spawnerIndex = selectedIndex
            });

            spawnerIndex++;
        }
    }

    private void FilterFurniture(string searchText)
    {
        string query = searchText.Trim().ToLowerInvariant();

        foreach (FurnitureEntry entry in entries)
        {
            bool matches = entry.item.displayName
                .ToLowerInvariant()
                .Contains(query);

            entry.buttonObject.SetActive(matches);
        }
    }

    private void CloseMenu()
    {
        furnitureMenu.SetActive(false);
    }

    private void OnDestroy()
    {
        if (searchField != null)
            searchField.onValueChanged.RemoveListener(FilterFurniture);

        if (closeButton != null)
            closeButton.onClick.RemoveListener(CloseMenu);
    }
}
