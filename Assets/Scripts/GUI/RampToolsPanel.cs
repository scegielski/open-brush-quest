// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using OpenBrush.Multiplayer;

namespace TiltBrush
{
    public sealed class RampToolsPanel : BasePanel
    {
        public void StartDrawing()
        {
            var surface = SketchSurfacePanel.m_Instance;
            if (surface == null || !App.Instance.IsInStateThatAllowsPainting() ||
                MultiplayerManager.m_Instance.IsViewOnly)
                return;

            surface.RequestHideActiveTool(true);
            surface.EnableSpecificTool(BaseTool.ToolType.FreePaintTool);
            if (surface.ActiveTool is FreePaintTool brush)
                brush.StartDrawing();
        }
    }
}
