using UnityEngine;

namespace MoveIt.Furniture
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public class FurnitureItem : MonoBehaviour
    {
        [Tooltip("Stable identifier, matching the model folder name.")]
        [SerializeField] string m_Id;

        [Tooltip("Name shown in the furniture selector.")]
        [SerializeField] string m_DisplayName;

        [Tooltip("Category used to group items in the selector.")]
        [SerializeField] string m_Category;

        [Tooltip("Where the model came from and under which license.")]
        [SerializeField] string m_Attribution;

        [Tooltip("Automatically generated preview image.")]
        [SerializeField] Sprite m_Thumbnail;

        public string id => m_Id;
        public string displayName => m_DisplayName;
        public string category => m_Category;
        public string attribution => m_Attribution;
        public Sprite thumbnail => m_Thumbnail;
        public Vector3 size => GetComponent<BoxCollider>().size;

        public void Initialize(
            string id,
            string displayName,
            string category,
            string attribution)
        {
            m_Id = id;
            m_DisplayName = displayName;
            m_Category = category;
            m_Attribution = attribution;
        }

        public void SetThumbnail(Sprite thumbnail)
        {
            m_Thumbnail = thumbnail;
        }
    }
}