using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MoveIt.AR
{
    /// <summary>
    /// A wall the app knows about: a vertical plane with a horizontal normal pointing into the room.
    /// </summary>
    public readonly struct Wall
    {
        /// <summary>A point on the wall.</summary>
        public readonly Vector3 point;

        /// <summary>Horizontal unit normal, pointing into the room (towards the user).</summary>
        public readonly Vector3 normal;

        /// <summary>The detected plane this wall comes from.</summary>
        public readonly ARPlane plane;

        public Wall(Vector3 point, Vector3 normal, ARPlane plane)
        {
            this.point = point;
            this.normal = normal;
            this.plane = plane;
        }

        /// <summary>
        /// Signed horizontal distance from the wall to <paramref name="position"/>: positive in the room, negative behind the wall.
        /// </summary>
        public float SignedDistance(Vector3 position)
        {
            return Vector3.Dot(position - point, normal);
        }
    }

    /// <summary>
    /// Looks up detected walls (vertical AR planes) near a position, for snapping and collision checks.
    /// </summary>
    public static class RoomWalls
    {
        static ARPlaneManager s_PlaneManager;
        static Transform s_Camera;

        /// <summary>
        /// How far outside a plane's detected area a wall still counts, in metres. Walls are usually only
        /// partly detected, so furniture next to the detected patch should still snap to it.
        /// </summary>
        public const float lateralMargin = 0.75f;

        /// <summary>
        /// Adds the walls whose plane covers <paramref name="position"/> (within <see cref="lateralMargin"/>)
        /// and that are closer than <paramref name="maxDistance"/> to it.
        /// </summary>
        public static void FindNear(Vector3 position, float maxDistance, List<Wall> results)
        {
            results.Clear();

            if (!TryGetDependencies())
                return;

            foreach (var plane in s_PlaneManager.trackables)
            {
                if (plane.alignment != PlaneAlignment.Vertical ||
                    plane.subsumedBy != null ||
                    plane.trackingState != TrackingState.Tracking)
                    continue;

                var normal = Vector3.ProjectOnPlane(plane.normal, Vector3.up);
                if (normal.sqrMagnitude < 0.5f)
                    continue;

                normal.Normalize();

                // The user stands in the room, so the side of the wall the camera is on is the room side.
                if (Vector3.Dot(s_Camera.position - plane.center, normal) < 0f)
                    normal = -normal;

                var wall = new Wall(plane.center, normal, plane);

                if (Mathf.Abs(wall.SignedDistance(position)) > maxDistance)
                    continue;

                if (!IsBesidePlane(plane, position))
                    continue;

                results.Add(wall);
            }
        }

        // True when position, projected onto the plane, falls within the plane's detected area plus the margin.
        static bool IsBesidePlane(ARPlane plane, Vector3 position)
        {
            var local = plane.transform.InverseTransformPoint(position);
            var offset = new Vector2(local.x, local.z) - plane.centerInPlaneSpace;
            var extents = plane.extents + new Vector2(lateralMargin, lateralMargin);
            return Mathf.Abs(offset.x) <= extents.x && Mathf.Abs(offset.y) <= extents.y;
        }

        static bool TryGetDependencies()
        {
            if (s_PlaneManager == null)
                s_PlaneManager = Object.FindAnyObjectByType<ARPlaneManager>();

            if (s_Camera == null && Camera.main != null)
                s_Camera = Camera.main.transform;

            return s_PlaneManager != null && s_Camera != null;
        }
    }
}
