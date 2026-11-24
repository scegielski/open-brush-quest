// Copyright 2021 The Tilt Brush Authors
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

using UnityEngine;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        private bool m_GridSnapActive;

        // based off of the MultiplyPoint3x4
        // note that positional offset (matrix[0].w, matrix[1].w, matrix[2].w) has been REVERSE and is subtracting
        // don't ask me why I had to do this, but it had to be done to make it work in this scenario.
        static Vector3 WorldToCanvasPos(Matrix4x4 matrix, Vector3 point)
        {
            Vector3 res;
            res.x = matrix.m00 * point.x + matrix.m01 * point.y + matrix.m02 * point.z - matrix.m03;
            res.y = matrix.m10 * point.x + matrix.m11 * point.y + matrix.m12 * point.z - matrix.m13;
            res.z = matrix.m20 * point.x + matrix.m21 * point.y + matrix.m22 * point.z - matrix.m23;
            return res;
        }

        internal static Vector3 RoundGridPosition(Vector3 point, float spacing, bool x, bool y, bool z,
            float floorHeight = 0f)
        {
            return new Vector3(
                x ? Mathf.Round(point.x / spacing) * spacing : point.x,
                y ? Mathf.Round((point.y - floorHeight) / spacing) * spacing + floorHeight : point.y,
                z ? Mathf.Round(point.z / spacing) * spacing : point.z);
        }

        public static Vector3 SnapToGrid(Vector3 position, bool allowAxisSlide = true)
        {
            var selection = SelectionManager.m_Instance;
            float gridSubdivision = selection.SnappingGridSize;
            if (gridSubdivision <= 0f)
                return position;

            Transform grid = App.ActiveCanvas.transform;
            Vector3 localCanvasPos = grid.InverseTransformPoint(position);
            Vector3 roundedCanvasPos = RoundGridPosition(localCanvasPos, gridSubdivision,
                selection.m_EnableSnapTranslationX, selection.m_EnableSnapTranslationY,
                selection.m_EnableSnapTranslationZ, App.Scene.FloorHeightCanvasUnits);
            if (!allowAxisSlide)
                return grid.TransformPoint(roundedCanvasPos);

            Vector3 offset = new Vector3(
                Mathf.Abs(roundedCanvasPos.x - localCanvasPos.x),
                Mathf.Abs(roundedCanvasPos.y - localCanvasPos.y),
                Mathf.Abs(roundedCanvasPos.z - localCanvasPos.z)
            );

            // If we're close to a grid point then always snap
            Vector3 snappedPos = roundedCanvasPos;

            // Otherwise allow a degree of freedom in the appropriate snap axis
            // (but only if we're not very close to a snap point)
            const float stickiness = 4f;
            if (offset.x > offset.y && offset.x > offset.z) // x is biggest
            {
                if (offset.x > gridSubdivision / stickiness)
                {
                    snappedPos = new Vector3(localCanvasPos.x, roundedCanvasPos.y, roundedCanvasPos.z);
                }
            }
            else if (offset.y > offset.x && offset.y > offset.z) // y is biggest
            {
                if (offset.y > gridSubdivision / stickiness)
                {
                    snappedPos = new Vector3(roundedCanvasPos.x, localCanvasPos.y, roundedCanvasPos.z);
                }
            }
            else // z is biggest
            {
                if (offset.z > gridSubdivision / stickiness)
                {
                    snappedPos = new Vector3(roundedCanvasPos.x, roundedCanvasPos.y, localCanvasPos.z);
                }
            }

            return grid.TransformPoint(snappedPos);
        }

    }
}
