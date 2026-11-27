// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public sealed class RoomCaptureControls : MonoBehaviour
    {
        [SerializeField] private TextActionButton m_CaptureButton;
        [SerializeField] private ActionButton m_ClearCaptureButton;
        [SerializeField] private TextMeshPro m_CaptureStatus;

        private TextMeshPro m_CaptureButtonLabel;
        private (bool Capturing, bool CanToggle, bool CanClear)? m_DisplayedCaptureState;
        private (string Text, bool ShowsGuide) m_DisplayedCaptureStatus;

        private void Awake()
        {
            m_CaptureButtonLabel = m_CaptureButton.GetComponentInChildren<TextMeshPro>();
        }

        public void ToggleEnvironmentCapture()
        {
            EnvironmentCapture.ToggleCapture();
            Refresh();
        }

        public void ClearEnvironmentCapture()
        {
            EnvironmentCapture.ClearCapture();
            Refresh();
        }

        private void Update() => Refresh();

        private void Refresh()
        {
            bool capturing = EnvironmentCapture.IsCapturing;
            var state = (Capturing: capturing, CanToggle: capturing || EnvironmentCapture.CanCapture,
                CanClear: capturing || EnvironmentCapture.HasCapture);
            if (m_DisplayedCaptureState != state)
            {
                m_CaptureButton.SetButtonAvailable(state.CanToggle);
                m_CaptureButton.SetButtonSelected(state.Capturing);
                m_CaptureButtonLabel.text = state.Capturing ? "Finish Capture" : "Start Capture";
                m_ClearCaptureButton.SetButtonAvailable(state.CanClear);
                m_DisplayedCaptureState = state;
            }

            var status = (Text: EnvironmentCapture.Status, ShowsGuide: EnvironmentCapture.ShowsCoverageGuide);
            if (m_DisplayedCaptureStatus != status)
            {
                m_CaptureStatus.text = status.ShowsGuide ? $"{status.Text}\nAim toward hollow amber rings" : status.Text;
                m_DisplayedCaptureStatus = status;
            }
        }
    }
}
