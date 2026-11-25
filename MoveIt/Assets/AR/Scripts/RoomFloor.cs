using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MoveIt.AR
{
    /// <summary>
    /// Finds the floor height from the detected horizontal planes.
    /// </summary>
    public static class RoomFloor
    {
        // Planes smaller than this are often noise or the tops of small objects.
        const float k_MinArea = 0.25f;
        const float k_MinBelowCamera = 0.5f;

        static ARPlaneManager s_PlaneManager;

        /// <summary>
        /// The height of the floor: the lowest large upward-facing plane, or the lowest one when none is large yet.
        /// Tables and beds are detected as planes too, but they are higher than the floor.
        /// </summary>
        public static bool TryGetY(out float floorY)
        {
            floorY = 0f;
            if (s_PlaneManager == null)
                s_PlaneManager = Object.FindAnyObjectByType<ARPlaneManager>();
            if (s_PlaneManager == null)
                return false;

            // The floor is well below the phone; this keeps desks and worktops at chest height out.
            var maxY = Camera.main != null ? Camera.main.transform.position.y - k_MinBelowCamera : float.MaxValue;

            var found = false;
            var lowestAny = float.MaxValue;
            var lowestLarge = float.MaxValue;
            foreach (var plane in s_PlaneManager.trackables)
            {
                if (plane.alignment != PlaneAlignment.HorizontalUp || plane.subsumedBy != null || plane.center.y > maxY)
                    continue;

                var y = plane.center.y;
                lowestAny = Mathf.Min(lowestAny, y);
                if (plane.size.x * plane.size.y >= k_MinArea)
                    lowestLarge = Mathf.Min(lowestLarge, y);
                found = true;
            }

            floorY = lowestLarge < float.MaxValue ? lowestLarge : lowestAny;
            return found;
        }
    }
}
