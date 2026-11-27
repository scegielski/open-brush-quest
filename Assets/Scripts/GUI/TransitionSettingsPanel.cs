// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class TransitionSettingsPanel : BasePanel
    {
        [SerializeField] private TextMeshPro m_ContextLabel;
        [SerializeField] private TextMeshPro m_RadiusLabel;
        [SerializeField] private TextMeshPro m_RadiusValue;
        [SerializeField] private TextMeshPro m_DeckDepthValue;
        [SerializeField] private TextMeshPro m_SteelDiameterValue;
        [SerializeField] private Transform m_PrimaryControls;
        [SerializeField] private Transform m_RampControls;
        [SerializeField] private GameObject m_SteelDiameterControls;
        [SerializeField] private TextActionButton[] m_CopingOptions;
        [SerializeField] private TextActionButton[] m_TileOptions;
        [SerializeField] private TextActionButton[] m_DeckOptions;

        private FreePaintTool.TransitionSettings? m_DisplayedSettings;

        private static FreePaintTool ActiveTool =>
            SketchSurfacePanel.m_Instance?.ActiveTool as FreePaintTool;

        public override void InitPanel()
        {
            base.InitPanel();
            UpdateReadouts();
        }

        public void StepRadius(int direction)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings))
                return;

            if (settings.IsStandaloneSteel)
                settings.SteelDiameterHalfInches += direction;
            else
                settings.RadiusHalfInches += direction;
            tool.SetTransitionSettings(settings);
            UpdateReadouts();
        }

        public void StepSteelCopingDiameter(int direction)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings) ||
                settings.IsStandaloneSteel || settings.Options.Coping != TransitionBrush.CopingKind.Steel)
                return;

            settings.SteelDiameterHalfInches += direction;
            tool.SetTransitionSettings(settings);
            UpdateReadouts();
        }

        public void StepDeckDepth(int direction)
        {
            ActiveTool?.StepTransitionDeckDepth(direction);
            UpdateReadouts();
        }

        public void SetCoping(int selection)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings))
                return;

            settings.Options = new TransitionBrush.SurfaceOptions(
                (TransitionBrush.CopingKind)selection, settings.Options.Tile, settings.Options.Deck);
            tool.SetTransitionSettings(settings);
            UpdateReadouts();
        }

        public void SetTile(int enabled)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings))
                return;

            settings.Options = new TransitionBrush.SurfaceOptions(
                settings.Options.Coping, enabled != 0, settings.Options.Deck);
            tool.SetTransitionSettings(settings);
            UpdateReadouts();
        }

        public void SetDeck(int enabled)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings))
                return;

            settings.Options = new TransitionBrush.SurfaceOptions(
                settings.Options.Coping, settings.Options.Tile, enabled != 0);
            tool.SetTransitionSettings(settings);
            UpdateReadouts();
        }

        private void Update()
        {
            BaseUpdate();
            UpdateReadouts();
        }

        private void UpdateReadouts()
        {
            var tool = ActiveTool;
            if (tool == null || !tool.TryGetTransitionSettings(out var settings))
                return;

            var previous = m_DisplayedSettings;
            bool standalone = settings.IsStandaloneSteel;
            bool typeChanged = previous?.IsStandaloneSteel != standalone;
            bool showsSteelDiameter = !standalone && settings.Options.Coping == TransitionBrush.CopingKind.Steel;
            if (typeChanged || previous?.Options.Coping != settings.Options.Coping)
                ApplyLayout(standalone, showsSteelDiameter);
            if (typeChanged || previous?.IsSelection != settings.IsSelection)
            {
                m_ContextLabel.text = standalone
                    ? (settings.IsSelection ? "Selected Steel Coping" : "Steel Coping")
                    : (settings.IsSelection ? "Selected Transition" : "New Transition");
            }
            if (typeChanged)
                m_RadiusLabel.text = standalone ? "Diameter" : "Radius";
            if (typeChanged || previous?.RadiusHalfInches != settings.RadiusHalfInches ||
                previous?.SteelDiameterHalfInches != settings.SteelDiameterHalfInches)
            {
                int primaryDimension = standalone ? settings.SteelDiameterHalfInches : settings.RadiusHalfInches;
                m_RadiusValue.text = TransitionBrush.FormatFeetAndInches(primaryDimension * 0.5f);
                m_SteelDiameterValue.text =
                    TransitionBrush.FormatFeetAndInches(settings.SteelDiameterHalfInches * 0.5f);
            }
            if (previous?.DeckDepthHalfInches != settings.DeckDepthHalfInches ||
                previous?.MixedDeckDepth != settings.MixedDeckDepth)
            {
                m_DeckDepthValue.text = settings.MixedDeckDepth
                    ? "Mixed" : TransitionBrush.FormatFeetAndInches(settings.DeckDepthHalfInches * 0.5f);
            }
            if (previous?.Options.Coping != settings.Options.Coping)
                SelectOption(m_CopingOptions, (int)settings.Options.Coping);
            if (previous?.Options.Tile != settings.Options.Tile)
                SelectOption(m_TileOptions, settings.Options.Tile ? 1 : 0);
            if (previous?.Options.Deck != settings.Options.Deck)
                SelectOption(m_DeckOptions, settings.Options.Deck ? 1 : 0);

            m_DisplayedSettings = settings;
        }

        private void ApplyLayout(bool standalone, bool showsSteelDiameter)
        {
            const float halfRowSpacing = 0.235f;
            float expandedOffset = showsSteelDiameter ? halfRowSpacing : 0f;
            m_PrimaryControls.localPosition = Vector3.up * (standalone ? -0.9f : expandedOffset);
            m_RampControls.localPosition = Vector3.down * expandedOffset;
            m_RampControls.gameObject.SetActive(!standalone);
            m_SteelDiameterControls.SetActive(showsSteelDiameter);
        }

        private static void SelectOption(TextActionButton[] buttons, int selected)
        {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].SetButtonSelected(i == selected);
        }
    }
}
