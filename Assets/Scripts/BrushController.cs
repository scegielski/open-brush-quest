// Copyright 2020 The Tilt Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using UnityEngine;

namespace TiltBrush
{

    // Brush Controller is an unfortunate name for this class, as it refers to the MVC "Controller"
    // for storing information about the app state regarding the currently active brush.  It also
    // maintains actions for objects to register with for status change notifications.
    public class BrushController : MonoBehaviour
    {
        static public BrushController m_Instance;

        public event Action<Stroke> StrokeSelected;
        public event Action<BrushDescriptor> BrushChanged;
        public event Action BrushSetToDefault;

        private BrushDescriptor m_ActiveBrush;

        public BrushDescriptor ActiveBrush { get { return m_ActiveBrush; } }

        void Awake()
        {
            m_Instance = this;
        }

        public void SetActiveBrush(BrushDescriptor brush)
        {
            var pointers = PointerManager.m_Instance;
            var sketchSurface = SketchSurfacePanel.m_Instance;
            if (pointers.MainPointer.CurrentBrush != brush &&
                sketchSurface.GetToolOfType(BaseTool.ToolType.FreePaintTool) is FreePaintTool freePaint &&
                !freePaint.TryFinishSplineForBrushChange(brush))
                return;

            pointers.SetBrushForAllPointers(brush, finishSpline: false);
            AudioClip buttonAudio = BrushCatalog.m_Instance.GetBrush(brush.m_Guid).m_ButtonAudio;
            if (buttonAudio != null)
            {
                AudioManager.m_Instance.TriggerOneShot(buttonAudio,
                    SketchControlsScript.m_Instance.GetUIReticlePos(), 1.0f);
            }
            m_ActiveBrush = brush;

            // Reset our tool when the user picks a new brush unless it is repainting.
            if (sketchSurface.ActiveTool is RepaintTool repaint)
            {
                // The trigger that picks a brush from the palette must not repaint the sketch.
                repaint.EatInput();
            }
            else if (sketchSurface.GetCurrentToolType() != BaseTool.ToolType.ScriptedTool &&
                sketchSurface.m_LastCommand?.GetType() != typeof(RepaintStrokeCommand))
            {
                sketchSurface.EnableDefaultTool();
            }

            if (BrushChanged != null)
            {
                BrushChanged(brush);
            }
        }

        internal bool SetActiveBrushFromSpline(BrushDescriptor brush)
        {
            if (brush == null)
                return false;

            var pointers = PointerManager.m_Instance;
            bool pointerBrushChanged = pointers.MainPointer.CurrentBrush != brush;
            if (pointerBrushChanged)
                pointers.SetBrushForAllPointers(brush, finishSpline: false);

            if (pointerBrushChanged || m_ActiveBrush != brush)
            {
                m_ActiveBrush = brush;
                BrushChanged?.Invoke(brush);
            }

            return pointerBrushChanged;
        }

        public void SetBrushToDefault()
        {
            m_ActiveBrush = BrushCatalog.m_Instance.DefaultBrush;

            if (BrushSetToDefault != null)
            {
                BrushSetToDefault();
            }
        }

        public void TriggerStrokeSelected(Stroke stroke)
        {
            if (stroke != null)
            {
                m_ActiveBrush = BrushCatalog.m_Instance.GetBrush(stroke.m_BrushGuid);
            }

            if (StrokeSelected != null)
            {
                StrokeSelected(stroke);
            }
        }
    }
} // namespace TiltBrush
