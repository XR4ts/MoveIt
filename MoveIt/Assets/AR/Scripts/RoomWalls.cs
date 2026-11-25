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
            foreach (var wall in AllWalls())
            {
                if (Mathf.Abs(wall.SignedDistance(position)) > maxDistance)
                    continue;

                if (!IsBesidePlane(wall.plane, position, lateralMargin))
                    continue;

                results.Add(wall);
            }
        }

        /// <summary>
        /// Adds every detected wall.
        /// </summary>
        public static void GetAll(List<Wall> results)
        {
            results.Clear();
            results.AddRange(AllWalls());
        }

        /// <summary>
        /// True when <paramref name="position"/> lies on one of <paramref name="walls"/>, within
        /// <paramref name="tolerance"/> metres of its surface and within <paramref name="lateralTolerance"/> metres
        /// of its detected area.
        /// </summary>
        public static bool IsOnWall(Vector3 position, List<Wall> walls, float tolerance, float lateralTolerance = 0.3f)
        {
            foreach (var wall in walls)
            {
                if (Mathf.Abs(wall.SignedDistance(position)) <= tolerance && IsBesidePlane(wall.plane, position, lateralTolerance))
                    return true;
            }

            return false;
        }

        static IEnumerable<Wall> AllWalls()
        {
            if (!TryGetDependencies())
                yield break;

            var hasFloor = RoomFloor.TryGetY(out var floorY);

            foreach (var plane in s_PlaneManager.trackables)
            {
                if (plane.alignment != PlaneAlignment.Vertical ||
                    plane.subsumedBy != null ||
                    plane.trackingState != TrackingState.Tracking)
                    continue;

                var normal = Vector3.ProjectOnPlane(plane.normal, Vector3.up);
                if (normal.sqrMagnitude < 0.5f)
                    continue;

                // Fronts and sides of furniture (dressers, beds, cabinets) are detected as vertical planes too.
                // A plane counts as a wall when it's taller than furniture usually is, or when it reaches higher up
                // than furniture does (on a phone, plain walls are often only detected as a patch at eye height).
                var extent = VerticalExtent(plane);
                var top = plane.center.y + 0.5f * extent;
                var reachesHigh = hasFloor && top - floorY >= minWallTop;
                if (extent < minWallHeight && !reachesHigh)
                    continue;

                normal.Normalize();

                // The user stands in the room, so the side of the wall the camera is on is the room side.
                if (Vector3.Dot(s_Camera.position - plane.center, normal) < 0f)
                    normal = -normal;

                yield return new Wall(plane.center, normal, plane);
            }
        }

        /// <summary>
        /// A vertical plane must reach at least this high, in metres, to count as a wall. Scan a little up
        /// the wall for it to qualify.
        /// </summary>
        public const float minWallHeight = 1.1f;

        /// <summary>
        /// A shorter vertical plane still counts as a wall when its top is at least this high above the floor.
        /// </summary>
        public const float minWallTop = 1.4f;

        // Height covered by the plane's detected area. Plane space has x and z in the plane; for a vertical
        // plane, one or both of them have a vertical component.
        static float VerticalExtent(ARPlane plane)
        {
            var t = plane.transform;
            return Mathf.Abs(t.right.y) * plane.size.x + Mathf.Abs(t.forward.y) * plane.size.y;
        }

        // True when position, projected onto the plane, falls within the plane's detected area plus the margin.
        static bool IsBesidePlane(ARPlane plane, Vector3 position, float margin)
        {
            var local = plane.transform.InverseTransformPoint(position);
            var offset = new Vector2(local.x, local.z) - plane.centerInPlaneSpace;
            var extents = plane.extents + new Vector2(margin, margin);
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
