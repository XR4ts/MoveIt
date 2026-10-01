using UnityEngine;

namespace MoveIt.Furniture
{
    /// <summary>
    /// Identifies a placeable furniture prefab and carries its display and attribution data.
    /// The prefab root has its pivot at the bottom centre of the model, so placing it at a
    /// plane hit pose puts the furniture on the floor. A <see cref="BoxCollider"/> on the same
    /// GameObject covers the whole model, for selection raycasts.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public class FurnitureItem : MonoBehaviour
    {
        [Tooltip("Stable identifier, matching the model folder name.")]
        [SerializeField]
        string m_Id;

        [Tooltip("Name shown in the furniture selector.")]
        [SerializeField]
        string m_DisplayName;

        [Tooltip("Category used to group items in the selector, e.g. Seating, Tables, Storage.")]
        [SerializeField]
        string m_Category;

        [Tooltip("Where the model came from and under which license. Required for the README references.")]
        [SerializeField]
        string m_Attribution;

        /// <summary>
        /// Stable identifier, matching the model folder name.
        /// </summary>
        public string id => m_Id;

        /// <summary>
        /// Name shown in the furniture selector.
        /// </summary>
        public string displayName => m_DisplayName;

        /// <summary>
        /// Category used to group items in the selector.
        /// </summary>
        public string category => m_Category;

        /// <summary>
        /// Source, author and license of the model.
        /// </summary>
        public string attribution => m_Attribution;

        /// <summary>
        /// Real-world footprint and height of the item in metres.
        /// </summary>
        public Vector3 size => GetComponent<BoxCollider>().size;

        /// <summary>
        /// Sets the metadata. Called by the editor prefab builder.
        /// </summary>
        public void Initialize(string id, string displayName, string category, string attribution)
        {
            m_Id = id;
            m_DisplayName = displayName;
            m_Category = category;
            m_Attribution = attribution;
        }
    }
}
