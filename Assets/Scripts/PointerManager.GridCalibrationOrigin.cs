// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using UnityEngine;
using UnityEngine.Rendering;

namespace TiltBrush
{
    public partial class PointerManager
    {
        private const float kCalibrationAxisHalfLength = 6f * 0.0254f * App.METERS_TO_UNITS;
        private const float kCalibrationAxisWidth = 0.006f * App.METERS_TO_UNITS;
        private static readonly Color[] sm_CalibrationAxisColors =
        {
            Color.red, Color.green, new Color(0.15f, 0.4f, 1f)
        };

        private GameObject m_CalibrationOrigin;
        private Material m_CalibrationOriginMaterial;
        private LineRenderer[] m_CalibrationOriginAxes;

        private void UpdateGridCalibrationOrigin()
        {
            if (SketchControlsScript.m_Instance == null || !SketchControlsScript.m_Instance.GridCalibrationActive ||
                App.Instance == null || App.Scene == null)
            {
                if (m_CalibrationOrigin != null && m_CalibrationOrigin.activeSelf)
                    m_CalibrationOrigin.SetActive(false);
                return;
            }

            if (m_CalibrationOrigin == null)
                CreateGridCalibrationOrigin();

            TrTransform pose = App.Scene.Pose;
            for (int axis = 0; axis < m_CalibrationOriginAxes.Length; axis++)
            {
                Vector3 direction = Vector3.zero;
                direction[axis] = kCalibrationAxisHalfLength;
                Vector3 offset = pose.rotation * direction;
                m_CalibrationOriginAxes[axis].SetPosition(0, pose.translation - offset);
                m_CalibrationOriginAxes[axis].SetPosition(1, pose.translation + offset);
            }
            if (!m_CalibrationOrigin.activeSelf)
                m_CalibrationOrigin.SetActive(true);
        }

        private void CreateGridCalibrationOrigin()
        {
            m_CalibrationOrigin = new GameObject("Grid calibration origin");
            m_CalibrationOrigin.transform.SetParent(transform, false);
            var shader = Resources.Load<Shader>("Brushes/Basic/Transition/TransitionPreviewLine");
            m_CalibrationOriginMaterial = new Material(shader);

            var properties = new MaterialPropertyBlock();
            int color = Shader.PropertyToID("_Color");
            string[] names = { "X axis", "Y axis", "Z axis" };
            m_CalibrationOriginAxes = new LineRenderer[names.Length];
            for (int axis = 0; axis < names.Length; axis++)
            {
                var axisObject = new GameObject(names[axis]) { layer = gameObject.layer };
                axisObject.transform.SetParent(m_CalibrationOrigin.transform, false);
                var line = axisObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = kCalibrationAxisWidth;
                line.endWidth = kCalibrationAxisWidth;
                // A constant UV at the center of the overlay shader's dash makes a solid line.
                line.textureScale = new Vector2(0f, 1f);
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.allowOcclusionWhenDynamic = false;
                line.sharedMaterial = m_CalibrationOriginMaterial;
                properties.SetColor(color, sm_CalibrationAxisColors[axis]);
                line.SetPropertyBlock(properties);
                m_CalibrationOriginAxes[axis] = line;
            }
        }
    }
}
