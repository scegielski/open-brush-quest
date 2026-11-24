// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class TransitionSettingsPanel : BasePanel
    {
        [SerializeField] private TextMeshPro m_ContextLabel;
        [SerializeField] private TextMeshPro m_RadiusValue;
        [SerializeField] private TextMeshPro m_DeckDepthValue;
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

            settings.RadiusHalfInches += direction;
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
            if (previous?.IsSelection != settings.IsSelection)
                m_ContextLabel.text = settings.IsSelection ? "Selected Transition" : "New Transition";
            if (previous?.RadiusHalfInches != settings.RadiusHalfInches)
                m_RadiusValue.text = TransitionBrush.FormatFeetAndInches(settings.RadiusHalfInches * 0.5f);
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

        private static void SelectOption(TextActionButton[] buttons, int selected)
        {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].SetButtonSelected(i == selected);
        }
    }
}
