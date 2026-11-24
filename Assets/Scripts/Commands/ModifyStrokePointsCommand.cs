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

using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    public class ModifyStrokePointsCommand : BaseCommand
    {
        private Stroke m_TargetStroke;
        private PointerManager.ControlPoint[] m_StartPoints;
        private PointerManager.ControlPoint[] m_EndPoints;
        private int m_StartKnotCount;
        private int m_EndKnotCount;
        private TrTransform[] m_StartSplinePoints;
        private TrTransform[] m_EndSplinePoints;
        private List<Color32?> m_StartColors;
        private List<Color32?> m_EndColors;
        private SketchMemoryScript.StrokeFlags m_StartFlags;
        private SketchMemoryScript.StrokeFlags m_EndFlags;
        private float m_StartBrushSize;
        private float m_EndBrushSize;
        private bool m_UpdatesStrokeMetadata;

        public ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints, BaseCommand parent = null) : base(parent)
        {
            m_TargetStroke = stroke;
            m_StartPoints = (PointerManager.ControlPoint[])stroke.m_ControlPoints.Clone();
            m_EndPoints = newControlPoints;
            m_StartKnotCount = stroke.m_SplineKnotCount;
            m_StartSplinePoints = stroke.m_SplineControlPoints;
            m_StartBrushSize = stroke.m_BrushSize;
        }

        public override bool NeedsSave { get { return true; } }

        internal ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints,
            TrTransform[] splinePoints, List<Color32?> colors, SketchMemoryScript.StrokeFlags flags)
            : this(stroke, newControlPoints, splinePoints, splinePoints.Length, colors, flags, stroke.m_BrushSize)
        {
        }

        internal ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints,
            SketchMemoryScript.StrokeFlags flags, float brushSize, TrTransform[] splinePoints)
            : this(stroke, newControlPoints, splinePoints, stroke.m_SplineKnotCount,
                stroke.m_OverrideColors, flags, brushSize)
        {
        }

        private ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints,
            TrTransform[] splinePoints, int knotCount, List<Color32?> colors,
            SketchMemoryScript.StrokeFlags flags, float brushSize)
            : this(stroke, newControlPoints)
        {
            m_UpdatesStrokeMetadata = true;
            m_EndSplinePoints = splinePoints;
            m_EndKnotCount = knotCount;
            m_StartColors = stroke.m_OverrideColors;
            m_EndColors = colors;
            m_StartFlags = stroke.m_Flags;
            m_EndFlags = flags;
            m_EndBrushSize = brushSize;
        }

        public void UpdateEndPoints(PointerManager.ControlPoint[] newControlPoints)
        {
            m_EndPoints = newControlPoints;
        }

        private void ApplyNewPositionsToStroke(PointerManager.ControlPoint[] points)
        {
            m_TargetStroke.m_ControlPoints = points;
            m_TargetStroke.InvalidateCopy();
            m_TargetStroke.Uncreate();
            m_TargetStroke.Recreate();
        }

        private void ApplySplineMetadata(int count, TrTransform[] splinePoints)
        {
            m_TargetStroke.m_SplineKnotCount = count;
            m_TargetStroke.m_SplineControlPoints = splinePoints;
        }

        private void ApplySplineColors(List<Color32?> colors, int pointCount)
        {
            m_TargetStroke.m_OverrideColors = colors;
            if (m_TargetStroke.m_ControlPointsToDrop?.Length != pointCount)
                m_TargetStroke.m_ControlPointsToDrop = new bool[pointCount];
        }

        protected override void OnRedo()
        {
            if (m_UpdatesStrokeMetadata)
            {
                ApplySplineMetadata(m_EndKnotCount, m_EndSplinePoints);
                ApplySplineColors(m_EndColors, m_EndPoints.Length);
                m_TargetStroke.m_Flags = m_EndFlags;
                m_TargetStroke.m_BrushSize = m_EndBrushSize;
            }
            else if (m_StartSplinePoints != null)
            {
                // Arbitrary changes to the baked stroke cannot be expressed as the same hull.
                ApplySplineMetadata(0, null);
            }
            ApplyNewPositionsToStroke(m_EndPoints);
        }

        protected override void OnUndo()
        {
            if (m_UpdatesStrokeMetadata || m_StartSplinePoints != null)
                ApplySplineMetadata(m_StartKnotCount, m_StartSplinePoints);
            if (m_UpdatesStrokeMetadata)
            {
                ApplySplineColors(m_StartColors, m_StartPoints.Length);
                m_TargetStroke.m_Flags = m_StartFlags;
                m_TargetStroke.m_BrushSize = m_StartBrushSize;
            }
            ApplyNewPositionsToStroke(m_StartPoints);
        }
    }
} // namespace TiltBrush
