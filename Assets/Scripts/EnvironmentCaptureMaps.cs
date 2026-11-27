// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TiltBrush
{
    internal sealed class EnvironmentCaptureMaps : IDisposable
    {
        internal const int ReflectionSize = 128;
        internal const int DiffuseSize = 16;
        internal const int CoverageSamples = 1024;

        private static readonly int kCameraImage = Shader.PropertyToID("_RoomCaptureImage");
        private static readonly int kCameraIntrinsics = Shader.PropertyToID("_RoomCaptureIntrinsics");
        private static readonly int kWorldToCamera = Shader.PropertyToID("_RoomCaptureWorldToCamera");
        private static readonly int kFaceForward = Shader.PropertyToID("_RoomCaptureFaceForward");
        private static readonly int kFaceRight = Shader.PropertyToID("_RoomCaptureFaceRight");
        private static readonly int kFaceDown = Shader.PropertyToID("_RoomCaptureFaceDown");
        private static readonly int kRadiance = Shader.PropertyToID("_RoomCaptureRadiance");
        private static readonly int kFallback = Shader.PropertyToID("_RoomCaptureFallback");
        private static readonly int kFallbackDecode = Shader.PropertyToID("_RoomCaptureFallbackDecode");
        private static readonly int kHasFallback = Shader.PropertyToID("_RoomCaptureHasFallback");

        private static readonly Vector3[] kFaceForwardDirections =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
        };

        private static readonly Vector3[] kFaceRightDirections =
        {
            Vector3.back, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.left
        };

        private static readonly Vector3[] kFaceDownDirections =
        {
            Vector3.down, Vector3.down, Vector3.forward, Vector3.back, Vector3.down, Vector3.down
        };

        private static readonly Vector3[] kCoverageDirections = CreateCoverageDirections(CoverageSamples);

        private readonly Material m_Material;
        private readonly CommandBuffer m_Commands = new CommandBuffer { name = "Room panorama capture" };
        private readonly float[] m_CoverageAlpha = new float[CoverageSamples];
        private const float kCoveredAlpha = 0.5f;
        private int m_CoveredCount;

        public RenderTexture Reflection { get; }
        public RenderTexture Diffuse { get; }
        public RenderTexture SceneReflection { get; private set; }
        public int CoveragePercent => 100 * m_CoveredCount / CoverageSamples;
        public bool IsReady => Reflection.IsCreated() && Diffuse.IsCreated();

        public EnvironmentCaptureMaps(Shader shader)
        {
            m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Reflection = CreateCube("Captured room reflections", ReflectionSize, mipmaps: true);
            Diffuse = CreateCube("Captured room diffuse light", DiffuseSize, mipmaps: false);
            if (!IsReady)
                return;

            for (int face = 0; face < 6; face++)
            {
                m_Commands.SetRenderTarget(Reflection, 0, (CubemapFace)face);
                m_Commands.ClearRenderTarget(false, true, Color.clear);
            }
            Execute();
        }

        public void AddFrame(Texture texture, Quaternion cameraRotation, Vector4 intrinsics)
        {
            var worldToCamera = Matrix4x4.Rotate(Quaternion.Inverse(cameraRotation));
            m_Commands.SetGlobalTexture(kCameraImage, texture);
            m_Commands.SetGlobalVector(kCameraIntrinsics, intrinsics);
            m_Commands.SetGlobalMatrix(kWorldToCamera, worldToCamera);
            DrawFaces(Reflection, pass: 0);
            for (int i = 0; i < kCoverageDirections.Length; i++)
            {
                if (IsDirectionCovered(i))
                    continue;

                float alpha = FrameAlpha(worldToCamera.MultiplyVector(kCoverageDirections[i]), intrinsics);
                m_CoverageAlpha[i] += alpha * (1f - m_CoverageAlpha[i]);
                if (IsDirectionCovered(i))
                    m_CoveredCount++;
            }
        }

        public void Finish()
        {
            Reflection.GenerateMips();
            m_Commands.SetGlobalTexture(kRadiance, Reflection);
            DrawFaces(Diffuse, pass: 1);
            ClearInputs();
        }

        public bool ComposeSceneReflection(Texture fallback, Vector4 hdrDecode)
        {
            if (SceneReflection == null)
                SceneReflection = CreateCube("Room reflections with environment fallback", ReflectionSize, mipmaps: true);
            if (!SceneReflection.IsCreated())
                return false;

            bool hasFallback = fallback != null && fallback.dimension == TextureDimension.Cube;
            if (hasFallback)
                m_Commands.SetGlobalTexture(kFallback, fallback);
            m_Commands.SetGlobalFloat(kHasFallback, hasFallback ? 1f : 0f);
            m_Commands.SetGlobalVector(kFallbackDecode, hdrDecode);
            m_Commands.SetGlobalTexture(kRadiance, Reflection);
            DrawFaces(SceneReflection, pass: 2);
            SceneReflection.GenerateMips();
            Shader.SetGlobalTexture(kFallback, null);
            ClearInputs();
            return true;
        }

        public Color[] ReadAmbientSamples()
        {
            // Read the six axes of the convolved cube together when the user finishes a capture.
            var samples = RenderTexture.GetTemporary(6, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(6, 1, TextureFormat.RGBAHalf, false, true);
            var previous = RenderTexture.active;
            try
            {
                m_Commands.SetRenderTarget(samples);
                m_Commands.SetViewport(new Rect(0, 0, 6, 1));
                m_Commands.SetGlobalTexture(kRadiance, Diffuse);
                m_Commands.DrawProcedural(Matrix4x4.identity, m_Material, 3, MeshTopology.Triangles, 3);
                Execute();
                RenderTexture.active = samples;
                readback.ReadPixels(new Rect(0, 0, 6, 1), 0, 0, false);
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(samples);
                Destroy(readback);
                ClearInputs();
            }
        }

        public void Dispose()
        {
            ClearInputs();
            m_Commands.Dispose();
            Release(Reflection);
            Release(Diffuse);
            if (SceneReflection != null)
                Release(SceneReflection);
            Destroy(m_Material);
        }

        private static float FrameAlpha(Vector3 cameraRay, Vector4 intrinsics)
        {
            if (cameraRay.z < 0.0001f)
                return 0f;

            float x = intrinsics.x * cameraRay.x / cameraRay.z + intrinsics.z;
            float y = intrinsics.y * cameraRay.y / cameraRay.z + intrinsics.w;
            float edge = Mathf.Min(Mathf.Min(x, 1f - x), Mathf.Min(y, 1f - y));
            // Match the Paint pass and its premultiplied-alpha blend across frames.
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.015f, 0.13f, edge));
        }

        private static RenderTexture CreateCube(string name, int size, bool mipmaps)
        {
            var cube = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                dimension = TextureDimension.Cube,
                useMipMap = mipmaps,
                autoGenerateMips = false,
                filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            cube.Create();
            return cube;
        }

        private void DrawFaces(RenderTexture target, int pass)
        {
            var viewport = new Rect(0, 0, target.width, target.height);
            for (int face = 0; face < 6; face++)
            {
                m_Commands.SetRenderTarget(target, 0, (CubemapFace)face);
                m_Commands.SetViewport(viewport);
                m_Commands.SetGlobalVector(kFaceForward, kFaceForwardDirections[face]);
                m_Commands.SetGlobalVector(kFaceRight, kFaceRightDirections[face]);
                m_Commands.SetGlobalVector(kFaceDown, kFaceDownDirections[face]);
                m_Commands.DrawProcedural(Matrix4x4.identity, m_Material, pass, MeshTopology.Triangles, 3);
            }
            Execute();
        }

        private static void ClearInputs()
        {
            Shader.SetGlobalTexture(kCameraImage, null);
            Shader.SetGlobalTexture(kRadiance, null);
        }

        private void Execute()
        {
            Graphics.ExecuteCommandBuffer(m_Commands);
            m_Commands.Clear();
        }

        internal static Vector3 GetFaceDirection(int face) => kFaceForwardDirections[face];

        internal static Vector3 GetCoverageDirection(int sample) => kCoverageDirections[sample];

        internal bool IsDirectionCovered(int sample) => m_CoverageAlpha[sample] >= kCoveredAlpha;

        internal static Vector3[] CreateCoverageDirections(int count)
        {
            var directions = new Vector3[count];
            const float goldenAngle = 2.39996323f;
            for (int i = 0; i < directions.Length; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / directions.Length;
                float angle = i * goldenAngle;
                float radius = Mathf.Sqrt(1f - y * y);
                directions[i] = new Vector3(radius * Mathf.Cos(angle), y, radius * Mathf.Sin(angle));
            }
            return directions;
        }

        private static void Release(RenderTexture texture)
        {
            texture.Release();
            Destroy(texture);
        }

        private static void Destroy(UnityEngine.Object obj)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
