// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using UnityEngine;

namespace TiltBrush
{
    public sealed class EnvironmentCapture : MonoBehaviour
    {
        private static readonly int kReflection = Shader.PropertyToID("_CapturedEnvironment");
        private static readonly int kDiffuse = Shader.PropertyToID("_CapturedDiffuseEnvironment");
        private static readonly int kWeight = Shader.PropertyToID("_CapturedEnvironmentWeight");
        private static EnvironmentCapture s_Instance;

        private QuestEnvironmentCamera m_Camera;
        private Coroutine m_Startup;
        private EnvironmentCaptureMaps m_Painting;
        private EnvironmentCaptureMaps m_Captured;
        private EnvironmentCaptureGuide m_CoverageGuide;
        private EnvironmentCaptureLighting m_SceneLighting;
        private RoomCaptureHud m_Hud;
        private float m_HudCloseTime;
        private string m_Status = "No room captured";
        private Vector3 m_CapturePosition;
        private Quaternion m_LastRotation;
        private float m_NextPaintTime;
        private float m_NextStationaryPaintTime;
        private float m_LastFrameTime;
        private int? m_LastCoverage;
        private bool m_MovedFromStart;
        private int m_ScannedHeadings;

        public static bool CanCapture => QuestEnvironmentCamera.IsSupported && App.Instance != null;
        public static bool IsCapturing => s_Instance != null && s_Instance.m_Camera != null;
        public static bool ShowsCoverageGuide => s_Instance != null && s_Instance.m_CoverageGuide != null;
        public static bool HasCapture => s_Instance != null && s_Instance.m_Captured != null;
        public static string Status => s_Instance != null ? s_Instance.m_Status :
            CanCapture ? "No room captured" : "Quest 3 camera required";

        public static void ToggleCapture()
        {
            if (IsCapturing)
            {
                s_Instance.Finish();
                return;
            }

            if (!CanCapture)
                return;

            if (s_Instance == null)
            {
                var root = new GameObject("Room environment capture");
                root.transform.SetParent(App.Instance.transform, false);
                s_Instance = root.AddComponent<EnvironmentCapture>();
            }

            s_Instance.m_Camera = new QuestEnvironmentCamera();
            s_Instance.ReleaseHud();
            s_Instance.m_Hud = new RoomCaptureHud(ViewpointScript.Head, s_Instance.gameObject.layer);
            s_Instance.SetStatus("Opening headset camera");
            s_Instance.m_Startup = s_Instance.StartCoroutine(s_Instance.OpenCamera());
        }

        public static void ClearCapture()
        {
            if (s_Instance == null)
                return;

            s_Instance.StopCamera();
            s_Instance.ReleaseHud();
            s_Instance.m_Painting?.Dispose();
            s_Instance.m_Painting = null;
            s_Instance.ReleasePublishedMaps();
            s_Instance.SetStatus("No room captured");
        }

        private IEnumerator OpenCamera()
        {
            yield return m_Camera.StartCapture();
            m_Startup = null;

            if (!m_Camera.IsRunning)
            {
                AbortCamera("Headset camera unavailable");
                yield break;
            }

            var shader = Resources.Load<Shader>("RoomCapture");
            if (shader == null || !shader.isSupported)
            {
                Abort("Capture rendering unavailable");
                yield break;
            }

            m_Painting = new EnvironmentCaptureMaps(shader);
            if (!m_Painting.IsReady)
            {
                Abort("Capture rendering unavailable");
                yield break;
            }

            m_LastCoverage = null;
            m_MovedFromStart = false;
            m_ScannedHeadings = 0;
            m_NextPaintTime = 0f;
            m_LastFrameTime = Time.unscaledTime;
            SetStatus("Look around (0%)");
        }

        private void Update()
        {
            if (m_Captured != null && !m_Captured.IsReady)
            {
                ReleasePublishedMaps();
                if (m_Camera == null)
                    SetStatus("Room capture expired");
            }

            if (m_Painting == null)
                return;

            if (!m_Camera.IsRunning || !m_Painting.IsReady)
            {
                AbortCamera("Capture interrupted");
                return;
            }

            // Poll every app frame so the source can match each camera timestamp to the XR pose history.
            if (!m_Camera.TryGetFrame(out var frame))
            {
                if (Time.unscaledTime - m_LastFrameTime > 6f)
                    AbortCamera("No headset camera frames");
                return;
            }

            m_LastFrameTime = Time.unscaledTime;
            if (Time.unscaledTime < m_NextPaintTime)
                return;

            Pose pose = frame.CameraPoseRoom;
            if (!m_LastCoverage.HasValue)
            {
                m_CapturePosition = pose.position;
                var guideShader = Resources.Load<Shader>("RoomCaptureGuide");
                if (guideShader != null && guideShader.isSupported)
                    m_CoverageGuide = new EnvironmentCaptureGuide(guideShader, ViewpointScript.Head.position, gameObject.layer);
                Debug.Log("[Room Capture] First calibrated camera frame received.");
            }

            // The panorama paints distant directions. Moving can introduce parallax,
            // but should never prevent the camera from recording a new direction.
            float travel = (pose.position - m_CapturePosition).magnitude / App.METERS_TO_UNITS;
            bool moved = travel > (m_MovedFromStart ? 0.35f : 0.40f);
            if (m_MovedFromStart != moved)
            {
                m_MovedFromStart = moved;
                SetCaptureStatus(m_LastCoverage ?? 0);
            }

            if (m_LastCoverage.HasValue && Time.unscaledTime < m_NextStationaryPaintTime &&
                Quaternion.Angle(m_LastRotation, pose.rotation) < 3f)
                return;

            var focal = frame.FocalLengthPixels;
            var center = frame.PrincipalPointPixels;
            var intrinsics = new Vector4(
                focal.x / frame.Width, focal.y / frame.Height, center.x / frame.Width, center.y / frame.Height);
            m_Painting.AddFrame(frame.Texture, pose.rotation, intrinsics);
            m_CoverageGuide?.UpdateCoverage(m_Painting);
            m_NextPaintTime = Time.unscaledTime + 0.13f;
            m_NextStationaryPaintTime = Time.unscaledTime + 0.45f;
            m_LastRotation = pose.rotation;
            int coverage = m_Painting.CoveragePercent;
            if (m_LastCoverage != coverage)
            {
                m_LastCoverage = coverage;
                SetCaptureStatus(coverage);
            }
            LogHeading(pose.rotation, travel, coverage);
        }

        private void SetCaptureStatus(int coverage)
        {
            string warning = m_MovedFromStart ? "Turn in place for best alignment" : null;
            SetStatus($"Look around ({coverage}%)", warning);
        }

        private void SetStatus(string text, string warning = null)
        {
            m_Status = text;
            m_Hud?.UpdateStatus(warning == null ? text : $"{text}\n{warning}");
        }

        private void LogHeading(Quaternion rotation, float travel, int coverage)
        {
            Vector3 direction = rotation * Vector3.forward;
            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            int sector = Mathf.FloorToInt(Mathf.Repeat(yaw + 22.5f, 360f) / 45f);
            int mask = 1 << sector;
            if ((m_ScannedHeadings & mask) == 0)
            {
                m_ScannedHeadings |= mask;
                Debug.Log($"[Room Capture] Painted heading {yaw:F0}°, {travel:F2} m from start, coverage {coverage}%.");
            }
        }

        private void LateUpdate()
        {
            // Markers show world directions. Centering on the viewer keeps those directions aligned as they move.
            m_CoverageGuide?.SetCenter(ViewpointScript.Head.position);
            m_SceneLighting?.Update();
            if (m_Hud != null && m_Camera == null && Time.unscaledTime >= m_HudCloseTime)
                ReleaseHud();
        }

        private void Finish()
        {
            if (m_Painting == null || !m_LastCoverage.HasValue || !m_Painting.IsReady)
            {
                Abort("Capture needs a camera frame");
                return;
            }

            // Complete rendering before closing the camera and releasing its input texture.
            m_Painting.Finish();
            StopCamera();
            ReleasePublishedMaps();
            m_Captured = m_Painting;
            m_Painting = null;
            Shader.SetGlobalTexture(kReflection, m_Captured.Reflection);
            Shader.SetGlobalTexture(kDiffuse, m_Captured.Diffuse);
            Shader.SetGlobalFloat(kWeight, 1f);
            m_SceneLighting = new EnvironmentCaptureLighting(m_Captured);
            m_SceneLighting.Update();
            SetStatus($"Room captured ({m_Captured.CoveragePercent}%)");
        }

        private void StopCamera()
        {
            if (m_Startup != null)
            {
                StopCoroutine(m_Startup);
                m_Startup = null;
            }

            m_CoverageGuide?.Dispose();
            m_CoverageGuide = null;
            m_Camera?.Dispose();
            m_Camera = null;
            m_HudCloseTime = Time.unscaledTime + 2.5f;
        }

        private void ReleaseHud()
        {
            m_Hud?.Dispose();
            m_Hud = null;
        }

        private void AbortCamera(string status)
        {
            string failure = m_Camera.Failure;
            if (!string.IsNullOrEmpty(failure))
            {
                Debug.LogWarning($"[Room Capture] {failure}");
                if (m_Camera.RequiresCameraPermission)
                    status = "Headset camera permission needed";
                else if (failure.Contains("Camera2 access was denied"))
                    status = "Camera2 access denied";
                else if (failure.Contains("passthrough camera was found"))
                    status = "Passthrough camera not found";
                else if (failure.Contains("calibration") || failure.Contains("sensor dimensions"))
                    status = "Camera calibration unavailable";
                else if (failure.Contains("rejected the video output") || failure.Contains("readable video stream"))
                    status = "Camera output unsupported";
                else if (failure.Contains("did not start in time"))
                    status = "Camera startup timed out";
                else if (failure.Contains("capture timestamps"))
                    status = "Camera tracking timestamps unavailable";
            }
            Abort(status);
        }

        private void Abort(string status)
        {
            StopCamera();
            m_Painting?.Dispose();
            m_Painting = null;
            SetStatus(status);
        }

        private void ReleasePublishedMaps()
        {
            m_SceneLighting?.Dispose();
            m_SceneLighting = null;
            Shader.SetGlobalFloat(kWeight, 0f);
            Shader.SetGlobalTexture(kReflection, null);
            Shader.SetGlobalTexture(kDiffuse, null);
            m_Captured?.Dispose();
            m_Captured = null;
        }

        private void OnApplicationPause(bool paused)
        {
            // During startup the Android camera permission dialog can temporarily pause Unity.
            if (paused && m_Camera != null && !m_Camera.IsRequestingPermission)
                Abort("Capture interrupted");
        }

        private void OnDisable()
        {
            ClearCapture();
        }

        private void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }
    }
}
