// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class SplinePanel : BasePanel
    {
        // Listed in SplineAction order so the prefab and the button states use the same actions.
        [SerializeField] private TextActionButton[] m_Actions;

        private (bool Available, bool Selected)[] m_ButtonStates;
        private TextMeshPro m_LoopLabel;
        private TextMeshPro m_AutoOrientLabel;

        private static FreePaintTool ActiveTool =>
            SketchSurfacePanel.m_Instance?.ActiveTool as FreePaintTool;

        public override void InitPanel()
        {
            base.InitPanel();
            m_ButtonStates = new (bool, bool)[m_Actions.Length];
            for (int i = 0; i < m_Actions.Length; i++)
                m_Actions[i].SetButtonAvailable(false);
            m_LoopLabel = m_Actions[(int)FreePaintTool.SplineAction.ToggleLoop].GetComponentInChildren<TextMeshPro>();
            m_AutoOrientLabel = m_Actions[(int)FreePaintTool.SplineAction.ToggleAutoOrient].GetComponentInChildren<TextMeshPro>();
        }

        private void Update()
        {
            BaseUpdate();
            if (m_ButtonStates == null)
                return;

            var tool = ActiveTool;
            for (int i = 0; i < m_Actions.Length; i++)
            {
                var action = (FreePaintTool.SplineAction)i;
                bool available = tool != null && tool.IsSplineActionAvailable(action);
                var state = (Available: available,
                    Selected: available && tool.IsSplineActionSelected(action));
                if (state == m_ButtonStates[i])
                    continue;
                if (state.Available != m_ButtonStates[i].Available)
                    m_Actions[i].SetButtonAvailable(state.Available);
                m_Actions[i].SetButtonSelected(state.Selected);
                m_ButtonStates[i] = state;
            }

            string loopLabel = tool != null && tool.SplineLoopIsClosed ? "Loop: Closed" : "Loop: Open";
            if (m_LoopLabel.text != loopLabel)
                m_LoopLabel.text = loopLabel;

            string autoOrientLabel = tool != null && tool.SplineAutoOrientEnabled ? "Auto Orient: On" : "Auto Orient: Off";
            if (m_AutoOrientLabel.text != autoOrientLabel)
                m_AutoOrientLabel.text = autoOrientLabel;
        }

        public void ChooseAction(int action)
        {
            ActiveTool?.ChooseSplineAction((FreePaintTool.SplineAction)action);
        }
    }
}
