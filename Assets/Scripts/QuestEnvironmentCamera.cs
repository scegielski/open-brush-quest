// Copyright 2026 The Open Brush Authors. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace TiltBrush
{
    /// Gives the environment capture one calibrated camera frame at a time. The frame texture
    /// is reused and is valid until the next call that returns a frame, or disposal.
    internal sealed class QuestEnvironmentCamera : IDisposable
    {
        internal readonly struct Frame
        {
            public Texture Texture { get; }
            public Pose CameraPoseRoom { get; }
            public Vector2 FocalLengthPixels { get; }
            public Vector2 PrincipalPointPixels { get; }
            public int Width => Texture.width;
            public int Height => Texture.height;
            public long TimestampNs { get; }
            public float? ExposureSeconds { get; }
            public int? Iso { get; }

            internal Frame(Texture texture, Pose cameraPoseRoom, Vector2 focalLengthPixels,
                Vector2 principalPointPixels, long timestampNs, long exposureNs, int iso)
            {
                Texture = texture;
                CameraPoseRoom = cameraPoseRoom;
                FocalLengthPixels = focalLengthPixels;
                PrincipalPointPixels = principalPointPixels;
                TimestampNs = timestampNs;
                ExposureSeconds = exposureNs > 0 ? exposureNs * 1e-9f : (float?)null;
                Iso = iso > 0 ? iso : (int?)null;
            }
        }

        public static bool IsSupported
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return SystemInfo.deviceModel.IndexOf("Quest 3", StringComparison.OrdinalIgnoreCase) >= 0;
#else
                return false;
#endif
            }
        }

        public bool IsRunning { get; private set; }
        public string Failure { get; private set; }
        public bool IsRequestingPermission { get; private set; }
        public bool RequiresCameraPermission { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
        private const string NativeClass = "foundation.icosa.openbrush.QuestEnvironmentCamera";
        private const int PoseHistorySize = 64;

        private readonly struct TimedHeadPose
        {
            public readonly long TimestampNs;
            public readonly Pose Pose;

            public TimedHeadPose(long timestampNs, Pose pose)
            {
                TimestampNs = timestampNs;
                Pose = pose;
            }
        }

        private readonly TimedHeadPose[] m_HeadPoses = new TimedHeadPose[PoseHistorySize];
        private int m_NextHeadPose;
        private int m_NumHeadPoses;
        private int m_Run;
        private AndroidJavaClass m_CameraClass;
        private AndroidJavaObject m_Camera;
        private Texture2D m_Texture;
        private Vector2 m_FocalLength;
        private Vector2 m_PrincipalPoint;
        private Pose m_CameraFromHead;
#endif

        public IEnumerator StartCapture()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            StopCapture();
            Failure = null;
            RequiresCameraPermission = false;
            if (!IsSupported)
            {
                Failure = "Room capture requires a Meta Quest 3 or 3S.";
                yield break;
            }

            int run = m_Run;
            if (!HasCameraAccess())
            {
                yield return RequestCameraPermission(run, HeadsetCameraPermission);
                if (run != m_Run)
                    yield break;

                if (!HasCameraAccess())
                {
                    RequiresCameraPermission = true;
                    Failure = $"Room capture needs camera permission. {DescribeCameraGrants()}";
                    yield break;
                }
            }

            yield return TryStartNativeCamera(run);
            if (run != m_Run || IsRunning)
                yield break;

            // Some Camera2 implementations only expose the passthrough camera
            // after the alternate runtime grant. Try it when the service signals
            // access denied or no passthrough camera is visible.
            string alternate = !Permission.HasUserAuthorizedPermission(HeadsetCameraPermission)
                ? HeadsetCameraPermission
                : !Permission.HasUserAuthorizedPermission(Permission.Camera) ? Permission.Camera : null;
            if (alternate != null && m_Camera != null && m_Camera.Call<bool>("mayNeedAlternatePermission"))
            {
                Debug.Log($"[Room Capture] First startup attempt: {Failure}");
                CloseNativeCamera();
                yield return RequestCameraPermission(run, alternate);
                if (run != m_Run)
                    yield break;

                if (Permission.HasUserAuthorizedPermission(alternate))
                    yield return TryStartNativeCamera(run);
            }

            if (run == m_Run && !IsRunning)
                StopCapture();
#else
            Failure = "Room capture requires a Meta Quest 3 or 3S.";
            yield break;
#endif
        }

        public bool TryGetFrame(out Frame frame)
        {
            frame = default;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsRunning)
            {
                return false;
            }
            try
            {
                SampleHeadPose();
                using var nativeFrame = m_Camera.Call<AndroidJavaObject>("takeLatestFrame");
                if (nativeFrame == null)
                {
                    if (m_Camera.Call<bool>("isRunning"))
                    {
                        Failure = ReadNativeMessage("getFrameProblem");
                    }
                    else
                    {
                        Failure = ReadNativeMessage("getFailure") ?? "The Quest camera stopped.";
                        StopCapture();
                    }
                    return false;
                }

                long timestampNs = nativeFrame.Call<long>("getTimestampNs");
                long alignedTimestampNs = nativeFrame.Call<long>("getAlignedTimestampNs");
                long exposureNs = nativeFrame.Call<long>("getExposureNs");
                int iso = nativeFrame.Call<int>("getIso");
                // Sensor timestamps identify the beginning of the exposure. Use its midpoint
                // when the camera also supplies the shutter duration.
                if (!TryGetHeadPose(alignedTimestampNs + exposureNs / 2, out var head))
                {
                    return false;
                }

                byte[] pixels = nativeFrame.Call<byte[]>("getPixels");
                m_Texture.LoadRawTextureData(pixels);
                m_Texture.Apply(false, false);
                var cameraPose = new Pose(head.position + head.rotation * m_CameraFromHead.position,
                    head.rotation * m_CameraFromHead.rotation);
                frame = new Frame(m_Texture, cameraPose, m_FocalLength, m_PrincipalPoint,
                    timestampNs, exposureNs, iso);
                Failure = null;
                return true;
            }
            catch (Exception error)
            {
                Failure = $"Couldn't deliver a Quest camera frame: {error.Message}";
                StopCapture();
            }
#endif
            return false;
        }

        public void StopCapture()
        {
            IsRunning = false;
            IsRequestingPermission = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Run++;
            CloseNativeCamera();
#endif
        }

        public void Dispose() => StopCapture();

#if UNITY_ANDROID && !UNITY_EDITOR
        private void CloseNativeCamera()
        {
            if (m_Camera != null)
            {
                try
                {
                    m_Camera.Call("close");
                }
                catch (AndroidJavaException error)
                {
                    Debug.LogWarning($"Couldn't close the Quest camera: {error.Message}");
                }
                finally
                {
                    m_Camera.Dispose();
                    m_Camera = null;
                }
            }
            m_CameraClass?.Dispose();
            m_CameraClass = null;
            if (m_Texture != null)
            {
                UnityEngine.Object.Destroy(m_Texture);
                m_Texture = null;
            }
            m_NumHeadPoses = 0;
            m_NextHeadPose = 0;
        }
        private static bool HasCameraAccess() =>
            Permission.HasUserAuthorizedPermission(HeadsetCameraPermission) ||
            Permission.HasUserAuthorizedPermission(Permission.Camera);

        private static string DescribeCameraGrants() =>
            $"Headset camera granted={Permission.HasUserAuthorizedPermission(HeadsetCameraPermission)}, " +
            $"Android camera granted={Permission.HasUserAuthorizedPermission(Permission.Camera)}.";

        private IEnumerator RequestCameraPermission(int run, string permission)
        {
            bool dialogCompleted = false;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => dialogCompleted = true;
            callbacks.PermissionDenied += _ => dialogCompleted = true;
            callbacks.PermissionRequestDismissed += _ => dialogCompleted = true;

            IsRequestingPermission = true;
            // Request one scope. Unity can report a dismissal for a permission omitted
            // from a combined result, even if the headset camera was granted.
            Permission.RequestUserPermission(permission, callbacks);
            float deadline = Time.realtimeSinceStartup + 60f;
            float settleUntil = float.PositiveInfinity;
            while (run == m_Run && !Permission.HasUserAuthorizedPermission(permission) &&
                   Time.realtimeSinceStartup < Mathf.Min(deadline, settleUntil))
            {
                if (dialogCompleted && Application.isFocused && float.IsPositiveInfinity(settleUntil))
                    settleUntil = Time.realtimeSinceStartup + 0.75f;
                yield return null;
            }

            // This app keeps running while the system dialog owns focus. Let the
            // permission overlay return focus before opening the physical camera.
            float focusDeadline = Time.realtimeSinceStartup + 2f;
            while (run == m_Run && Permission.HasUserAuthorizedPermission(permission) &&
                   !Application.isFocused && Time.realtimeSinceStartup < focusDeadline)
                yield return null;

            if (run == m_Run)
                IsRequestingPermission = false;
        }

        private IEnumerator TryStartNativeCamera(int run)
        {
            Failure = null;
            Debug.Log($"[Room Capture] Opening Camera2. {DescribeCameraGrants()}");
            if (!OpenNativeCamera())
                yield break;

            float deadline = Time.realtimeSinceStartup + 8f;
            while (run == m_Run && Time.realtimeSinceStartup < deadline)
            {
                if (PollStartup() || Failure != null)
                    yield break;
                yield return null;
            }
            if (run == m_Run)
                Failure = "The Quest camera did not start in time.";
        }

        private string ReadNativeMessage(string method)
        {
            string message = m_Camera.Call<string>(method);
            return string.IsNullOrEmpty(message) ? null : message;
        }

        private bool OpenNativeCamera()
        {
            try
            {
                m_CameraClass = new AndroidJavaClass(NativeClass);
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Camera = new AndroidJavaObject(NativeClass, activity);
                return true;
            }
            catch (Exception error)
            {
                Failure = $"Couldn't open the Quest camera: {error.Message}";
                return false;
            }
        }

        private bool PollStartup()
        {
            try
            {
                SampleHeadPose();
                Failure = ReadNativeMessage("getFailure");
                if (Failure != null || !m_Camera.Call<bool>("isRunning"))
                {
                    return false;
                }

                int width = m_Camera.Call<int>("getWidth");
                int height = m_Camera.Call<int>("getHeight");
                float[] lens = m_Camera.Call<float[]>("getCalibration");
                m_FocalLength = new Vector2(lens[0], lens[1]);
                m_PrincipalPoint = new Vector2(lens[2], lens[3]);
                // Camera2 gives a right-handed lens offset and a headset-to-sensor
                // rotation. Flip the headset basis, invert it, then turn the image
                // basis right-side up for a Unity camera looking along +Z.
                m_CameraFromHead = new Pose(
                    new Vector3(lens[4], lens[5], -lens[6]) * App.METERS_TO_UNITS,
                    Quaternion.Inverse(new Quaternion(-lens[7], -lens[8], lens[9], lens[10])) *
                    Quaternion.AngleAxis(180f, Vector3.right));
                // Bytes contain gamma-encoded camera color. Leave them untouched at sampling
                // time in both Gamma and Linear Unity projects for the capture compositor.
                m_Texture = new Texture2D(width, height, TextureFormat.RGB24, false, true)
                {
                    name = "Quest environment camera",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                IsRunning = true;
                return true;
            }
            catch (Exception error)
            {
                Failure = $"Couldn't prepare the Quest camera: {error.Message}";
                return false;
            }
        }

        private void SampleHeadPose()
        {
            Transform head = ViewpointScript.Head;
            long timestampNs = m_CameraClass.CallStatic<long>("elapsedRealtimeNs");
            m_HeadPoses[m_NextHeadPose] = new TimedHeadPose(timestampNs, new Pose(head.position, head.rotation));
            m_NextHeadPose = (m_NextHeadPose + 1) % PoseHistorySize;
            m_NumHeadPoses = Mathf.Min(m_NumHeadPoses + 1, PoseHistorySize);
        }

        private bool TryGetHeadPose(long timestampNs, out Pose pose)
        {
            // Both timestamps are on Android BOOTTIME. Interpolate the Unity XR
            // poses around shutter time instead of using the head pose at delivery.
            int newerIndex = (m_NextHeadPose - 1 + PoseHistorySize) % PoseHistorySize;
            TimedHeadPose newer = m_HeadPoses[newerIndex];
            if (timestampNs > newer.TimestampNs)
            {
                pose = default;
                return false;
            }
            for (int i = 1; i < m_NumHeadPoses; ++i)
            {
                int olderIndex = (newerIndex - 1 + PoseHistorySize) % PoseHistorySize;
                TimedHeadPose older = m_HeadPoses[olderIndex];
                if (timestampNs >= older.TimestampNs)
                {
                    long intervalNs = newer.TimestampNs - older.TimestampNs;
                    float t = intervalNs > 0 ? (float)(timestampNs - older.TimestampNs) / intervalNs : 0f;
                    pose = new Pose(Vector3.Lerp(older.Pose.position, newer.Pose.position, t),
                        Quaternion.Slerp(older.Pose.rotation, newer.Pose.rotation, t));
                    return true;
                }
                newer = older;
                newerIndex = olderIndex;
            }
            pose = default;
            return false;
        }
#endif
    }
}
