// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class ModularCopingPanel : BasePanel
    {
        [SerializeField] private TextActionButton m_ConcaveButton;
        [SerializeField] private TextActionButton m_ConvexButton;
        [SerializeField] private ActionButton m_DecreaseLengthButton;
        [SerializeField] private ActionButton m_IncreaseLengthButton;
        [SerializeField] private TextMeshPro m_LengthValue;
        [SerializeField] private TextMeshPro m_PlacementHint;

        private (bool Available, ModularCopingPiece Piece, int LengthPercent)? m_DisplayedSelection;

        private static FreePaintTool ActiveTool =>
            SketchSurfacePanel.m_Instance?.ActiveTool as FreePaintTool;

        public override void InitPanel()
        {
            base.InitPanel();
            UpdateSelection();
        }

        private void Update()
        {
            BaseUpdate();
            UpdateSelection();
        }

        public void SelectPiece(int selection)
        {
            var tool = ActiveTool;
            if (tool == null || !tool.UsesModularCoping)
                return;

            tool.SetModularCopingPiece((ModularCopingPiece)selection);
            UpdateSelection();
        }

        public void StepLength(int delta)
        {
            var tool = ActiveTool;
            if (tool != null && tool.UsesModularCoping)
                tool.StepModularCopingLength(delta);
        }

        private void UpdateSelection()
        {
            var tool = ActiveTool;
            bool available = tool != null && tool.UsesModularCoping;
            var piece = available ? tool.CurrentModularCopingPiece : default;
            int lengthPercent = available ? tool.ModularCopingLengthPercent : 100;
            var selection = (Available: available, Piece: piece, LengthPercent: lengthPercent);
            if (m_DisplayedSelection == selection)
                return;

            bool availabilityChanged = m_DisplayedSelection?.Available != available;
            if (availabilityChanged)
            {
                m_ConcaveButton.SetButtonAvailable(available);
                m_ConvexButton.SetButtonAvailable(available);
            }
            if (availabilityChanged || m_DisplayedSelection?.Piece != piece)
            {
                m_ConcaveButton.SetButtonSelected(available && piece == ModularCopingPiece.Concave);
                m_ConvexButton.SetButtonSelected(available && piece == ModularCopingPiece.Convex);
            }
            if (availabilityChanged || m_DisplayedSelection?.LengthPercent != lengthPercent)
            {
                m_DecreaseLengthButton.SetButtonAvailable(available && lengthPercent > 0);
                m_IncreaseLengthButton.SetButtonAvailable(available && lengthPercent < 100);
                m_LengthValue.text = $"{lengthPercent}%";
                m_PlacementHint.text = lengthPercent == 0
                    ? "Increase length to place"
                    : "Aim at a free joint to snap";
            }
            m_DisplayedSelection = selection;
        }
    }
}
