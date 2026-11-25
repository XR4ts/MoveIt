using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.AR;

namespace MoveIt.AR
{
    /// <summary>
    /// Replaces the AR template's step-by-step onboarding (whose "Tap to place" card stays on screen until something
    /// is placed) with help the user opens when they want it: a "?" button in the toolbar shows a short card with
    /// all gestures. Scanning guidance is shown by <see cref="SurfaceDots"/>.
    /// </summary>
    public class HelpOverlay : MonoBehaviour
    {
        const string k_HelpText =
            "<b>Scan</b>  Move your phone slowly over the floor and walls until they're covered in dots, then tap <b>Done scanning</b>. " +
            "<b>Scan room</b> scans again.\n\n" +
            "<b>Place</b>  Tap <b>+</b>, pick a piece of furniture, then tap the floor.\n\n" +
            "<b>Move / rotate</b>  Drag an item to move it. Twist with two fingers to rotate it.\n\n" +
            "<b>Walls</b>  Let go near a wall and the item snaps flush against it. " +
            "The outline under it is <color=#40BFFF>blue</color> when it will snap and <color=#FF5040>red</color> when it doesn't fit.\n\n" +
            "<b>Delete</b>  Tap an item to select it, then tap the trash button.\n\n" +
            "<b>Erase a real object</b>  Tap <b>Erase object</b>, then tap the object. It's measured and erased " +
            "automatically. If part of it still shows, tap <b>Adjust</b> to move, rotate or resize the yellow box.";

        GameObject m_HelpCard;

        void Start()
        {
            SkipTemplateOnboarding();
            BuildUI();
        }

        /// <summary>
        /// Shows or hides the help card.
        /// </summary>
        public void ToggleHelp()
        {
            m_HelpCard.SetActive(!m_HelpCard.activeSelf);
        }

        // Does what the template's greeting "Continue" button does, without starting the coaching cards.
        static void SkipTemplateOnboarding()
        {
            var goals = FindAnyObjectByType<GoalManager>();
            if (goals == null)
                return;

            if (goals.greetingPrompt != null)
                goals.greetingPrompt.SetActive(false);

            foreach (var step in goals.stepList)
            {
                if (step.stepObject != null)
                    step.stepObject.SetActive(false);
            }

            if (goals.optionsButton != null)
                goals.optionsButton.SetActive(true);

            if (goals.createButton != null)
                goals.createButton.SetActive(true);

            if (goals.menuManager != null)
                goals.menuManager.enabled = true;
        }

        void BuildUI()
        {
            var help = MoveItUI.CreateButton(MoveItUI.toolbarTop, "?", ToggleHelp, 88f);
            help.transform.SetAsFirstSibling();

            var root = MoveItUI.canvas.transform;

            // Help card in the middle of the screen; tapping the dimmed background closes it.
            // The card is a sibling of the background, not a child, so taps on the card don't close it.
            var container = MoveItUI.CreateRect("Help", root);
            container.anchorMin = Vector2.zero;
            container.anchorMax = Vector2.one;
            container.offsetMin = container.offsetMax = Vector2.zero;

            var dim = MoveItUI.CreateRect("Background", container);
            dim.anchorMin = Vector2.zero;
            dim.anchorMax = Vector2.one;
            dim.offsetMin = dim.offsetMax = Vector2.zero;
            dim.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
            dim.gameObject.AddComponent<Button>().onClick.AddListener(ToggleHelp);

            var card = MoveItUI.CreatePanel("Help Card", container, 36, 24);
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(940f, 0f);

            var title = MoveItUI.CreateText("Title", card, 40f);
            title.text = "<b>How to use MoveIt</b>";
            var body = MoveItUI.CreateText("Body", card);
            body.text = k_HelpText;
            MoveItUI.CreateButton(card, "Got it", ToggleHelp).GetComponent<Image>().color = MoveItUI.accentColor;

            m_HelpCard = container.gameObject;
            m_HelpCard.SetActive(false);
        }
    }
}
