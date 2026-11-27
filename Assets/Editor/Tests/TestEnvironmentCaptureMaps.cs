// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace TiltBrush
{
    public class TestEnvironmentCaptureMaps
    {
        [Test]
        public void ScanCoversBothSidesAndPolesWithOffCenterCamera()
        {
            var shader = Resources.Load<Shader>("RoomCapture");
            var camera = Texture2D.whiteTexture;
            using var maps = new EnvironmentCaptureMaps(shader);
            Assert.That(maps.IsReady, Is.True);

            var intrinsics = new Vector4(0.65f, 0.79f, 0.45f, 0.54f);
            // Sweep around the entire room at several elevations, including the vertical poles.
            for (int pitch = -90; pitch <= 90; pitch += 30)
            {
                for (int yaw = 0; yaw < 360; yaw += 30)
                    maps.AddFrame(camera, Quaternion.Euler(pitch, yaw, 0f), intrinsics);
            }
            Assert.That(maps.CoveragePercent, Is.EqualTo(100));
        }

        [Test]
        public void PaintedFramesFollowCameraOrientationAndLeaveUnseenDirectionsEmpty()
        {
            var shader = Resources.Load<Shader>("RoomCapture");
            var guideShader = Resources.Load<Shader>("RoomCaptureGuide");
            Assert.That(guideShader != null && guideShader.isSupported && !ShaderUtil.ShaderHasError(guideShader), Is.True);
            var readerShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/Editor/Tests/RoomCaptureTestReadback.shader");
            Assert.That(shader != null && shader.isSupported, Is.True);
            Assert.That(readerShader != null && readerShader.isSupported, Is.True);

            const int size = 32;
            var camera = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var pixel = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var reader = new Material(readerShader);
            var previous = RenderTexture.active;
            try
            {
                var image = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Encoded camera values. The two gradients also distinguish a mirrored or flipped source.
                    image[y * size + x] = new Color((x + 0.5f) / size, (y + 0.5f) / size, 0.6f);
                }
                camera.SetPixels(image);
                camera.Apply();

                using var maps = new EnvironmentCaptureMaps(shader);
                Assert.That(maps.IsReady, Is.True);
                Assert.That(maps.CoveragePercent, Is.Zero);

                var intrinsics = new Vector4(0.56f, 0.56f, 0.5f, 0.5f);
                maps.AddFrame(camera, Quaternion.identity, intrinsics);
                int firstCoverage = maps.CoveragePercent;
                Assert.That(firstCoverage, Is.InRange(10, 30));
                for (int i = 0; i < EnvironmentCaptureMaps.CoverageSamples; i++)
                {
                    Vector3 direction = EnvironmentCaptureMaps.GetCoverageDirection(i);
                    if (direction.z > 0.98f)
                        Assert.That(maps.IsDirectionCovered(i), Is.True);
                    if (direction.z < -0.98f)
                        Assert.That(maps.IsDirectionCovered(i), Is.False);
                }

                Color Read(RenderTexture cube, Vector3 direction)
                {
                    reader.SetTexture("_TestRoomCube", cube);
                    reader.SetVector("_TestRoomDirection", direction);
                    Graphics.Blit(null, pixel, reader);
                    RenderTexture.active = pixel;
                    readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                    return readback.GetPixel(0, 0);
                }

                Color forward = Read(maps.Reflection, Vector3.forward);
                Assert.That(forward.a, Is.EqualTo(1f).Within(0.01f));
                Assert.That(forward.b, Is.EqualTo(Mathf.GammaToLinearSpace(0.6f)).Within(0.025f));
                Assert.That(Read(maps.Reflection, Vector3.back).a, Is.EqualTo(0f).Within(0.001f));

                Color topRight = Read(maps.Reflection, new Vector3(0.4f, 0.4f, 1f));
                Color bottomLeft = Read(maps.Reflection, new Vector3(-0.4f, -0.4f, 1f));
                Assert.That(topRight.r, Is.GreaterThan(bottomLeft.r + 0.15f));
                Assert.That(topRight.g, Is.GreaterThan(bottomLeft.g + 0.15f));

                int edgeSample = -1;
                for (int i = 0; i < EnvironmentCaptureMaps.CoverageSamples; i++)
                {
                    Vector3 ray = EnvironmentCaptureMaps.GetCoverageDirection(i);
                    if (ray.z <= 0f)
                        continue;

                    float x = intrinsics.x * ray.x / ray.z + intrinsics.z;
                    float y = intrinsics.y * ray.y / ray.z + intrinsics.w;
                    float edge = Mathf.Min(Mathf.Min(x, 1f - x), Mathf.Min(y, 1f - y));
                    if (edge > 0.04f && edge < 0.055f)
                    {
                        edgeSample = i;
                        break;
                    }
                }
                Assert.That(edgeSample, Is.GreaterThanOrEqualTo(0));
                Assert.That(maps.IsDirectionCovered(edgeSample), Is.False);
                for (int i = 0; i < 8; i++)
                    maps.AddFrame(camera, Quaternion.identity, intrinsics);
                Assert.That(maps.IsDirectionCovered(edgeSample), Is.True);

                maps.AddFrame(camera, Quaternion.Euler(0f, 90f, 0f), intrinsics);
                Assert.That(maps.CoveragePercent, Is.GreaterThan(firstCoverage));
                Assert.That(Read(maps.Reflection, Vector3.right).a, Is.EqualTo(1f).Within(0.01f));
                Assert.That(Read(maps.Reflection, Vector3.forward).a, Is.EqualTo(1f).Within(0.01f));

                maps.Finish();
                Color lit = Read(maps.Diffuse, Vector3.forward);
                Color shaded = Read(maps.Diffuse, Vector3.back);
                Assert.That(lit.a, Is.InRange(0.25f, 0.99f));
                Assert.That(lit.b, Is.EqualTo(lit.a * Mathf.GammaToLinearSpace(0.6f)).Within(0.025f));
                Assert.That(lit.a, Is.GreaterThan(shaded.a + 0.2f));
                Color[] axes = maps.ReadAmbientSamples();
                Assert.That(axes[4].b, Is.EqualTo(lit.b).Within(0.01f));
                Assert.That(axes[5].b, Is.EqualTo(shaded.b).Within(0.01f));

                var fallback = new Cubemap(1, TextureFormat.RGBAHalf, false);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                var fallbackColor = new Color(0.2f, 0.3f, 0.5f, 1f);
                try
                {
                    for (int face = 0; face < 6; face++)
                        fallback.SetPixel((CubemapFace)face, 0, 0, fallbackColor);
                    fallback.Apply();
                    Assert.That(maps.ComposeSceneReflection(fallback, new Vector4(1, 1, 0, 0)), Is.True);
                    Color shownForward = Read(maps.SceneReflection, Vector3.forward);
                    Color shownBack = Read(maps.SceneReflection, Vector3.back);
                    Assert.That(shownForward.b, Is.EqualTo(linear ? Mathf.GammaToLinearSpace(0.6f) : 0.6f).Within(0.025f));
                    Assert.That(shownBack.b, Is.EqualTo(fallbackColor.b).Within(0.025f));
                    Assert.That(shownBack.a, Is.EqualTo(1f).Within(0.01f));

                    var previousReflection = RenderSettings.customReflectionTexture;
                    var previousMode = RenderSettings.defaultReflectionMode;
                    var previousAmbient = RenderSettings.ambientProbe;
                    using (var scene = new EnvironmentCaptureLighting(maps))
                    {
                        scene.Update();
                        Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(maps.SceneReflection));
                        Assert.That(RenderSettings.defaultReflectionMode,
                            Is.EqualTo(UnityEngine.Rendering.DefaultReflectionMode.Custom));
                        Assert.That(RenderSettings.ambientProbe, Is.Not.EqualTo(previousAmbient));
                    }
                    Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(previousReflection));
                    Assert.That(RenderSettings.defaultReflectionMode, Is.EqualTo(previousMode));
                }
                finally
                {
                    Object.DestroyImmediate(fallback);
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(pixel);
                Object.DestroyImmediate(reader);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(camera);
            }
        }
    }
}
