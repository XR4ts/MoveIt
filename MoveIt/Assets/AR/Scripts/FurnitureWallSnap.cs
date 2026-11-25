using System.Collections;
using System.Collections.Generic;
using MoveIt.Furniture;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace MoveIt.AR
{
    /// <summary>
    /// When the user lets go of a furniture item near a detected wall, turns it to the nearest
    /// right angle to the wall and slides it flush against it. In a corner, it snaps to both walls.
    /// Also reports whether the item overlaps a wall or other furniture, for <see cref="FurnitureFootprint"/>.
    /// </summary>
    [RequireComponent(typeof(FurnitureItem))]
    public class FurnitureWallSnap : MonoBehaviour
    {
        [Tooltip("Snap when the side of the item is closer to a wall than this, in metres.")]
        [SerializeField]
        float m_SnapDistance = 0.3f;

        [Tooltip("Space left between the item and the wall after snapping, in metres.")]
        [SerializeField]
        float m_WallGap = 0.01f;

        [Tooltip("How far the item may poke into a wall or other furniture before it counts as blocked, in metres.")]
        [SerializeField]
        float m_OverlapTolerance = 0.03f;

        [Tooltip("Length of the snap animation, in seconds.")]
        [SerializeField]
        float m_SnapDuration = 0.15f;

        // Walls only count as a corner pair when they are roughly perpendicular.
        const float k_MaxCornerDot = 0.3f;

        static readonly List<Wall> s_Walls = new List<Wall>();
        static readonly Collider[] s_Overlaps = new Collider[16];

        BoxCollider m_Box;
        XRGrabInteractable m_Grab;
        Coroutine m_SnapRoutine;

        /// <summary>
        /// Whether snapping on release is on.
        /// </summary>
        public bool snapEnabled { get; set; } = true;

        void Awake()
        {
            m_Box = GetComponent<BoxCollider>();
            m_Grab = GetComponent<XRGrabInteractable>();
        }

        void OnEnable()
        {
            if (m_Grab == null)
                return;

            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }

        void OnDisable()
        {
            if (m_Grab == null)
                return;

            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            if (m_SnapRoutine != null)
            {
                StopCoroutine(m_SnapRoutine);
                m_SnapRoutine = null;
            }
        }

        void OnReleased(SelectExitEventArgs args)
        {
            if (!snapEnabled || !TryGetSnapPose(out var position, out var rotation, out _))
                return;

            m_SnapRoutine = StartCoroutine(AnimateTo(position, rotation));
        }

        /// <summary>
        /// Computes where the item would end up if it snapped now.
        /// </summary>
        /// <param name="position">Snapped pivot position.</param>
        /// <param name="rotation">Snapped rotation, upright and at a right angle to the wall.</param>
        /// <param name="wallCount">1 for a wall, 2 for a corner.</param>
        /// <returns>False when no wall is within snapping distance.</returns>
        public bool TryGetSnapPose(out Vector3 position, out Quaternion rotation, out int wallCount)
        {
            position = transform.position;
            rotation = transform.rotation;
            wallCount = 0;

            var size = worldSize;
            var searchDistance = 0.5f * Mathf.Max(size.x, size.z) + m_SnapDistance;
            RoomWalls.FindNear(worldCenter, searchDistance, s_Walls);

            // First wall: the one closest to the item's side.
            if (!TryFindClosestWall(position, rotation, null, out var first))
                return false;

            rotation = SquareUpTo(first.normal, rotation);
            position += first.normal * PushOut(first, position, rotation);
            wallCount = 1;

            // Second wall: a perpendicular one, to sit snugly in a corner.
            if (TryFindClosestWall(position, rotation, first.normal, out var second))
            {
                position += second.normal * PushOut(second, position, rotation);
                wallCount = 2;
            }

            return true;
        }

        /// <summary>
        /// True when the item pokes into a wall or another furniture item.
        /// </summary>
        public bool IsBlocked()
        {
            var size = worldSize;
            var center = worldCenter;
            var rotation = transform.rotation;

            RoomWalls.FindNear(center, 0.5f * Mathf.Max(size.x, size.z) + m_SnapDistance, s_Walls);
            foreach (var wall in s_Walls)
            {
                if (GapToWall(wall, transform.position, rotation) < -m_OverlapTolerance)
                    return true;
            }

            var halfExtents = 0.5f * size - Vector3.one * m_OverlapTolerance;
            halfExtents = Vector3.Max(halfExtents, Vector3.one * 0.01f);
            var count = Physics.OverlapBoxNonAlloc(center, halfExtents, s_Overlaps, rotation, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count; i++)
            {
                var other = s_Overlaps[i].GetComponentInParent<FurnitureItem>();
                if (other != null && other.gameObject != gameObject)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Size of the item's box in metres, including its scale.
        /// </summary>
        public Vector3 worldSize => Vector3.Scale(m_Box.size, transform.lossyScale);

        Vector3 worldCenter => transform.TransformPoint(m_Box.center);

        bool TryFindClosestWall(Vector3 position, Quaternion rotation, Vector3? perpendicularTo, out Wall closest)
        {
            closest = default;
            var bestGap = float.MaxValue;
            var found = false;

            foreach (var wall in s_Walls)
            {
                if (perpendicularTo.HasValue && Mathf.Abs(Vector3.Dot(wall.normal, perpendicularTo.Value)) > k_MaxCornerDot)
                    continue;

                // Ignore walls the item's centre is behind: the item is in a different room.
                if (wall.SignedDistance(WorldCenter(position, rotation)) <= 0f)
                    continue;

                var gap = GapToWall(wall, position, SquareUpTo(wall.normal, rotation));
                if (gap > m_SnapDistance || Mathf.Abs(gap) >= Mathf.Abs(bestGap))
                    continue;

                bestGap = gap;
                closest = wall;
                found = true;
            }

            return found;
        }

        // Distance between the side of the item facing the wall and the wall. Negative when it pokes through.
        float GapToWall(Wall wall, Vector3 position, Quaternion rotation)
        {
            return wall.SignedDistance(WorldCenter(position, rotation)) - HalfExtentAlong(wall.normal, rotation);
        }

        // How far to move along the wall normal so the item sits m_WallGap from the wall.
        float PushOut(Wall wall, Vector3 position, Quaternion rotation)
        {
            return m_WallGap - GapToWall(wall, position, rotation);
        }

        Vector3 WorldCenter(Vector3 position, Quaternion rotation)
        {
            return position + rotation * Vector3.Scale(m_Box.center, transform.lossyScale);
        }

        float HalfExtentAlong(Vector3 direction, Quaternion rotation)
        {
            var size = worldSize;
            return 0.5f * (Mathf.Abs(Vector3.Dot(rotation * Vector3.right, direction)) * size.x +
                Mathf.Abs(Vector3.Dot(rotation * Vector3.up, direction)) * size.y +
                Mathf.Abs(Vector3.Dot(rotation * Vector3.forward, direction)) * size.z);
        }

        // Upright rotation whose yaw is the multiple of 90 degrees from the wall normal closest to the current yaw,
        // so a sofa turned roughly towards the room ends up with its back exactly against the wall.
        static Quaternion SquareUpTo(Vector3 wallNormal, Quaternion current)
        {
            var forward = Vector3.ProjectOnPlane(current * Vector3.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = wallNormal;

            var angle = Vector3.SignedAngle(wallNormal, forward, Vector3.up);
            var snapped = Mathf.Round(angle / 90f) * 90f;
            return Quaternion.AngleAxis(snapped, Vector3.up) * Quaternion.LookRotation(wallNormal, Vector3.up);
        }

        IEnumerator AnimateTo(Vector3 position, Quaternion rotation)
        {
            // Let the grab and transformer finish their drop handling first.
            yield return null;

            var startPosition = transform.position;
            var startRotation = transform.rotation;

            for (var t = 0f; t < m_SnapDuration; t += Time.deltaTime)
            {
                var k = Mathf.SmoothStep(0f, 1f, t / m_SnapDuration);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(startPosition, position, k),
                    Quaternion.Slerp(startRotation, rotation, k));
                yield return null;
            }

            transform.SetPositionAndRotation(position, rotation);
            m_SnapRoutine = null;
        }
    }
}
