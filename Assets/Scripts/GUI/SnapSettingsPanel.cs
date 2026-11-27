// Copyright 2022 The Tilt Brush Authors
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


using TMPro;
using UnityEngine;

namespace TiltBrush
{

    public enum SnapSettingsPanelToggleType
    {
        LockSnapTranslateX,
        LockSnapTranslateY,
        LockSnapTranslateZ,
        LockSnapAngleX,
        LockSnapAngleY,
        LockSnapAngleZ,
        SnapToGuides,
        StraightEdgeEndpointSnap
    }

    public class SnapSettingsPanel : BasePanel
    {
        private const string kShowHeightPreference = "ShowDrawingHeight";

        [SerializeField] private TextActionButton m_CalibrationButton;
        [SerializeField] private TextMeshPro m_CalibrationHint;
        [SerializeField] private TextMeshPro m_FloorHeightValue;
        [SerializeField] private ActionButton[] m_FloorHeightButtons;
        [SerializeField] private TextActionButton m_ShowHeightButton;

        private TextMeshPro m_CalibrationLabel;
        private TextMeshPro m_ShowHeightLabel;
        private (bool Available, bool Active)? m_CalibrationState;
        private int? m_DisplayedFloorHeight;
        private bool? m_FloorHeightAvailable;
        private bool? m_DisplayedShowHeight;

        public static bool ShowHeight => PlayerPrefs.GetInt(kShowHeightPreference, 1) != 0;

        public override void InitPanel()
        {
            base.InitPanel();
            m_CalibrationLabel = m_CalibrationButton.GetComponentInChildren<TextMeshPro>();
            m_ShowHeightLabel = m_ShowHeightButton.GetComponentInChildren<TextMeshPro>();
            UpdateCalibrationControls();
            UpdateFloorHeight();
            UpdateShowHeight();
        }

        private void Update()
        {
            BaseUpdate();
            if (m_CalibrationLabel == null)
                return;

            UpdateCalibrationControls();
            UpdateFloorHeight();
            UpdateShowHeight();
        }

        public void ToggleShowHeight()
        {
            PlayerPrefs.SetInt(kShowHeightPreference, ShowHeight ? 0 : 1);
            PlayerPrefs.Save();
            UpdateShowHeight();
        }

        private void UpdateShowHeight()
        {
            bool showHeight = ShowHeight;
            if (m_DisplayedShowHeight == showHeight)
                return;

            m_ShowHeightButton.SetButtonSelected(showHeight);
            m_ShowHeightLabel.text = showHeight ? "Show Height: On" : "Show Height: Off";
            m_DisplayedShowHeight = showHeight;
        }

        public void ToggleGridCalibration()
        {
            var controls = SketchControlsScript.m_Instance;
            if (controls == null)
                return;

            controls.SetGridCalibration(!controls.GridCalibrationActive);
            UpdateCalibrationControls();
        }

        public override void OnWidgetHide()
        {
            base.OnWidgetHide();
            var controls = SketchControlsScript.m_Instance;
            if (controls != null && controls.GridCalibrationActive)
                controls.SetGridCalibration(false);
        }

        public void StepFloorHeight(int halfInchSteps)
        {
            if (App.Instance == null || App.Scene == null)
                return;

            App.Scene.StepFloorHeight(halfInchSteps);
            UpdateFloorHeight();
        }

        private void UpdateFloorHeight()
        {
            var scene = App.Instance != null ? App.Scene : null;
            bool available = scene != null && scene.CanEditFloorHeight;
            if (m_FloorHeightAvailable != available)
            {
                foreach (var button in m_FloorHeightButtons)
                    button.SetButtonAvailable(available);
                m_FloorHeightAvailable = available;
            }

            int height = scene != null ? scene.FloorHeightHalfInches : 0;
            if (m_DisplayedFloorHeight != height)
            {
                long magnitude = System.Math.Abs((long)height);
                string sign = height < 0 ? "-" : height > 0 ? "+" : "";
                string fraction = magnitude % 2 != 0 ? ".5" : "";
                m_FloorHeightValue.text = $"{sign}{magnitude / 24} ft {magnitude % 24 / 2}{fraction} in";
                m_DisplayedFloorHeight = height;
            }
        }

        private void UpdateCalibrationControls()
        {
            var controls = SketchControlsScript.m_Instance;
            bool active = controls != null && controls.GridCalibrationActive;
            bool available = active || (controls != null && controls.CanCalibrateGrid);
            var state = (Available: available, Active: active);
            if (m_CalibrationState == state)
                return;

            if (m_CalibrationState?.Available != available)
                m_CalibrationButton.SetButtonAvailable(available);
            m_CalibrationButton.SetButtonSelected(active);
            m_CalibrationLabel.text = active ? "Finish Calibration" : "Calibrate Grid";
            m_CalibrationHint.text = available ? "One grip to move\nTwo grips to rotate" : "";
            m_CalibrationState = state;
        }

        public void HandleToggle(SnapSettingsPanelToggleButton btn)
        {
            switch (btn.m_ButtonType)
            {
                case SnapSettingsPanelToggleType.LockSnapTranslateX:
                    SelectionManager.m_Instance.m_EnableSnapTranslationX = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.LockSnapTranslateY:
                    SelectionManager.m_Instance.m_EnableSnapTranslationY = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.LockSnapTranslateZ:
                    SelectionManager.m_Instance.m_EnableSnapTranslationZ = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.LockSnapAngleX:
                    SelectionManager.m_Instance.m_EnableSnapRotationX = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.LockSnapAngleY:
                    SelectionManager.m_Instance.m_EnableSnapRotationY = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.LockSnapAngleZ:
                    SelectionManager.m_Instance.m_EnableSnapRotationZ = btn.ToggleState;
                    break;
                case SnapSettingsPanelToggleType.StraightEdgeEndpointSnap:
                    PointerManager.m_Instance.StraightEdgeGuide.EndpointSnappingEnabled =
                        btn.ToggleState;
                    break;
            }
        }

        public void HandleSnapSelectionToGrid()
        {
            TransformItems.SnapSelectionToGrid();
        }

        public void HandleToggleSnapToGuides()
        {
            WidgetManager.m_Instance.m_EnableSnapToGuides = !WidgetManager.m_Instance.m_EnableSnapToGuides;
        }

        public void HandleSnapSelectedRotationAngles()
        {
            TransformItems.SnapSelectedRotationAngles();
        }
    }
} // namespace TiltBrush
