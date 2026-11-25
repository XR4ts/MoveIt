using System.Collections.Generic;
using MoveIt.Furniture;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.AR;

namespace MoveIt.AR
{
    /// <summary>
    /// Guides room scanning and decides when the AR template's dotted surface visuals show.
    /// While scanning, a bar at the top shows how much floor and how many walls have been found, and the dots stay on
    /// so the user sees what's covered, until they tap Done scanning. After that the dots only show while a piece of
    /// furniture is held, so the room looks clean. Scan room in the toolbar starts scanning again.
    /// While erasing the dots are always hidden: the erase fill can't copy them (they're drawn after the camera image
    /// it samples), so an erased box would show as a hole in the dots.
    /// </summary>
    public class SurfaceDots : MonoBehaviour
    {
        const float k_GoodFloorArea = 3f;
        const int k_GoodWallCount = 2;
        const float k_FloorBand = 0.1f;
        const float k_StatsInterval = 0.25f;

        static ARTemplateMenuManager s_Menu;
        static int s_HideRequests;

        readonly List<Wall> m_Walls = new List<Wall>();

        ARPlaneManager m_PlaneManager;
        bool m_Scanning = true;
        bool m_Holding;
        float m_NextStats;
        bool? m_LastWanted;

        GameObject m_ScanBar;
        TMP_Text m_ScanText;
        Button m_ScanButton;

        /// <summary>
        /// True while the user is scanning the room.
        /// </summary>
        public bool scanning => m_Scanning;

        /// <summary>
        /// Hides the dots (and the scanning bar) until <see cref="EndHide"/>, e.g. while erasing.
        /// </summary>
        public static void BeginHide()
        {
            s_HideRequests++;
        }

        /// <summary>
        /// Ends a <see cref="BeginHide"/>.
        /// </summary>
        public static void EndHide()
        {
            s_HideRequests = Mathf.Max(0, s_HideRequests - 1);
        }

        // Statics survive between Play mode sessions when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Menu = null;
            s_HideRequests = 0;
        }

        void Start()
        {
            m_PlaneManager = FindAnyObjectByType<ARPlaneManager>();
            s_Menu = FindAnyObjectByType<ARTemplateMenuManager>(FindObjectsInactive.Include);
            BuildUI();
            UpdateScanUI();
        }

        void Update()
        {
            if (Time.time >= m_NextStats)
            {
                m_NextStats = Time.time + k_StatsInterval;
                m_Holding = IsAnyFurnitureHeld();
                if (m_Scanning)
                    UpdateScanText();
            }

            var hidden = s_HideRequests > 0;
            if (m_ScanBar.activeSelf != (m_Scanning && !hidden))
                m_ScanBar.SetActive(m_Scanning && !hidden);
            if (m_ScanButton.gameObject.activeSelf != (!m_Scanning && !hidden))
                m_ScanButton.gameObject.SetActive(!m_Scanning && !hidden);

            if (s_Menu == null)
                return;

            var wanted = !hidden && (m_Scanning || m_Holding);

            // Only act on changes, so the options menu toggle isn't overridden every frame.
            if (m_LastWanted == wanted)
                return;

            m_LastWanted = wanted;
            s_Menu.visualizePlanes = wanted;
        }

        /// <summary>
        /// Starts or finishes scanning.
        /// </summary>
        public void SetScanning(bool value)
        {
            m_Scanning = value;
            UpdateScanUI();
        }

        void UpdateScanUI()
        {
            m_ScanBar.SetActive(m_Scanning);
            m_ScanButton.gameObject.SetActive(!m_Scanning);
            if (m_Scanning)
                UpdateScanText();
        }

        // Scanning progress: floor area found at floor height and number of walls.
        void UpdateScanText()
        {
            var floorArea = 0f;
            if (m_PlaneManager != null && RoomFloor.TryGetY(out var floorY))
            {
                foreach (var plane in m_PlaneManager.trackables)
                {
                    if (plane.alignment == PlaneAlignment.HorizontalUp && plane.subsumedBy == null &&
                        Mathf.Abs(plane.center.y - floorY) < k_FloorBand)
                        floorArea += plane.size.x * plane.size.y;
                }
            }

            RoomWalls.GetAll(m_Walls);
            var walls = m_Walls.Count;

            var progress = $"Floor {floorArea:0.0} m²  ·  {walls} wall{(walls == 1 ? "" : "s")}";
            string hint;
            if (floorArea < 0.5f)
                hint = "Point at the floor and move slowly. Good light and patterned surfaces help.";
            else if (walls == 0)
                hint = "Now point at a wall at eye height and move slowly sideways.";
            else if (floorArea < k_GoodFloorArea || walls < k_GoodWallCount)
                hint = "Keep walking around to cover more of the room.";
            else
                hint = "Looks good. Tap <b>Done</b> when you're ready.";

            m_ScanText.text = $"<b>Scanning</b>  {progress}\n<size=85%>{hint}</size>";
        }

        static bool IsAnyFurnitureHeld()
        {
            foreach (var item in FindObjectsByType<FurnitureItem>(FindObjectsSortMode.None))
            {
                var grab = item.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected)
                    return true;
            }

            return false;
        }

        void BuildUI()
        {
            m_ScanButton = MoveItUI.CreateButton(MoveItUI.toolbarTop, "Scan room", () => SetScanning(true));

            var bar = MoveItUI.CreateStatusBar("Scan Bar", out m_ScanText);
            MoveItUI.CreateButton(bar, "Done", () => SetScanning(false)).GetComponent<Image>().color = MoveItUI.accentColor;
            m_ScanBar = bar.gameObject;
        }
    }
}
