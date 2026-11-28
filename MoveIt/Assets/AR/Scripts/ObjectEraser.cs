using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Interaction.Toolkit.Samples.ARStarterAssets;

namespace MoveIt.AR
{
    /// <summary>
    /// Lets the user erase real objects from the camera view.
    /// The user taps an object; <see cref="DepthObjectFitter"/> finds it in the depth image and fits a box around it,
    /// and everything inside the box is erased live (see <see cref="ErasedRegion"/>). If the fit is off, or depth
    /// isn't available, the user can drag, rotate and resize the box before tapping Done. What's shown is a clean,
    /// still fill (<see cref="EraseInpainter"/>), redone a moment after each change to the box.
    /// </summary>
    public class ObjectEraser : MonoBehaviour
    {
        enum State
        {
            Idle,
            PlaceBox,
            Measuring,
            Adjust,
        }

        const float k_MinSize = 0.1f;
        const float k_MaxSize = 3f;
        const float k_DefaultWidth = 0.8f;
        const float k_DefaultDepth = 0.6f;
        const float k_DefaultHeight = 1f;
        const float k_MaxDepthToWall = 1.2f;
        const float k_WallSearchDistance = 3f;
        const float k_WallContactDistance = 0.3f;
        // Distance of the stand-in backdrop behind a free-standing object. Far away, so view rays looking down
        // reach the real floor behind the object before it; only rays above the horizon end on it.
        const float k_BackdropDistance = 3f;
        const float k_RotateStep = 15f;
        const float k_TapSlop = 30f;
        // Wait this long after the last change to the box before inpainting it again.
        const float k_InpaintDelay = 0.4f;

        static readonly Color k_PreviewColor = new Color(1f, 0.85f, 0.2f, 0.95f);

        readonly List<ErasedRegion> m_Erased = new List<ErasedRegion>();
        readonly List<Wall> m_Walls = new List<Wall>();
        readonly List<RaycastResult> m_UIHits = new List<RaycastResult>();
        readonly List<Behaviour> m_DisabledSpawnTriggers = new List<Behaviour>();

        State m_State;
        Camera m_Camera;
        ARAnchorManager m_AnchorManager;
        DepthObjectFitter m_Fitter;
        EraseInpainter m_Inpainter;
        bool m_ShowingOriginal;
        bool m_HidingDots;

        // The box being edited: front-edge centre on the floor, yaw in degrees, and size (width, height, depth).
        ErasedRegion m_Live;
        Vector3 m_Front;
        float m_Yaw;
        Vector3 m_Size;
        float m_FloorY;
        float m_InpaintDue;
        string m_AdjustMessage;

        // Input
        Vector2 m_PressPosition;
        bool m_PressStartedOverUI;
        bool m_Dragging;
        Vector3 m_DragStartHit;
        Vector3 m_DragStartFront;
        bool m_Twisting;
        float m_TwistAngle;

        // UI
        Button m_EraseButton;
        Button m_UndoButton;
        Button m_ShowOriginalButton;
        TMP_Text m_ShowOriginalLabel;
        GameObject m_InstructionBar;
        TMP_Text m_Instructions;
        GameObject m_ProgressBar;
        GameObject m_AdjustCard;
        GameObject m_AdjustControls;
        TMP_Text m_AdjustToggleLabel;
        Slider m_WidthSlider;
        Slider m_DepthSlider;
        Slider m_HeightSlider;
        TMP_Text m_WidthLabel;
        TMP_Text m_DepthLabel;
        TMP_Text m_HeightLabel;

        // Outline of the box being edited
        GameObject m_Outline;
        LineRenderer m_BottomLoop;
        LineRenderer m_TopLoop;
        LineRenderer m_Uprights;
        static Material s_OutlineMaterial;

        void Awake()
        {
            m_Camera = Camera.main;
            m_AnchorManager = FindAnyObjectByType<ARAnchorManager>();

            if (m_AnchorManager == null)
            {
                // Anchors keep erased objects in place when ARCore refines its map of the room.
                var origin = FindAnyObjectByType<XROrigin>();
                if (origin != null)
                    m_AnchorManager = origin.gameObject.AddComponent<ARAnchorManager>();
            }

            m_Fitter = GetComponent<DepthObjectFitter>();
            if (m_Fitter == null)
                m_Fitter = gameObject.AddComponent<DepthObjectFitter>();

            m_Inpainter = GetComponent<EraseInpainter>();
            if (m_Inpainter == null)
                m_Inpainter = gameObject.AddComponent<EraseInpainter>();

            BuildUI();
            BuildOutline();
            SetState(State.Idle);
        }

        void Update()
        {
            if (m_State == State.PlaceBox)
                HandleTap();
            else if (m_State == State.Adjust)
            {
                HandleDragAndTwist();
                UpdatePreview();
            }

            var cleaning = m_State == State.Idle && isInpainting;
            if (m_ProgressBar.activeSelf != cleaning)
                m_ProgressBar.SetActive(cleaning);
        }

        void OnDestroy()
        {
            RestoreSpawning();
        }

        // Redoes the clean fill in the background once the user has stopped changing the box, so it's ready (or
        // nearly) when they tap Done. It isn't shown until then.
        void UpdatePreview()
        {
            if (m_Live == null)
                return;

            if (m_InpaintDue > 0f && Time.unscaledTime >= m_InpaintDue && !m_Dragging && !m_Twisting)
            {
                m_InpaintDue = 0f;
                m_Inpainter.Request(m_Live);
            }
        }

        // ----- Scripting (used by the test harness) -----

        /// <summary>True while a box is shown for the user to confirm or adjust.</summary>
        public bool isAdjusting => m_State == State.Adjust;

        /// <summary>True from tapping Erase object until Done or Cancel.</summary>
        public bool isEditing => m_State != State.Idle;

        /// <summary>True while any erased object is shown.</summary>
        public bool hasErasedObjects
        {
            get
            {
                foreach (var region in m_Erased)
                {
                    if (region != null && region.erased)
                        return true;
                }

                return false;
            }
        }

        /// <summary>True while waiting for the depth fit.</summary>
        public bool isMeasuring => m_State == State.Measuring;

        /// <summary>Whether a confirmed erase is still getting its clean fill.</summary>
        public bool isInpainting => m_Inpainter != null && m_Inpainter.busy;

        /// <summary>The box being edited, for logging: front-edge centre, yaw and size.</summary>
        public string boxDescription => $"front {m_Front}, yaw {m_Yaw:0}, size {m_Size}";

        /// <summary>Same as tapping Erase object and then tapping <paramref name="screenPosition"/>.</summary>
        public void EraseAt(Vector2 screenPosition)
        {
            SetState(State.PlaceBox);
            OnEraseTap(screenPosition);
        }

        /// <summary>Same as tapping Done.</summary>
        public void Confirm() => Done();

        /// <summary>Same as tapping Show original / Hide again.</summary>
        public void SetShowOriginal(bool show)
        {
            if (m_ShowingOriginal != show)
                ToggleShowOriginal();
        }

        // ----- Input -----

        void HandleTap()
        {
            var pointer = Pointer.current;
            if (pointer == null)
                return;

            var position = pointer.position.ReadValue();
            if (pointer.press.wasPressedThisFrame)
            {
                m_PressPosition = position;
                m_PressStartedOverUI = IsOverUI(position);
            }

            if (pointer.press.wasReleasedThisFrame && !m_PressStartedOverUI &&
                (position - m_PressPosition).sqrMagnitude < k_TapSlop * k_TapSlop)
            {
                OnEraseTap(position);
            }
        }

        // One finger (or the mouse) drags the box along the floor; two fingers twist it.
        void HandleDragAndTwist()
        {
            if (TryGetTwoTouches(out var a, out var b))
            {
                m_Dragging = false;
                var angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                if (m_Twisting)
                {
                    // Twisting counter-clockwise on screen turns the box counter-clockwise seen from above.
                    Rotate(-Mathf.DeltaAngle(m_TwistAngle, angle));
                }

                m_TwistAngle = angle;
                m_Twisting = true;
                return;
            }

            m_Twisting = false;

            var pointer = Pointer.current;
            if (pointer == null)
                return;

            var position = pointer.position.ReadValue();
            if (pointer.press.wasPressedThisFrame)
            {
                m_Dragging = !IsOverUI(position) && TryHitFloor(position, out m_DragStartHit);
                m_DragStartFront = m_Front;
            }

            if (!pointer.press.isPressed)
            {
                m_Dragging = false;
                return;
            }

            if (m_Dragging && TryHitFloor(position, out var hit))
            {
                m_Front = m_DragStartFront + (hit - m_DragStartHit);
                m_Front.y = m_FloorY;
                ApplyBox();
            }
        }

        static bool TryGetTwoTouches(out Vector2 a, out Vector2 b)
        {
            a = b = default;
            var touchscreen = Touchscreen.current;
            if (touchscreen == null)
                return false;

            var found = 0;
            foreach (var touch in touchscreen.touches)
            {
                if (!touch.isInProgress)
                    continue;

                if (found == 0)
                    a = touch.position.ReadValue();
                else
                    b = touch.position.ReadValue();

                if (++found == 2)
                    return true;
            }

            return false;
        }

        // ----- Box -----

        void OnEraseTap(Vector2 screenPosition)
        {
            if (!TryGetFloorY(out m_FloorY))
            {
                m_Instructions.text = "Scan the floor first: move your phone slowly over it.";
                return;
            }

            // Find the object in the depth image. On a phone the result comes straight away; in XR Simulation a
            // frame or two later. Either way OnFitted only acts while measuring, so switch state first.
            SetState(State.Measuring);
            StartCoroutine(FitWhenDepthReady(screenPosition));
        }

        // Depth is only captured while erasing, so right after Erase object is tapped it may not have arrived yet:
        // keep trying for a moment before falling back to a default box.
        System.Collections.IEnumerator FitWhenDepthReady(Vector2 screenPosition)
        {
            const float maxWait = 1f;
            var giveUp = Time.realtimeSinceStartup + maxWait;
            while (m_State == State.Measuring)
            {
                if (m_Fitter != null && m_Fitter.TryFit(screenPosition, m_FloorY, (ok, fit) => OnFitted(ok, fit, screenPosition)))
                    yield break;

                if (Time.realtimeSinceStartup > giveUp)
                {
                    PlaceDefaultBox(screenPosition);
                    yield break;
                }

                yield return null;
            }
        }

        void OnFitted(bool ok, DepthObjectFitter.Fit fit, Vector2 screenPosition)
        {
            // Cancelled while measuring.
            if (m_State != State.Measuring)
                return;

            if (!ok)
            {
                PlaceDefaultBox(screenPosition);
                return;
            }

            m_Front = fit.front;
            m_FloorY = fit.front.y;
            m_Yaw = fit.yaw;
            m_Size = ClampSize(fit.size);
            StartAdjusting(true);
        }

        // Without a depth fit: a typical furniture-sized box where the tap meets the floor, squared to the wall
        // behind it if there is one, for the user to fit by hand.
        void PlaceDefaultBox(Vector2 screenPosition)
        {
            if (!TryHitFloor(screenPosition, out var point))
            {
                SetState(State.PlaceBox);
                m_Instructions.text = "Couldn't find that object. Tap the object, or the <b>floor</b> right in front of it.";
                return;
            }

            m_Size = new Vector3(k_DefaultWidth, k_DefaultHeight, k_DefaultDepth);

            var awayFromUser = Horizontal(point - m_Camera.transform.position, m_Camera.transform.forward);
            var forward = awayFromUser;
            RoomWalls.FindNear(point, k_WallSearchDistance, m_Walls);
            var bestDistance = float.MaxValue;
            foreach (var wall in m_Walls)
            {
                var distance = wall.SignedDistance(point);
                if (Vector3.Dot(wall.normal, -awayFromUser) < 0.5f || distance < 0.05f || distance > bestDistance)
                    continue;

                bestDistance = distance;
                forward = -wall.normal;
            }

            // Furniture usually stands against the wall: reach the box back to it when it's close.
            if (bestDistance <= k_MaxDepthToWall)
                m_Size.z = Mathf.Max(bestDistance, 0.2f);

            m_Yaw = Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y;
            // The tap's ray usually meets the floor somewhere under the object, so centre the box there.
            m_Front = point - forward * (0.5f * m_Size.z);
            StartAdjusting(false);
        }

        void StartAdjusting(bool autoFitted)
        {
            m_WidthSlider.SetValueWithoutNotify(m_Size.x);
            m_HeightSlider.SetValueWithoutNotify(m_Size.y);
            m_DepthSlider.SetValueWithoutNotify(m_Size.z);
            UpdateSliderLabels();

            m_Live = null;
            ApplyBox();
            if (m_Live == null)
            {
                SetState(State.PlaceBox);
                m_Instructions.text = "Erasing isn't supported on this device.";
                return;
            }

            SetState(State.Adjust);
            SetAdjustControlsVisible(!autoFitted);
            m_AdjustMessage = autoFitted
                ? "Is the object inside the yellow box? Tap <b>Done</b> to remove it, or <b>Adjust</b> to fix the box."
                : "Couldn't measure it automatically. Drag, twist and resize the box until it covers the object, then tap <b>Done</b>.";
            m_Instructions.text = m_AdjustMessage;

            // Photo now, while the camera points at the object; the clean fill shows a moment later.
            m_InpaintDue = 0f;
            m_Inpainter.Request(m_Live);
        }

        static Vector3 ClampSize(Vector3 size)
        {
            return new Vector3(
                Mathf.Clamp(size.x, k_MinSize, k_MaxSize),
                Mathf.Clamp(size.y, k_MinSize, k_MaxSize),
                Mathf.Clamp(size.z, k_MinSize, k_MaxSize));
        }

        void Rotate(float degrees)
        {
            // Turn around the box centre, not the front edge.
            var center = m_Front + Forward() * (0.5f * m_Size.z);
            m_Yaw = Mathf.Repeat(m_Yaw + degrees, 360f);
            m_Front = center - Forward() * (0.5f * m_Size.z);
            ApplyBox();
        }

        void OnSliderChanged()
        {
            // Width and height grow around the middle; depth grows backwards from the front edge.
            m_Size = new Vector3(m_WidthSlider.value, m_HeightSlider.value, m_DepthSlider.value);
            UpdateSliderLabels();
            ApplyBox();
        }

        Vector3 Forward()
        {
            return Quaternion.Euler(0f, m_Yaw, 0f) * Vector3.forward;
        }

        // Pushes the current box to the live erased region and the outline.
        void ApplyBox()
        {
            var rotation = Quaternion.Euler(0f, m_Yaw, 0f);
            var bottomCenter = m_Front + rotation * Vector3.forward * (0.5f * m_Size.z);
            var walls = GetWalls(bottomCenter, rotation);

            var pose = new Pose(bottomCenter, rotation);
            if (m_Live == null)
            {
                m_Live = ErasedRegion.Create(pose, m_Size, walls);
                if (m_Live == null)
                    return;
            }
            else
            {
                m_Live.SetBox(pose, m_Size, walls);
            }

            // The real room stays visible while the box is placed and adjusted; Done removes the object.
            m_Live.erased = false;
            UpdateOutline(bottomCenter, rotation);
            if (m_State == State.Adjust)
                m_InpaintDue = Time.unscaledTime + k_InpaintDelay;
        }

        // The walls the erase fill copies from: up to two walls the box stands against (two in a corner), or a
        // stand-in backdrop just behind it (seen from the user) where there's no wall.
        EraseWalls GetWalls(Vector3 bottomCenter, Quaternion rotation)
        {
            var away = Horizontal(bottomCenter - m_Camera.transform.position, rotation * Vector3.forward);
            var maxExtent = 0.5f * Mathf.Max(m_Size.x, m_Size.z);
            RoomWalls.FindNear(bottomCenter, maxExtent + k_WallContactDistance, m_Walls);

            // Walls the box touches, closest first.
            m_Walls.RemoveAll(wall => Gap(wall, bottomCenter, rotation) > k_WallContactDistance);
            m_Walls.Sort((a, b) => Gap(a, bottomCenter, rotation).CompareTo(Gap(b, bottomCenter, rotation)));

            var backdropPoint = bottomCenter + away * (HalfExtentAlong(away, rotation) + k_BackdropDistance);
            var result = new EraseWalls { wallPoint = backdropPoint, wallNormal = -away };
            if (m_Walls.Count == 0)
                return result;

            var first = m_Walls[0];
            result.wallPoint = first.point;
            result.wallNormal = first.normal;

            // A second wall at roughly a right angle to the first: the object stands in a corner.
            foreach (var wall in m_Walls)
            {
                if (Mathf.Abs(Vector3.Dot(wall.normal, first.normal)) < 0.5f)
                {
                    result.hasSecondWall = true;
                    result.secondWallPoint = wall.point;
                    result.secondWallNormal = wall.normal;
                    return result;
                }
            }

            // Only a side wall: still use the backdrop behind the object.
            if (Vector3.Dot(first.normal, -away) < 0.5f)
            {
                result.hasSecondWall = true;
                result.secondWallPoint = backdropPoint;
                result.secondWallNormal = -away;
            }

            return result;
        }

        // Distance between the box's side facing the wall and the wall.
        float Gap(Wall wall, Vector3 bottomCenter, Quaternion rotation)
        {
            return Mathf.Abs(wall.SignedDistance(bottomCenter) - HalfExtentAlong(wall.normal, rotation));
        }

        float HalfExtentAlong(Vector3 direction, Quaternion rotation)
        {
            return 0.5f * (Mathf.Abs(Vector3.Dot(rotation * Vector3.right, direction)) * m_Size.x +
                Mathf.Abs(Vector3.Dot(rotation * Vector3.forward, direction)) * m_Size.z);
        }

        static Vector3 Horizontal(Vector3 direction, Vector3 fallback)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-4f)
            {
                direction = fallback;
                direction.y = 0f;
            }

            return direction.sqrMagnitude < 1e-4f ? Vector3.forward : direction.normalized;
        }

        void Done()
        {
            if (m_Live != null)
            {
                m_Live.erased = !m_ShowingOriginal;
                m_Erased.Add(m_Live);
                AnchorAsync(m_Live);
                // Redo the fill if the last change to the box hasn't been inpainted yet, then free the photo.
                if (m_InpaintDue > 0f)
                    m_Inpainter.Request(m_Live);
                m_InpaintDue = 0f;
                m_Inpainter.Finish(m_Live);
                m_Live = null;
            }

            SetState(State.Idle);
        }

        void Cancel()
        {
            if (m_Live != null)
            {
                Destroy(m_Live.gameObject);
                m_Live = null;
            }

            SetState(State.Idle);
        }

        async void AnchorAsync(ErasedRegion region)
        {
            if (m_AnchorManager == null || !m_AnchorManager.enabled)
                return;

            var pose = new Pose(region.transform.position, region.transform.rotation);
            var result = await m_AnchorManager.TryAddAnchorAsync(pose);
            if (region == null || !result.status.IsSuccess())
                return;

            region.transform.SetParent(result.value.transform, true);
            region.PinTo(result.value.transform);
        }

        void Undo()
        {
            if (m_Erased.Count == 0)
                return;

            var last = m_Erased[m_Erased.Count - 1];
            m_Erased.RemoveAt(m_Erased.Count - 1);
            if (last != null)
            {
                var anchor = last.GetComponentInParent<ARAnchor>();
                Destroy(last.gameObject);

                // The anchor manager destroys the anchor's GameObject once ARCore has removed it.
                if (anchor != null && m_AnchorManager != null && m_AnchorManager.enabled)
                    m_AnchorManager.TryRemoveAnchor(anchor);
            }

            UpdateIdleButtons();
            UpdateDepthCapture();
        }

        void ToggleShowOriginal()
        {
            m_ShowingOriginal = !m_ShowingOriginal;
            foreach (var region in m_Erased)
            {
                if (region != null)
                    region.erased = !m_ShowingOriginal;
            }

            UpdateIdleButtons();
            UpdateDepthCapture();
        }

        static bool TryGetFloorY(out float floorY)
        {
            return RoomFloor.TryGetY(out floorY);
        }

        bool TryHitFloor(Vector2 screenPosition, out Vector3 point)
        {
            var ray = m_Camera.ScreenPointToRay(screenPosition);
            var floor = new Plane(Vector3.up, new Vector3(0f, m_FloorY, 0f));
            if (floor.Raycast(ray, out var distance) && distance < 10f)
            {
                point = ray.GetPoint(distance);
                return true;
            }

            point = default;
            return false;
        }

        bool IsOverUI(Vector2 screenPosition)
        {
            if (EventSystem.current == null)
                return false;

            var data = new PointerEventData(EventSystem.current) { position = screenPosition };
            m_UIHits.Clear();
            EventSystem.current.RaycastAll(data, m_UIHits);
            return m_UIHits.Count > 0;
        }

        // ----- State -----

        void SetState(State state)
        {
            m_State = state;
            var editing = state != State.Idle;

            m_InstructionBar.SetActive(editing);
            m_AdjustCard.SetActive(state == State.Adjust);
            if (state != State.Adjust)
                SetAdjustControlsVisible(false);
            m_EraseButton.gameObject.SetActive(!editing);
            m_Outline.SetActive(state == State.Adjust);
            m_Dragging = m_Twisting = false;

            if (editing)
                DisableSpawning();
            else
                RestoreSpawning();

            UpdateDepthCapture();

            // The dots would show as a hole where the box is, so hide them while erasing.
            if (editing != m_HidingDots)
            {
                m_HidingDots = editing;
                if (editing)
                    SurfaceDots.BeginHide();
                else
                    SurfaceDots.EndHide();
            }

            if (state == State.PlaceBox)
                m_Instructions.text = "Tap the object you want to erase.";
            else if (state == State.Measuring)
                m_Instructions.text = "Finding the object\u2026";

            UpdateIdleButtons();
        }

        // Depth (for fitting, and for the erase fill's checks) and the screen copy the fill samples are only
        // needed while erasing or while erased objects are shown.
        void UpdateDepthCapture()
        {
            var shown = false;
            foreach (var region in m_Erased)
                shown |= region != null && region.erased;

            var needed = m_State != State.Idle || shown;
            if (m_Fitter != null)
                m_Fitter.captureDepth = needed;
            SetScreenCopy(needed);
        }

        void SetScreenCopy(bool on)
        {
            if (m_Camera == null)
                return;

            var data = m_Camera.GetUniversalAdditionalCameraData();
            if (data != null)
                data.requiresColorOption = on ? CameraOverrideOption.On : CameraOverrideOption.Off;
        }

        void UpdateIdleButtons()
        {
            var idle = m_State == State.Idle;
            var any = m_Erased.Count > 0;
            m_UndoButton.gameObject.SetActive(idle && any);
            m_ShowOriginalButton.gameObject.SetActive(idle && any);
            m_ShowOriginalLabel.text = m_ShowingOriginal ? "Hide again" : "Show original";
        }

        // Taps while erasing must not place furniture.
        void DisableSpawning()
        {
            foreach (var trigger in FindObjectsByType<ARInteractorSpawnTrigger>(FindObjectsSortMode.None))
            {
                if (!trigger.enabled)
                    continue;

                trigger.enabled = false;
                m_DisabledSpawnTriggers.Add(trigger);
            }
        }

        void RestoreSpawning()
        {
            foreach (var trigger in m_DisabledSpawnTriggers)
            {
                if (trigger != null)
                    trigger.enabled = true;
            }

            m_DisabledSpawnTriggers.Clear();
        }

        // ----- Outline -----

        void BuildOutline()
        {
            if (s_OutlineMaterial == null)
            {
                // Drawn after the erase fill (Transparent queue), so the fill doesn't paint over the outline.
                s_OutlineMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
            }

            m_Outline = new GameObject("Erase Outline");
            m_Outline.transform.SetParent(transform, false);
            m_BottomLoop = CreateLine("Bottom", true);
            m_TopLoop = CreateLine("Top", true);
            m_Uprights = CreateLine("Uprights", false);
        }

        LineRenderer CreateLine(string lineName, bool loop)
        {
            var go = new GameObject(lineName);
            go.transform.SetParent(m_Outline.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.widthMultiplier = 0.01f;
            line.sharedMaterial = s_OutlineMaterial;
            line.startColor = k_PreviewColor;
            line.endColor = k_PreviewColor;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        void UpdateOutline(Vector3 bottomCenter, Quaternion rotation)
        {
            var right = rotation * Vector3.right * (0.5f * m_Size.x);
            var forward = rotation * Vector3.forward * (0.5f * m_Size.z);
            var up = Vector3.up * m_Size.y;

            var b0 = bottomCenter - right - forward;
            var b1 = bottomCenter + right - forward;
            var b2 = bottomCenter + right + forward;
            var b3 = bottomCenter - right + forward;

            m_BottomLoop.positionCount = 4;
            m_BottomLoop.SetPositions(new[] { b0, b1, b2, b3 });
            m_TopLoop.positionCount = 4;
            m_TopLoop.SetPositions(new[] { b0 + up, b1 + up, b2 + up, b3 + up });
            m_Uprights.positionCount = 8;
            m_Uprights.SetPositions(new[] { b0, b0 + up, b1 + up, b1, b2, b2 + up, b3 + up, b3 });
        }

        void UpdateSliderLabels()
        {
            m_WidthLabel.text = $"Width {m_WidthSlider.value * 100f:0} cm";
            m_DepthLabel.text = $"Depth {m_DepthSlider.value * 100f:0} cm";
            m_HeightLabel.text = $"Height {m_HeightSlider.value * 100f:0} cm";
        }

        // ----- UI -----

        void BuildUI()
        {
            m_EraseButton = MoveItUI.CreateButton(MoveItUI.toolbarTop, "Erase object", () => SetState(State.PlaceBox));
            m_UndoButton = MoveItUI.CreateButton(MoveItUI.toolbarBottom, "Undo erase", Undo);
            m_ShowOriginalButton = MoveItUI.CreateButton(MoveItUI.toolbarBottom, "Show original", ToggleShowOriginal, out m_ShowOriginalLabel);

            var root = MoveItUI.canvas.transform;

            var bar = MoveItUI.CreateStatusBar("Erase Instructions", out m_Instructions);
            MoveItUI.CreateButton(bar, "Cancel", Cancel);
            m_InstructionBar = bar.gameObject;

            var progress = MoveItUI.CreateStatusBar("Erase Progress", out var progressText);
            progressText.text = "Removing it\u2026";
            m_ProgressBar = progress.gameObject;
            m_ProgressBar.SetActive(false);

            // Compact card at the bottom while adjusting the box.
            var card = MoveItUI.CreatePanel("Erase Adjust", root, 20, 8);
            card.anchorMin = new Vector2(0f, 0f);
            card.anchorMax = new Vector2(1f, 0f);
            card.pivot = new Vector2(0.5f, 0f);
            card.anchoredPosition = new Vector2(0f, 24f);
            card.sizeDelta = new Vector2(-48f, 0f);

            // Manual controls, folded away unless the user asks for them.
            var controls = MoveItUI.CreateRect("Adjust Controls", card);
            var controlsLayout = controls.gameObject.AddComponent<VerticalLayoutGroup>();
            controlsLayout.spacing = 8f;
            controlsLayout.childControlWidth = controlsLayout.childControlHeight = true;
            controlsLayout.childForceExpandWidth = true;
            controlsLayout.childForceExpandHeight = false;
            m_WidthSlider = CreateSliderRow(controls, "Width", out m_WidthLabel);
            m_DepthSlider = CreateSliderRow(controls, "Depth", out m_DepthLabel);
            m_HeightSlider = CreateSliderRow(controls, "Height", out m_HeightLabel);
            var rotateRow = CreateButtonRow(controls, "Rotate");
            MoveItUI.CreateButton(rotateRow, "Rotate -15°", () => Rotate(-k_RotateStep)).GetComponent<LayoutElement>().flexibleWidth = 1f;
            MoveItUI.CreateButton(rotateRow, "Rotate +15°", () => Rotate(k_RotateStep)).GetComponent<LayoutElement>().flexibleWidth = 1f;
            m_AdjustControls = controls.gameObject;

            // Adjust on the left, Done on the right, both normal-sized.
            var buttons = CreateButtonRow(card, "Buttons");
            MoveItUI.CreateButton(buttons, "Adjust", ToggleAdjustControls, out m_AdjustToggleLabel, 200f);
            MoveItUI.CreateRect("Spacer", buttons).gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            MoveItUI.CreateButton(buttons, "Done", Done, 240f).GetComponent<Image>().color = MoveItUI.accentColor;

            m_AdjustCard = card.gameObject;
        }

        static RectTransform CreateButtonRow(Transform parent, string rowName)
        {
            var row = MoveItUI.CreateRect(rowName, parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 16f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            return row;
        }

        void ToggleAdjustControls()
        {
            SetAdjustControlsVisible(!m_AdjustControls.activeSelf);
        }

        void SetAdjustControlsVisible(bool visible)
        {
            m_AdjustControls.SetActive(visible);
            m_AdjustToggleLabel.text = visible ? "Hide controls" : "Adjust";
            if (visible && m_State == State.Adjust)
                m_Instructions.text = "Drag to move, twist to rotate, and resize until the box covers the object.";
        }

        // A label on the left and a slider filling the rest of the row.
        Slider CreateSliderRow(Transform parent, string rowName, out TMP_Text label)
        {
            var row = MoveItUI.CreateRect(rowName, parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 20f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            label = MoveItUI.CreateText("Label", row);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            var labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.preferredWidth = 250f;
            labelLayout.minHeight = 60f;

            return MoveItUI.CreateSlider(rowName + " Slider", row, k_MinSize, k_MaxSize, _ => OnSliderChanged());
        }
    }
}
