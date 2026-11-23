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
        private int? m_EndKnotCount;
        private List<Color32?> m_StartColors;
        private List<Color32?> m_EndColors;

        public ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints, BaseCommand parent = null) : base(parent)
        {
            m_TargetStroke = stroke;
            m_StartPoints = (PointerManager.ControlPoint[])stroke.m_ControlPoints.Clone();
            m_EndPoints = newControlPoints;
        }

        public override bool NeedsSave { get { return true; } }

        internal ModifyStrokePointsCommand(Stroke stroke, PointerManager.ControlPoint[] newControlPoints,
            int knotCount, List<Color32?> colors) : this(stroke, newControlPoints)
        {
            m_StartKnotCount = stroke.m_SplineKnotCount;
            m_EndKnotCount = knotCount;
            m_StartColors = stroke.m_OverrideColors;
            m_EndColors = colors;
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

        private void ApplySplineMetadata(int count, List<Color32?> colors, int pointCount)
        {
            m_TargetStroke.m_SplineKnotCount = count;
            m_TargetStroke.m_OverrideColors = colors;
            if (m_TargetStroke.m_ControlPointsToDrop?.Length != pointCount)
                m_TargetStroke.m_ControlPointsToDrop = new bool[pointCount];
        }

        protected override void OnRedo()
        {
            if (m_EndKnotCount is int count)
                ApplySplineMetadata(count, m_EndColors, m_EndPoints.Length);
            ApplyNewPositionsToStroke(m_EndPoints);
        }

        protected override void OnUndo()
        {
            if (m_EndKnotCount.HasValue)
                ApplySplineMetadata(m_StartKnotCount, m_StartColors, m_StartPoints.Length);
            ApplyNewPositionsToStroke(m_StartPoints);
        }
    }
} // namespace TiltBrush
