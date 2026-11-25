using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MoveIt.AR
{
    /// <summary>
    /// The shared screen overlay for MoveIt's own controls, built in code so the scene doesn't need editing.
    /// It has a toolbar in the top-left corner (two rows) and helpers for consistently styled buttons, text and sliders.
    /// </summary>
    public static class MoveItUI
    {
        public static readonly Color panelColor = new Color(0.08f, 0.08f, 0.1f, 0.82f);
        public static readonly Color buttonColor = new Color(0.15f, 0.15f, 0.18f, 0.9f);
        public static readonly Color accentColor = new Color(0.25f, 0.75f, 1f, 1f);

        public const float fontSize = 30f;
        public const float buttonHeight = 88f;

        static Canvas s_Canvas;
        static RectTransform s_ToolbarTop;
        static RectTransform s_ToolbarBottom;

        /// <summary>
        /// The overlay canvas, created on first use. Reference resolution is 1080 x 1920 (portrait phone).
        /// </summary>
        public static Canvas canvas
        {
            get
            {
                if (s_Canvas == null)
                    Build();
                return s_Canvas;
            }
        }

        /// <summary>
        /// First toolbar row, in the top-left corner.
        /// </summary>
        public static RectTransform toolbarTop
        {
            get
            {
                if (s_Canvas == null)
                    Build();
                return s_ToolbarTop;
            }
        }

        /// <summary>
        /// Second toolbar row, under the first.
        /// </summary>
        public static RectTransform toolbarBottom
        {
            get
            {
                if (s_Canvas == null)
                    Build();
                return s_ToolbarBottom;
            }
        }

        static void Build()
        {
            var canvasObject = new GameObject("MoveIt UI", typeof(RectTransform));
            s_Canvas = canvasObject.AddComponent<Canvas>();
            s_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            s_Canvas.sortingOrder = 10;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();

            // Toolbar in the top-left corner, below the status bar.
            var toolbar = CreateRect("Toolbar", canvasObject.transform);
            toolbar.anchorMin = toolbar.anchorMax = toolbar.pivot = new Vector2(0f, 1f);
            toolbar.anchoredPosition = new Vector2(28f, -150f);
            var column = toolbar.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 14f;
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = column.childForceExpandHeight = false;
            var fitter = toolbar.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            s_ToolbarTop = CreateRow("Toolbar Top", toolbar);
            s_ToolbarBottom = CreateRow("Toolbar Bottom", toolbar);
        }

        static RectTransform CreateRow(string rowName, Transform parent)
        {
            var row = CreateRect(rowName, parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            return row;
        }

        /// <summary>
        /// A full-width bar under the toolbar for status and instructions (scanning, erasing), with text on the left.
        /// Buttons added to it go on the right.
        /// </summary>
        public static RectTransform CreateStatusBar(string barName, out TMP_Text text)
        {
            var bar = CreateRect(barName, canvas.transform);
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0.5f, 1f);
            // Below the toolbar (which starts 150 below the top and is one button high), with side margins.
            bar.anchoredPosition = new Vector2(0f, -150f - buttonHeight - 16f);
            bar.sizeDelta = new Vector2(-56f, 0f);
            bar.gameObject.AddComponent<Image>().color = panelColor;
            var layout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(24, 12, 10, 10);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            bar.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            text = CreateText("Text", bar, fontSize - 2f);
            text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return bar;
        }

        public static RectTransform CreateRect(string rectName, Transform parent)
        {
            var go = new GameObject(rectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>
        /// A rounded-looking dark panel with padding and a vertical layout.
        /// </summary>
        public static RectTransform CreatePanel(string panelName, Transform parent, int padding = 24, float spacing = 14f)
        {
            var panel = CreateRect(panelName, parent);
            panel.gameObject.AddComponent<Image>().color = panelColor;
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.spacing = spacing;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return panel;
        }

        public static TMP_Text CreateText(string textName, Transform parent, float size = fontSize)
        {
            var rect = CreateRect(textName, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.color = Color.white;
            text.textWrappingMode = TextWrappingModes.Normal;
            return text;
        }

        public static Button CreateButton(Transform parent, string label, UnityAction onClick, out TMP_Text text, float minWidth = 0f)
        {
            var go = TMP_DefaultControls.CreateButton(new TMP_DefaultControls.Resources());
            go.name = label;
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<LayoutElement>();
            layout.minWidth = minWidth;
            layout.preferredHeight = buttonHeight;
            // Only as wide as the label unless a caller makes it stretch.
            layout.flexibleWidth = 0f;

            go.GetComponent<Image>().color = buttonColor;
            text = go.GetComponentInChildren<TMP_Text>();
            text.text = label;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.margin = new Vector4(28f, 0f, 28f, 0f);
            text.textWrappingMode = TextWrappingModes.NoWrap;

            // Size the button to its label.
            var fit = go.AddComponent<HorizontalLayoutGroup>();
            fit.childAlignment = TextAnchor.MiddleCenter;
            fit.childControlWidth = fit.childControlHeight = true;
            fit.childForceExpandWidth = false;
            fit.childForceExpandHeight = true;

            var button = go.GetComponent<Button>();
            button.onClick.AddListener(onClick);
            return button;
        }

        public static Button CreateButton(Transform parent, string label, UnityAction onClick, float minWidth = 0f)
        {
            return CreateButton(parent, label, onClick, out _, minWidth);
        }

        public static Slider CreateSlider(string sliderName, Transform parent, float min, float max, UnityAction<float> onChanged)
        {
            var go = DefaultControls.CreateSlider(new DefaultControls.Resources());
            go.name = sliderName;
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = 56f;
            layout.flexibleWidth = 1f;

            var background = go.transform.Find("Background");
            if (background != null)
                background.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.25f);

            var fill = go.transform.Find("Fill Area/Fill");
            if (fill != null)
                fill.GetComponent<Image>().color = accentColor;

            var handle = go.transform.Find("Handle Slide Area/Handle");
            if (handle != null)
                ((RectTransform)handle).sizeDelta = new Vector2(56f, 0f);

            var slider = go.GetComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.onValueChanged.AddListener(onChanged);
            return slider;
        }
    }
}
