
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using MoveIt.Furniture;

public class FurnitureItemButton : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private Image thumbnail;
    [SerializeField] private TMP_Text itemName;

    public void Setup(FurnitureItem item, Action onClick)
    {
        if (item == null)
            return;

        itemName.text = item.displayName;
        thumbnail.sprite = item.thumbnail;
        thumbnail.enabled = item.thumbnail != null;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => onClick?.Invoke());
    }
}
