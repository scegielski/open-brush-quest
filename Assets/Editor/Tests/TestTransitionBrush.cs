// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TiltBrush
{
    public class TestTransitionBrush
    {
        private const string kBrush = "Assets/Resources/Brushes/Basic/Transition/Transition.asset";

        [TestCase(0.1f, 6f, 1.090872f)]
        [TestCase(3f, 6f, 5.196152f)]
        [TestCase(6f, 6f, 6f)]
        [TestCase(9f, 6f, 6f)]
        public void ProfileConnectsCopingAndFloorAtTheRequestedRadius(
            float height, float radius, float run)
        {
            var profile = new TransitionBrush.SurfaceProfile(height, radius);
            Vector2 deck = profile.Sample(0, out float deckDistance);
            Assert.That(deck.x, Is.LessThan(0f));
            Assert.That(deck.y, Is.LessThan(height));
            Assert.That(deckDistance, Is.LessThan(0f));
            Vector2 copingEdge = profile.Sample(1, out _);
            Assert.That(copingEdge.x - deck.x,
                Is.EqualTo(TransitionBrush.DefaultDeckDepthInches * TransitionBrush.UnitsPerInch).Within(1e-5f));
            Assert.That(profile.Sample(TransitionBrush.TopOfCoping, out _), Is.EqualTo(new Vector2(0, height)));
            int noseMiddle = (TransitionBrush.TopOfCoping + TransitionBrush.WallStart) / 2;
            Vector2 lip = profile.Sample(noseMiddle, out _);
            Assert.That(lip.x, Is.GreaterThan(0f));
            Assert.That(lip.y, Is.LessThan(height));
            Vector2 join = profile.Sample(TransitionBrush.WallStart, out float joinDistance);
            Assert.That(joinDistance, Is.Zero);
            Assert.That(join.y, Is.LessThan(lip.y));
            if (height > radius)
                Assert.That(lip.x - join.x, Is.EqualTo(1.5f * TransitionBrush.UnitsPerInch).Within(1e-5f));
            Vector2 floor = profile.Sample(TransitionBrush.ProfilePoints - 1, out _);
            Assert.That(floor.y, Is.Zero);
            Assert.That(floor.x, Is.EqualTo(run).Within(1e-5f));

            for (int row = TransitionBrush.WallStart + 1; row < TransitionBrush.ProfilePoints; row++)
            {
                Vector2 point = profile.Sample(row, out _);
                Assert.That(Vector2.Distance(point, new Vector2(run, radius)), Is.EqualTo(radius).Within(1e-5f));
            }
        }

        [Test]
        public void DimensionsDisplayHalfInchesAcrossFootBoundaries()
        {
            Assert.That(TransitionBrush.FormatFeetAndInches(71), Is.EqualTo("5 ft 11 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(71.5f), Is.EqualTo("5 ft 11.5 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(72), Is.EqualTo("6 ft 0 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(72.5f), Is.EqualTo("6 ft 0.5 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(73), Is.EqualTo("6 ft 1 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(-14.5f), Is.EqualTo("-1 ft 2.5 in"));
        }

        [Test]
        public void HalfInchSettingsRetainLegacyPreferences()
        {
            const string radius = "TransitionRadiusInches";
            const string deck = "TransitionDeckDepthInches";
            const string radiusHalf = "TransitionRadiusHalfInches";
            const string deckHalf = "TransitionDeckDepthHalfInches";
            string[] keys = { radius, deck, radiusHalf, deckHalf };
            var previous = keys.Select(key => PlayerPrefs.HasKey(key) ? (int?)PlayerPrefs.GetInt(key) : null).ToArray();
            try
            {
                PlayerPrefs.DeleteKey(radiusHalf);
                PlayerPrefs.DeleteKey(deckHalf);
                PlayerPrefs.DeleteKey(radius);
                Assert.That(TransitionBrush.RadiusInches, Is.EqualTo(54f));
                PlayerPrefs.SetInt(radius, 83);
                PlayerPrefs.SetInt(deck, 27);
                Assert.That(TransitionBrush.RadiusInches, Is.EqualTo(83f));
                Assert.That(TransitionBrush.DeckDepthInches, Is.EqualTo(27f));
                TransitionBrush.RadiusHalfInches++;
                TransitionBrush.DeckDepthHalfInches--;
                Assert.That(TransitionBrush.RadiusInches, Is.EqualTo(83.5f));
                Assert.That(TransitionBrush.DeckDepthInches, Is.EqualTo(26.5f));
            }
            finally
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    if (previous[i] is int value)
                        PlayerPrefs.SetInt(keys[i], value);
                    else
                        PlayerPrefs.DeleteKey(keys[i]);
                }
            }
        }

        [Test]
        public void UnsnappedTransitionHeadingUsesTheCurrentPoseEvenWithinOneFrame()
        {
            var previousInput = InputManager.m_Instance;
            var toolObject = new GameObject("Transition heading test");
            try
            {
                InputManager.m_Instance = null;
                var tool = toolObject.AddComponent<FreePaintTool>();
                var first = tool.SampleTransitionOrientation(null, Quaternion.Euler(22f, 35f, 0f), snap: false);
                var turned = tool.SampleTransitionOrientation(null, Quaternion.Euler(22f, 74f, 0f), snap: false);
                var vertical = tool.SampleTransitionOrientation(null, Quaternion.Euler(89f, 117f, 0f), snap: false);

                Assert.That(Quaternion.Angle(first, Quaternion.Euler(0f, 35f, 0f)), Is.LessThan(0.05f));
                Assert.That(Quaternion.Angle(turned, Quaternion.Euler(0f, 74f, 0f)), Is.LessThan(0.05f));
                Assert.That(Quaternion.Angle(vertical, Quaternion.Euler(0f, 117f, 0f)), Is.LessThan(0.05f));
            }
            finally
            {
                InputManager.m_Instance = previousInput;
                Object.DestroyImmediate(toolObject);
            }
        }

        [Test]
        public void VerticalGridSnapUsesTheAdjustedFloor()
        {
            float spacing = TransitionBrush.UnitsPerInch;
            float floor = spacing * 0.5f;
            var position = new Vector3(1.2f * spacing, floor + 0.2f * spacing, -1.2f * spacing);
            var snapped = FreePaintTool.RoundGridPosition(position, spacing, x: true, y: true, z: true,
                floorHeight: floor);
            Assert.That(snapped, Is.EqualTo(new Vector3(spacing, floor, -spacing)));
            Assert.That(FreePaintTool.RoundGridPosition(position, spacing, x: true, y: false, z: true,
                floorHeight: floor).y, Is.EqualTo(position.y));
        }

        [Test]
        public void ProfileReachesTheFloorOfATransformedCanvas()
        {
            var roomFromCanvas = TrTransform.TRS(new Vector3(4f, 2f, 3f),
                Quaternion.Euler(35f, 50f, 0f), 2.5f);
            var coping = new Vector3(1f, 8f, 2f);
            var across = Vector3.back;
            var profile = new TransitionBrush.SurfaceProfile(coping.y, 6f);
            Vector3 top = roomFromCanvas * profile.Vertex(coping, across, TransitionBrush.TopOfCoping, out _);
            Vector3 floor = roomFromCanvas *
                profile.Vertex(coping, across, TransitionBrush.ProfilePoints - 1, out _);

            Assert.That(Vector3.Distance(top, roomFromCanvas * coping), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(floor, roomFromCanvas * new Vector3(1f, 0f, -4f)),
                Is.LessThan(1e-5f));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void PreviewRendersOverOpaqueSketchGeometryAndRespondsToDimensions(int turns)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Preview rendering requires a graphics device.");

            var scene = SceneManager.GetSceneByPath("Assets/Scenes/Main.unity");
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Additive);
            var tool = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<FreePaintTool>(true))
                .Single();
            var cameraObject = new GameObject("Transition preview test camera");
            var occluder = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var occluderMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            var image = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            var target = RenderTexture.GetTemporary(512, 512, 24, RenderTextureFormat.ARGB32);
            var previousTarget = RenderTexture.active;
            int layer = tool.gameObject.layer;
            bool wasActive = tool.gameObject.activeSelf;
            try
            {
                const int previewLayer = 30;
                tool.gameObject.layer = previewLayer;
                tool.gameObject.SetActive(true);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 7f;
                camera.aspect = 1f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << previewLayer;
                camera.transform.position = new Vector3(3f, 4f, -9f);
                occluder.layer = previewLayer;
                occluder.transform.position = new Vector3(3f, 4f, -7f);
                occluder.transform.localScale = Vector3.one * 20f;
                occluderMaterial.SetColor("_BaseColor", Color.black);
                occluder.GetComponent<MeshRenderer>().sharedMaterial = occluderMaterial;

                var canvasPose = TrTransform.TRS(new Vector3(2f, 1f, -3f), Quaternion.Euler(0f, 30f, 0f), 1.5f);
                Vector3 drawingPoint = new Vector3(1f, 6f * TransitionBrush.UnitsPerInch, 4f);
                tool.DrawBrushHeightLabel(canvasPose, drawingPoint, 6.5f * TransitionBrush.UnitsPerInch,
                    camera.transform);
                var height = tool.transform.Find("Brush height").GetComponent<TMPro.TextMeshPro>();
                Assert.That(height.text, Is.EqualTo("Height -0 ft 0.5 in"));
                Vector3 labelAnchor = canvasPose * drawingPoint + Vector3.up * (0.08f * App.METERS_TO_UNITS);
                Assert.That(Vector3.Distance(height.transform.position, labelAnchor), Is.LessThan(1e-5f));
                Assert.That(tool.transform.Find("Transition profile preview"), Is.Null);

                var coping = new Vector3(0f, 30f * TransitionBrush.UnitsPerInch, 0f);
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 72, turns,
                    viewer: camera.transform);
                var preview = tool.transform.Find("Transition profile preview");
                Assert.That(height.text, Is.EqualTo("Height 2 ft 6 in"));
                Assert.That(height.transform.position.y, Is.GreaterThan(coping.y));
                var line = preview.GetComponent<LineRenderer>();
                Assert.That(line.widthMultiplier, Is.EqualTo(0.055f));
                Assert.That(line.textureMode, Is.EqualTo(LineTextureMode.Tile));
                Assert.That(line.sharedMaterial.shader.name, Is.EqualTo("Custom/TransitionPreviewLine"));
                Assert.That(ShaderUtil.ShaderHasError(line.sharedMaterial.shader), Is.False);
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0f, 0f, 512f, 512f), 0, 0);
                var pixels = image.GetPixels32();
                Assert.That(pixels.Count(p => p.a > 250 && p.r < 4 && p.g < 4 && p.b < 4),
                    Is.GreaterThan(512 * 450), "An opaque surface must cover the preview in depth.");
                Assert.That(pixels.Count(p => p.a > 128 && p.b > 140 && p.g > 140 && p.r < 150), Is.GreaterThan(30),
                    "The arc must write visible cyan pixels with alpha.");
                Vector3 across = TransitionBrush.Across(Vector3.forward, turns);
                float Across(Vector3 point) => Vector3.Dot(point - coping, across);
                float[] BaseAt(float floorY)
                {
                    var points = new Vector3[line.positionCount];
                    line.GetPositions(points);
                    return points.Where(point => Mathf.Abs(point.y - floorY) < 1e-5f).Select(Across).ToArray();
                }
                float Run() => BaseAt(0f).Max();
                float[] baseAtFloor = BaseAt(0f);
                Assert.That(baseAtFloor.Length, Is.GreaterThanOrEqualTo(2));
                Assert.That(baseAtFloor.Min(), Is.LessThan(0f));
                Assert.That(line.GetPosition(line.positionCount - 1), Is.EqualTo(line.GetPosition(0)));
                float initialRun = Run();
                float initialDeck = Vector3.Distance(line.GetPosition(0), line.GetPosition(1));
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 71.5f, turns,
                    viewer: camera.transform);
                Assert.That(Run(), Is.LessThan(initialRun));
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 71.5f, turns,
                    deckDepthInches: 12.5f, viewer: camera.transform);
                Assert.That(Vector3.Distance(line.GetPosition(0), line.GetPosition(1)), Is.GreaterThan(initialDeck));

                // A profile behind the viewer still has its exact dimensions in the sketch.
                coping.y = 36f * TransitionBrush.UnitsPerInch;
                coping.z = -15f;
                Assert.That(camera.WorldToViewportPoint(coping).z, Is.LessThan(0f));
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 54, turns,
                    viewer: camera.transform);
                Assert.That(height.text, Is.EqualTo("Height 3 ft 0 in"));
                Assert.That(line.GetPosition(TransitionBrush.TopOfCoping), Is.EqualTo(coping));
                Assert.That(Run() / TransitionBrush.UnitsPerInch, Is.EqualTo(50.91169f).Within(1e-4f));

                float raisedFloor = 6.5f * TransitionBrush.UnitsPerInch;
                coping.y = 54f * TransitionBrush.UnitsPerInch;
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 54, turns,
                    viewer: camera.transform, floorHeight: raisedFloor);
                Assert.That(height.text, Is.EqualTo("Height 3 ft 11.5 in"));
                Assert.That(line.GetPosition(TransitionBrush.TopOfCoping), Is.EqualTo(coping));
                Assert.That(BaseAt(raisedFloor).Length, Is.GreaterThanOrEqualTo(2));

                coping.y = -2f * TransitionBrush.UnitsPerInch;
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 72, turns,
                    viewer: camera.transform);
                Assert.That(line.enabled, Is.False);
                Assert.That(height.renderer.enabled, Is.False);

                coping.y = 30f * TransitionBrush.UnitsPerInch;
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 72, turns,
                    style: TransitionBrush.SweepStyle.SteelTransition, viewer: camera.transform);
                Assert.That(line.enabled, Is.True);
                Assert.That(height.renderer.enabled, Is.True);
                var tube = preview.Find("Steel coping profile").GetComponent<LineRenderer>();
                Assert.That(tube.enabled, Is.True);
                Assert.That(tube.positionCount, Is.EqualTo(TransitionBrush.TubePoints));
                Assert.That(tube.GetPosition(0), Is.EqualTo(tube.GetPosition(tube.positionCount - 1)));
                Assert.That(line.GetPosition(line.positionCount - 1), Is.EqualTo(line.GetPosition(0)));
                const float tubeDiameterInches = 3.5f;
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 72, turns,
                    style: TransitionBrush.SweepStyle.SteelTransition, viewer: camera.transform,
                    steelDiameterInches: tubeDiameterInches);
                int tubeBottom = TransitionBrush.TubePoints / 2;
                Assert.That(tube.GetPosition(0).y, Is.EqualTo(coping.y).Within(1e-5f));
                Assert.That((tube.GetPosition(0).y - tube.GetPosition(tubeBottom).y) /
                    TransitionBrush.UnitsPerInch, Is.EqualTo(tubeDiameterInches).Within(1e-4f));
                Assert.That(BaseAt(0f).Length, Is.GreaterThanOrEqualTo(2));

                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, 72, turns);
                Assert.That(line.enabled, Is.True);
                Assert.That(tube.enabled, Is.False);
                Assert.That(height.renderer.enabled, Is.False);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(occluder);
                Object.DestroyImmediate(occluderMaterial);
                var preview = tool.transform.Find("Transition profile preview");
                if (preview != null)
                {
                    Object.DestroyImmediate(preview.GetComponent<LineRenderer>().sharedMaterial);
                    Object.DestroyImmediate(preview.gameObject);
                }
                var height = tool.transform.Find("Brush height");
                if (height != null)
                {
                    Object.DestroyImmediate(height.GetComponent<TMPro.TextMeshPro>().fontSharedMaterial);
                    Object.DestroyImmediate(height.gameObject);
                }
                tool.gameObject.layer = layer;
                tool.gameObject.SetActive(wasActive);
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        [Test]
        public void SteelSurfaceReflectsTheEnvironmentCubemap()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Steel rendering requires a graphics device.");

            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(kBrush);
            var tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var filter = tile.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            var cameraObject = new GameObject("Steel reflection test camera");
            var target = RenderTexture.GetTemporary(64, 64, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var previousTarget = RenderTexture.active;
            var previousReflection = RenderSettings.customReflectionTexture;
            var previousMode = RenderSettings.defaultReflectionMode;
            var previousIntensity = RenderSettings.reflectionIntensity;
            var cube = new Cubemap(16, TextureFormat.RGBA32, true);

            try
            {
                tile.layer = 30;
                filter.sharedMesh = mesh;
                tile.GetComponent<MeshRenderer>().sharedMaterial = descriptor.Material;
                mesh.SetUVs(1, Enumerable.Repeat(Vector3.forward, mesh.vertexCount).ToList());
                mesh.colors32 = Enumerable.Repeat(new Color32(128, 128, 128, 255), mesh.vertexCount).ToArray();
                for (int face = 0; face < 6; face++)
                    cube.SetPixels(Enumerable.Repeat(new Color(0.10f, 0.26f, 0.8f), 256).ToArray(), (CubemapFace)face);
                cube.Apply();
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = cube;
                RenderSettings.reflectionIntensity = 1f;

                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 0.6f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << tile.layer;
                camera.transform.position = Vector3.back * 3f;
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0f, 0f, 64f, 64f), 0, 0);
                var reflected = image.GetPixel(32, 32);
                Assert.That(reflected.a, Is.GreaterThan(0.99f));
                Assert.That(reflected.b - reflected.r, Is.GreaterThan(0.30f));

                mesh.colors32 = Enumerable.Repeat(new Color32(220, 90, 40, 255), mesh.vertexCount).ToArray();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                image.ReadPixels(new Rect(0f, 0f, 64f, 64f), 0, 0);
                Assert.That(image.GetPixel(32, 32).b, Is.LessThan(reflected.b - 0.12f));
            }
            finally
            {
                RenderSettings.customReflectionTexture = previousReflection;
                RenderSettings.defaultReflectionMode = previousMode;
                RenderSettings.reflectionIntensity = previousIntensity;
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void CapturedEnvironmentAndSelectionWorkTogetherOnConstructionSurfaces()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Captured environment rendering requires a graphics device.");

            Cubemap SolidCube(Color color, bool mipmaps)
            {
                const int size = 16;
                var cube = new Cubemap(size, GraphicsFormat.R8G8B8A8_UNorm,
                    mipmaps ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
                var pixels = Enumerable.Repeat(color, size * size).ToArray();
                for (int face = 0; face < 6; face++)
                    cube.SetPixels(pixels, (CubemapFace)face);
                cube.Apply(updateMipmaps: mipmaps);
                return cube;
            }

            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(kBrush);
            var material = new Material(descriptor.Material) { mainTexture = Texture2D.grayTexture };
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var filter = quad.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            var cameraObject = new GameObject("Captured transition test camera");
            var target = RenderTexture.GetTemporary(32, 32, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var fallback = SolidCube(new Color(0.02f, 0.06f, 0.65f), mipmaps: true);
            var reflection = SolidCube(new Color(0.7f, 0f, 0f, 0.8f), mipmaps: true);
            var diffuse = SolidCube(Color.red, mipmaps: false);
            int reflectionId = Shader.PropertyToID("_CapturedEnvironment");
            int diffuseId = Shader.PropertyToID("_CapturedDiffuseEnvironment");
            int weightId = Shader.PropertyToID("_CapturedEnvironmentWeight");
            int leftSelectionId = Shader.PropertyToID("_LeftEyeSelectionColor");
            int rightSelectionId = Shader.PropertyToID("_RightEyeSelectionColor");
            int disableSelectionId = Shader.PropertyToID("_DisableSelectionEffect");
            var previousCapturedReflection = Shader.GetGlobalTexture(reflectionId);
            var previousCapturedDiffuse = Shader.GetGlobalTexture(diffuseId);
            float previousWeight = Shader.GetGlobalFloat(weightId);
            Color previousLeftSelection = Shader.GetGlobalColor(leftSelectionId);
            Color previousRightSelection = Shader.GetGlobalColor(rightSelectionId);
            float previousDisableSelection = Shader.GetGlobalFloat(disableSelectionId);
            var previousTarget = RenderTexture.active;
            var previousReflection = RenderSettings.customReflectionTexture;
            var previousReflectionMode = RenderSettings.defaultReflectionMode;
            float previousReflectionIntensity = RenderSettings.reflectionIntensity;
            try
            {
                quad.layer = 30;
                filter.sharedMesh = mesh;
                mesh.uv = Enumerable.Repeat(new Vector2(0.01f, 0.01f), mesh.vertexCount).ToArray();
                mesh.colors32 = Enumerable.Repeat(new Color32(128, 128, 128, 255), mesh.vertexCount).ToArray();
                quad.GetComponent<MeshRenderer>().sharedMaterial = material;
                material.SetFloat("_BumpScale", 0f);
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = fallback;
                RenderSettings.reflectionIntensity = 1f;
                Shader.SetGlobalTexture(reflectionId, reflection);
                Shader.SetGlobalTexture(diffuseId, diffuse);

                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 0.6f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << quad.layer;
                camera.transform.position = Vector3.back * 3f;
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };

                Color Draw(Vector3 surface, float weight)
                {
                    mesh.SetUVs(1, Enumerable.Repeat(surface, mesh.vertexCount).ToList());
                    Shader.SetGlobalFloat(weightId, weight);
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(16f, 16f, 1f, 1f), 0, 0, false);
                    Color pixel = image.GetPixel(0, 0);
                    Assert.That(pixel.a, Is.GreaterThan(0.99f));
                    return pixel;
                }

                Color steelBefore = Draw(Vector3.forward, 0f);
                Color steelAfter = Draw(Vector3.forward, 1f);
                Assert.That(steelAfter.r - steelBefore.r, Is.GreaterThan(0.20f));
                Assert.That(steelBefore.b - steelAfter.b, Is.GreaterThan(0.15f));

                Color concreteBefore = Draw(Vector3.up, 0f);
                Color concreteAfter = Draw(Vector3.up, 1f);
                Assert.That(concreteAfter.r - concreteBefore.r, Is.GreaterThan(0.01f));

                Color tileBefore = Draw(Vector3.zero, 0f);
                Color tileAfter = Draw(Vector3.zero, 1f);
                Assert.That(tileAfter.r, Is.EqualTo(tileBefore.r).Within(0.004f));
                Assert.That(tileAfter.g, Is.EqualTo(tileBefore.g).Within(0.004f));
                Assert.That(tileAfter.b, Is.EqualTo(tileBefore.b).Within(0.004f));

                // Match the keyword that BatchManager enables on Selection Canvas.
                Assert.That(material.shader.keywordSpace.FindKeyword("SELECTION_ON").isValid, Is.True);
                material.EnableKeyword("SELECTION_ON");
                Shader.SetGlobalColor(leftSelectionId, Color.green);
                Shader.SetGlobalColor(rightSelectionId, Color.green);
                Shader.SetGlobalFloat(disableSelectionId, 0f);
                Color selectedSteelWithoutCapture = Draw(Vector3.forward, 0f);
                Color selectedSteel = Draw(Vector3.forward, 1f);
                Assert.That(selectedSteel.g - steelAfter.g, Is.GreaterThan(0.15f));
                Assert.That(selectedSteel.r - selectedSteelWithoutCapture.r, Is.GreaterThan(0.20f));
                Color selectedConcrete = Draw(Vector3.up, 1f);
                Assert.That(selectedConcrete.g - concreteAfter.g, Is.GreaterThan(0.15f));
                Color selectedTile = Draw(Vector3.zero, 1f);
                Assert.That(selectedTile.g - tileAfter.g, Is.GreaterThan(0.15f));

                // Save thumbnails disable the selection effect but keep captured lighting.
                Shader.SetGlobalFloat(disableSelectionId, 1f);
                Color thumbnailSteel = Draw(Vector3.forward, 1f);
                Assert.That(thumbnailSteel.g, Is.EqualTo(steelAfter.g).Within(0.004f));
                Assert.That(thumbnailSteel.r, Is.EqualTo(steelAfter.r).Within(0.004f));
            }
            finally
            {
                Shader.SetGlobalTexture(reflectionId, previousCapturedReflection);
                Shader.SetGlobalTexture(diffuseId, previousCapturedDiffuse);
                Shader.SetGlobalFloat(weightId, previousWeight);
                Shader.SetGlobalColor(leftSelectionId, previousLeftSelection);
                Shader.SetGlobalColor(rightSelectionId, previousRightSelection);
                Shader.SetGlobalFloat(disableSelectionId, previousDisableSelection);
                RenderSettings.customReflectionTexture = previousReflection;
                RenderSettings.defaultReflectionMode = previousReflectionMode;
                RenderSettings.reflectionIntensity = previousReflectionIntensity;
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(fallback);
                Object.DestroyImmediate(reflection);
                Object.DestroyImmediate(diffuse);
                Object.DestroyImmediate(quad);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ConcreteMaterialRendersTintedDetailAndRepeatsAtTileBoundaries()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Concrete rendering requires a graphics device.");

            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(kBrush);
            Assert.That(descriptor.Material.mainTexture.name, Is.EqualTo("Concrete019Diffuse"));
            var normalMap = descriptor.Material.GetTexture("_BumpMap");
            var normalImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(normalMap));
            Assert.That(normalImporter.textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(normalImporter.flipGreenChannel, Is.True);
            Assert.That(normalImporter.GetPlatformTextureSettings("Android").maxTextureSize, Is.EqualTo(2048));
            Assert.That(descriptor.Material.GetTexture("_RoughnessMap").name, Is.EqualTo("Concrete019Specular"));
            var tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var cameraObject = new GameObject("Concrete test camera");
            var target = RenderTexture.GetTemporary(128, 128, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previousTarget = RenderTexture.active;
            var mesh = Object.Instantiate(tile.GetComponent<MeshFilter>().sharedMesh);
            var previousSun = RenderSettings.sun;
            Light key = null;
            try
            {
                tile.layer = 30;
                tile.GetComponent<MeshFilter>().sharedMesh = mesh;
                tile.GetComponent<MeshRenderer>().sharedMaterial = descriptor.Material;
                mesh.colors32 = Enumerable.Repeat(new Color32(156, 114, 76, 255), mesh.vertexCount).ToArray();
                mesh.SetUVs(1, Enumerable.Repeat(Vector3.zero, mesh.vertexCount).ToList());
                Vector2[] quadUvs = mesh.uv;

                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 0.55f;
                camera.aspect = 1f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << tile.layer;
                camera.transform.position = Vector3.back * 3f;
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                var sample = image.GetPixel(32, 32);
                Assert.That(sample.a, Is.GreaterThan(0.95f));
                Assert.That(sample.r, Is.GreaterThan(sample.g));
                Assert.That(sample.g, Is.GreaterThan(sample.b));
                var patch = image.GetPixels(16, 16, 96, 96);
                Assert.That(patch.Max(pixel => pixel.g) - patch.Min(pixel => pixel.g), Is.GreaterThan(0.03f));

                mesh.uv = mesh.uv.Select(uv => uv + Vector2.right).ToArray();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                Assert.That(image.GetPixel(32, 32).g, Is.EqualTo(sample.g).Within(0.015f));

                // Fit the coping, tile band, and adjacent wall into one view of the brush material.
                mesh.uv = mesh.uv.Select(uv => new Vector2((uv.x - 1f) * 0.10f, uv.y * 0.20f - 0.075f)).ToArray();
                mesh.colors32 = Enumerable.Repeat(new Color32(128, 128, 128, 255), mesh.vertexCount).ToArray();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                Color tileColor = image.GetPixel(23, 63);
                Color groutColor = image.GetPixel(25, 63);
                Color copingColor = image.GetPixel(23, 32);
                Assert.That(tileColor.b, Is.GreaterThan(tileColor.r + 0.06f));
                Assert.That(tileColor.b, Is.GreaterThan(groutColor.b + 0.03f));
                Assert.That(copingColor.b, Is.LessThan(tileColor.b));

                mesh.colors32 = Enumerable.Repeat(new Color32(156, 114, 76, 255), mesh.vertexCount).ToArray();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                Assert.That(image.GetPixel(23, 63).b, Is.LessThan(tileColor.b - 0.025f));
                Assert.That(image.GetPixel(23, 32).b, Is.LessThan(copingColor.b - 0.025f));
                Assert.That(image.GetPixel(23, 105).r, Is.GreaterThan(image.GetPixel(23, 105).b));

                // The same negative UVs on the deck should display concrete in place of coping.
                mesh.SetUVs(1, Enumerable.Repeat(Vector3.right, mesh.vertexCount).ToList());
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                Color deckColor = image.GetPixel(23, 63);
                Assert.That(deckColor.r, Is.GreaterThan(deckColor.b));

                // Magnify the surface so pixels sample the glaze, bevel, and full mortar separately.
                mesh.colors32 = Enumerable.Repeat(new Color32(128, 128, 128, 255), mesh.vertexCount).ToArray();
                mesh.SetUVs(1, Enumerable.Repeat(Vector3.zero, mesh.vertexCount).ToList());
                mesh.uv = quadUvs.Select(uv => new Vector2((uv.x - 0.5f) / 60f, (1.25f + uv.y * 0.5f) / 60f))
                    .ToArray();
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                float ambientGlaze = image.GetPixel(95, 63).b;
                float ambientMortar = image.GetPixel(64, 63).b;
                key = new GameObject("Tile glaze test light", typeof(Light)).GetComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 0.35f;
                key.cullingMask = 1 << tile.layer;
                key.gameObject.layer = tile.layer;
                RenderSettings.sun = key;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                float litGlaze = image.GetPixel(95, 63).b;
                float litMortar = image.GetPixel(64, 63).b;
                float litBevel = image.GetPixel(70, 63).b;
                Assert.That((litGlaze - ambientGlaze) - (litMortar - ambientMortar), Is.GreaterThan(0.15f));
                Assert.That(litGlaze - litBevel, Is.GreaterThan(0.08f));
            }
            finally
            {
                RenderSettings.sun = previousSun;
                if (key != null)
                    Object.DestroyImmediate(key.gameObject);
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void ConcreteNormalMapChangesShadingUnderTheFillLightWithFlatAlbedo()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Concrete rendering requires a graphics device.");

            const int layer = 30;
            const int resolution = 512;
            const int border = 32;
            const int patchSize = resolution - 2 * border;
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(kBrush);
            var material = new Material(descriptor.Material);
            var tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var filter = tile.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            var cameraObject = new GameObject("Concrete normal test camera");
            var key = new GameObject("Light behind concrete", typeof(Light)).GetComponent<Light>();
            var fill = new GameObject("Concrete grazing fill", typeof(Light)).GetComponent<Light>();
            var previousSun = RenderSettings.sun;
            var previousTarget = RenderTexture.active;
            var target = RenderTexture.GetTemporary(resolution, resolution, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);

            try
            {
                tile.layer = key.gameObject.layer = fill.gameObject.layer = layer;
                filter.sharedMesh = mesh;
                mesh.uv = mesh.uv.Select(uv => uv + Vector2.up * 0.1f).ToArray();
                mesh.SetUVs(1, Enumerable.Repeat(Vector3.zero, mesh.vertexCount).ToList());
                mesh.colors32 = Enumerable.Repeat(new Color32(128, 128, 128, 255), mesh.vertexCount).ToArray();
                tile.GetComponent<MeshRenderer>().sharedMaterial = material;
                material.mainTexture = Texture2D.whiteTexture;
                material.SetTexture("_RoughnessMap", Texture2D.whiteTexture);

                key.type = fill.type = LightType.Directional;
                key.cullingMask = fill.cullingMask = 1 << layer;
                key.transform.rotation = Quaternion.LookRotation(Vector3.back);
                key.intensity = 1.6f;
                fill.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -0.35f, 0.35f));
                fill.intensity = 0.6f;
                RenderSettings.sun = key;

                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 0.55f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << layer;
                camera.transform.position = Vector3.back * 3f;
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                var frame = new Rect(0f, 0f, resolution, resolution);

                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(frame, 0, 0);
                Color[] withNormalMap = image.GetPixels(border, border, patchSize, patchSize);
                material.SetFloat("_BumpScale", 0f);
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(frame, 0, 0);
                Color[] flat = image.GetPixels(border, border, patchSize, patchSize);
                fill.enabled = false;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(frame, 0, 0);
                Color[] withoutFill = image.GetPixels(border, border, patchSize, patchSize);

                Assert.That(flat.Min(pixel => pixel.a), Is.GreaterThan(0.99f));
                Assert.That(flat.Max(pixel => pixel.g) - flat.Min(pixel => pixel.g), Is.LessThan(0.005f));
                Assert.That(flat.Average(pixel => pixel.g) - withoutFill.Average(pixel => pixel.g),
                    Is.GreaterThan(0.06f), "The fill light must illuminate the concrete from a grazing angle.");
                var normalContrast = withNormalMap.Zip(flat, (lit, plain) => Mathf.Abs(lit.g - plain.g)).ToArray();
                Assert.That(normalContrast.Average(), Is.GreaterThan(0.008f));
                Assert.That(normalContrast.Count(change => change > 0.025f),
                    Is.GreaterThan(normalContrast.Length / 40),
                    "The normal map alone must create visible shading under a grazing fill light.");
            }
            finally
            {
                RenderSettings.sun = previousSun;
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(key.gameObject);
                Object.DestroyImmediate(fill.gameObject);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [TestCase(0, 1, 0, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(1, 0, -1, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(2, -1, 0, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(3, 0, 1, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(0, 0, -1, SketchMemoryScript.StrokeFlags.TransitionFixedHeading)]
        [TestCase(0, 0, -1, SketchMemoryScript.StrokeFlags.TransitionControllerHeading)]
        [TestCase(0, 1, 0, SketchMemoryScript.StrokeFlags.TransitionVariableDeck)]
        [TestCase(0, 0, -1, SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets)]
        [TestCase(0, 1, 0, SketchMemoryScript.StrokeFlags.TransitionVariableDeck, true)]
        [TestCase(1, 1, 0, SketchMemoryScript.StrokeFlags.TransitionControllerHeading |
            SketchMemoryScript.StrokeFlags.SplineAutoOrient)]
        public void BrushPrefabProducesATexturedSurfaceAtItsSavedAngle(
            int turns, int acrossX, int acrossZ,
            SketchMemoryScript.StrokeFlags headingMode, bool untiled = false)
        {
            string brushPath = untiled ? kBrush.Replace("/Transition.asset", "/PoolCopingTransition.asset") : kBrush;
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(brushPath);
            var manifest = AssetDatabase.LoadAssetAtPath<TiltBrushManifest>("Assets/Manifest.asset");
            Assert.That(manifest.Brushes, Does.Contain(descriptor));
            Assert.That(descriptor.DurableName, Is.EqualTo(untiled ? "Transition with Pool Coping" : "Transition"));
            Assert.That(descriptor.Description, Is.EqualTo(
                untiled ? "Transition with Pool Coping" : "Transition with Tile and Pool Coping"));
            Assert.That(TransitionBrush.HasDeck(descriptor), Is.True);
            Assert.That(TransitionBrush.IsTransition(descriptor), Is.True);
            Assert.That(descriptor.m_AllowExport, Is.True);
            Assert.That(descriptor.Material, Is.Not.Null);
            Assert.That(descriptor.HasExportTexture(), Is.True);
            Assert.That(ShaderUtil.ShaderHasError(descriptor.Material.shader), Is.False);

            var scene = SceneManager.GetSceneByPath("Assets/Scenes/Main.unity");
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Additive);
            var previousApp = App.Instance;
            var previousConfig = Config.m_SingletonState;
            var parent = new GameObject("Transition test canvas");
            BaseBrushScript brush = null;
            Mesh mesh = null;
            try
            {
                App.Instance = scene.GetRootGameObjects().Select(g => g.GetComponent<App>()).First(a => a != null);
                Config.m_SingletonState = parent.AddComponent<Config>();
                var start = TrTransform.T(Vector3.up * 8f);
                // GeometryBrush creates its runtime-owned mesh. The fixture destroys it below.
                LogAssert.Expect(LogType.Error,
                    "Instantiating mesh due to calling MeshFilter.mesh during edit mode. This will leak meshes. Please use MeshFilter.sharedMesh instead.");
                brush = BaseBrushScript.Create(parent.transform, start, descriptor, Color.cyan, 6f);
                var transition = (TransitionBrush)brush;
                var end = TrTransform.T(new Vector3(0f, 8f, 10f));
                bool variableDeck = headingMode == SketchMemoryScript.StrokeFlags.TransitionVariableDeck;
                const float firstDepth = 18f * TransitionBrush.UnitsPerInch;
                const float lastDepth = 64f * TransitionBrush.UnitsPerInch;
                var profile = new TransitionBrush.SurfaceProfile(end.translation.y, 6f,
                    variableDeck ? lastDepth : TransitionBrush.LegacyPoolDeckDepthInches * TransitionBrush.UnitsPerInch);
                var flags = TransitionBrush.FlagsForQuarterTurns(turns) | headingMode;
                var points = new[]
                {
                    new PointerManager.ControlPoint { m_Pos = start.translation, m_Orient = start.rotation },
                    new PointerManager.ControlPoint { m_Pos = end.translation, m_Orient = end.rotation }
                };
                if (variableDeck)
                {
                    points[0].m_Pressure = 18f / TransitionBrush.MaxDeckDepthInches;
                    points[1].m_Pressure = 64f / TransitionBrush.MaxDeckDepthInches;
                }
                Vector3 across = new Vector3(acrossX, 0, acrossZ);
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionFixedHeading)
                    points[0].m_Orient = Quaternion.LookRotation(Vector3.right);
                if ((headingMode & SketchMemoryScript.StrokeFlags.SplineAutoOrient) != 0)
                {
                    end.rotation = Quaternion.LookRotation(Vector3.right);
                    points[1].m_Orient = end.rotation;
                }
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets)
                {
                    end.rotation = Quaternion.Euler(0f, 90f, 0f);
                    points[1].m_Orient = end.rotation;
                }
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionControllerHeading)
                {
                    // Motion stays straight while the wrist turns. Only the later cross-section
                    // should turn, and the saved samples should rebuild the same surface.
                    end.rotation = Quaternion.LookRotation(Vector3.right);
                    points[1].m_Orient = end.rotation;
                    transition.TrackControllerForNewStroke = true;
                    transition.NewStrokeQuarterTurns = turns;
                    brush.UpdatePosition_LS(end, 1f);
                    brush.ApplyChangesToVisuals();
                    var drawnVertices = brush.GetComponent<MeshFilter>().sharedMesh.vertices;
                    Vector3 expectedFloor = profile.Vertex(end.translation, across, TransitionBrush.ProfilePoints - 1, out _);
                    Assert.That(drawnVertices.Any(vertex => Vector3.Distance(vertex, new Vector3(6f, 0f, 0f)) < 1e-5f));
                    Assert.That(drawnVertices.Any(vertex => Vector3.Distance(vertex, expectedFloor) < 1e-5f));
                }
                // Recreated strokes take precedence over the current painting orientation.
                transition.NewStrokeQuarterTurns = (turns + 1) % 4;
                transition.TrackControllerForNewStroke = false;
                brush.SetStrokeData(new StrokeData { m_Flags = flags, m_ControlPoints = points });
                brush.ResetBrushForPreview(start);
                brush.UpdatePosition_LS(end, variableDeck ? 64f / TransitionBrush.MaxDeckDepthInches : 1f);
                brush.ApplyChangesToVisuals();
                mesh = brush.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh.GetIndexCount(0), Is.GreaterThan(0));
                Assert.That(mesh.bounds.min.y, Is.Zero.Within(1e-5f));
                Assert.That(mesh.bounds.max.y, Is.EqualTo(8f).Within(1e-5f));
                Vector3 firstAcross = headingMode == SketchMemoryScript.StrokeFlags.TransitionControllerHeading ||
                    headingMode == SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets ? Vector3.right : across;
                float deckWidth = 10f * TransitionBrush.UnitsPerInch;
                float defaultDepth = TransitionBrush.LegacyPoolDeckDepthInches * TransitionBrush.UnitsPerInch;
                Vector3 deckDrop = Vector3.up * (0.6f * TransitionBrush.UnitsPerInch);
                Vector3 firstDeck = start.translation - deckDrop - firstAcross * (deckWidth + (variableDeck ? firstDepth : defaultDepth));
                Vector3 lastDeck = end.translation - deckDrop - across * (deckWidth + (variableDeck ? lastDepth : defaultDepth));
                var vertices = mesh.vertices;
                Vector2[] uv = mesh.uv;
                var region = new List<Vector3>();
                mesh.GetUVs(1, region);
                const float tileSize = 3f * App.METERS_TO_UNITS;
                int FindVertex(Vector3 expected, System.Predicate<int> hasAttributes)
                {
                    for (int index = 0; index < vertices.Length; index++)
                    {
                        if (Vector3.Distance(vertices[index], expected) < 1e-5f && hasAttributes(index))
                            return index;
                    }
                    return -1;
                }

                Assert.That(FindVertex(firstDeck, index => region[index] == Vector3.right), Is.GreaterThanOrEqualTo(0));
                Assert.That(FindVertex(lastDeck, index => region[index] == Vector3.right &&
                    Mathf.Abs(uv[index].x - 10f / tileSize) < 1e-5f), Is.GreaterThanOrEqualTo(0));
                Vector3 wallRegion = untiled ? Vector3.up : Vector3.zero;
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionControllerHeading ||
                    headingMode == SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets)
                    Assert.That(FindVertex(new Vector3(6f, 0f, 0f), index => region[index] == wallRegion),
                        Is.GreaterThanOrEqualTo(0));
                int rim = FindVertex(end.translation, index => uv[index].y < 0f);
                Assert.That(rim, Is.GreaterThanOrEqualTo(0));
                Vector4 tangent = mesh.tangents[rim];
                var tangentDirection = new Vector3(tangent.x, tangent.y, tangent.z);
                Assert.That(tangentDirection.magnitude, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(Vector3.Dot(tangentDirection, mesh.normals[rim]), Is.Zero.Within(1e-5f));
                for (int row = 0; row < TransitionBrush.ProfilePoints; row++)
                {
                    Vector3 preview = profile.Vertex(end.translation, across, row, out float distance);
                    Assert.That(FindVertex(preview, index =>
                        Mathf.Abs(uv[index].y - distance / tileSize) < 1e-5f &&
                        (row < TransitionBrush.WallStart || region[index] == wallRegion)), Is.GreaterThanOrEqualTo(0),
                        $"Front profile point {row} is missing or has the wrong surface coordinates.");
                }
                brush.FinalizeSolitaryBrush();
            }
            finally
            {
                Object.DestroyImmediate(parent);
                if (mesh != null)
                    Object.DestroyImmediate(mesh);
                App.Instance = previousApp;
                Config.m_SingletonState = previousConfig;
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, 0f, 8f, false)]
        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, 6.5f * TransitionBrush.UnitsPerInch, 8f,
            false, -1f, false, -1, false, 13)]
        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, -6.5f * TransitionBrush.UnitsPerInch, 8f,
            false, -1f, false, -1, false, -13)]
        [TestCase("SteelTransition", "Steel Transition", 0, 0f, 0f, 8f, true)]
        [TestCase("SteelTransition", "Steel Transition", 0, 0f, 0f, 8f, true, 12.5f)]
        [TestCase("SteelTransition", "Steel Transition", 0, 0f, 0f, 8f, true, 18.5f, true)]
        [TestCase("SteelTransition", "Steel Transition", 0, 0f, 0f, 8f, true, 12.5f, true, -1, false, 0, 13)]
        [TestCase("PoolCoping", "Pool Coping", 15, -10f, 8f - 2.25f * TransitionBrush.UnitsPerInch, 8f, false)]
        [TestCase("PoolTile", "Pool Tile", 2, 0f, 8f - TransitionBrush.TileBandWidth, 8f, false)]
        [TestCase("SteelCoping", "Steel Coping", 17, -1f, 8f - TransitionBrush.SteelRadius,
            8f + TransitionBrush.SteelRadius, true)]
        [TestCase("SteelCoping", "Steel Coping", 17, -3.25f, 8f - 3.25f * TransitionBrush.UnitsPerInch,
            8f + 3.25f * TransitionBrush.UnitsPerInch, true, -1f, false, -1, false, 0, 13)]
        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, 0f, 8f, false, 12.5f, true, 0, true)]
        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, 0f, 8f, false, 0f, true, 1, false)]
        [TestCase("ConcreteTransition", "Concrete Transition", 0, 0f, 0f, 8f, true, 12.5f, true, 2, true)]
        public void ConstructionBrushAppearsInTheManifestAndDrawsOnlyItsOwnSurfaces(string asset, string name,
            int standaloneRows, float standaloneLeftInches, float minHeight, float maxHeight, bool steel,
            float deckInches = -1f, bool savedDeck = false, int copingOption = -1, bool tileOption = false,
            int floorHeightHalfInches = 0, int steelDiameterHalfInches = 0)
        {
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                $"Assets/Resources/Brushes/Basic/Transition/{asset}.asset");
            var manifest = AssetDatabase.LoadAssetAtPath<TiltBrushManifest>("Assets/Manifest.asset");
            Assert.That(manifest.Brushes, Does.Contain(descriptor));
            Assert.That(descriptor.Description, Is.EqualTo(name));
            Assert.That(descriptor.m_ButtonTexture, Is.Not.Null);
            Assert.That(TransitionBrush.HasDeck(descriptor), Is.EqualTo(asset == "SteelTransition"));

            var scene = SceneManager.GetSceneByPath("Assets/Scenes/Main.unity");
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Additive);
            var previousApp = App.Instance;
            var previousConfig = Config.m_SingletonState;
            var parent = new GameObject("Construction brush test canvas");
            Mesh mesh = null;
            SceneScript sketchScene = null;
            int previousFloorHeight = 0;
            try
            {
                App.Instance = scene.GetRootGameObjects().Select(g => g.GetComponent<App>()).First(a => a != null);
                Config.m_SingletonState = parent.AddComponent<Config>();
                sketchScene = App.Scene;
                previousFloorHeight = sketchScene.FloorHeightHalfInches;
                sketchScene.RestoreFloorHeight(floorHeightHalfInches);
                var start = TrTransform.T(Vector3.up * 8f);
                LogAssert.Expect(LogType.Error,
                    "Instantiating mesh due to calling MeshFilter.mesh during edit mode. This will leak meshes. Please use MeshFilter.sharedMesh instead.");
                var brush = BaseBrushScript.Create(parent.transform, start, descriptor, Color.gray, 6f);
                if (steelDiameterHalfInches > 0 && !savedDeck)
                    ((TransitionBrush)brush).NewStrokeSteelDiameterHalfInches = steelDiameterHalfInches;
                var end = TrTransform.T(new Vector3(0f, 8f, 10f));
                float pressure = 1f;
                if (savedDeck)
                {
                    pressure = TransitionBrush.EncodeDeckDepth(deckInches);
                    var flags = SketchMemoryScript.StrokeFlags.TransitionVariableDeck;
                    if (steelDiameterHalfInches > 0)
                        flags = TransitionBrush.FlagsWithSteelDiameter(flags, steelDiameterHalfInches);
                    if (copingOption >= 0)
                    {
                        var options = new TransitionBrush.SurfaceOptions((TransitionBrush.CopingKind)copingOption,
                            tileOption, deckInches > 0f);
                        flags = TransitionBrush.FlagsWithSurfaceOptions(flags, options);
                        var loaded = TransitionBrush.OptionsFor(descriptor, new StrokeData { m_Flags = flags });
                        Assert.That(loaded.Coping, Is.EqualTo(options.Coping));
                        Assert.That(loaded.Tile, Is.EqualTo(options.Tile));
                        Assert.That(loaded.Deck, Is.EqualTo(options.Deck));
                    }
                    brush.SetStrokeData(new StrokeData
                    {
                        m_Flags = flags,
                        m_ControlPoints = new[]
                        {
                            new PointerManager.ControlPoint { m_Pos = start.translation, m_Orient = start.rotation, m_Pressure = pressure },
                            new PointerManager.ControlPoint { m_Pos = end.translation, m_Orient = end.rotation, m_Pressure = pressure }
                        }
                    });
                }
                else if (deckInches >= 0)
                {
                    ((TransitionBrush)brush).NewStrokeDeckDepthInches = deckInches;
                }
                brush.UpdatePosition_LS(end, pressure);
                brush.ApplyChangesToVisuals();
                mesh = brush.GetComponent<MeshFilter>().sharedMesh;
                if (standaloneRows > 0)
                {
                    Assert.That(mesh.vertexCount, Is.EqualTo(2 * standaloneRows));
                    Assert.That(mesh.bounds.min.x,
                        Is.EqualTo(standaloneLeftInches * TransitionBrush.UnitsPerInch).Within(1e-5f));
                }
                Assert.That(mesh.bounds.min.y, Is.EqualTo(minHeight).Within(1e-5f));
                Assert.That(mesh.bounds.max.y, Is.EqualTo(maxHeight).Within(1e-5f));
                Assert.That(mesh.GetIndexCount(0), Is.GreaterThan(0));
                var region = new List<Vector3>();
                mesh.GetUVs(1, region);
                Assert.That(region.Any(uv => uv.z > 0.5f), Is.EqualTo(steel));
                Assert.That(region.Any(uv => uv.x > 0.5f), Is.EqualTo(deckInches > 0));
                Assert.That(region.Any(uv => uv.y > 0.5f), Is.EqualTo(TransitionBrush.IsTransition(descriptor)));
                if (tileOption)
                    Assert.That(region, Does.Contain(Vector3.zero));
                if (steel)
                {
                    var vertices = mesh.vertices;
                    var tube = vertices.Where((_, index) => region[index].z > 0.5f).ToArray();
                    float expectedDiameter = steelDiameterHalfInches > 0
                        ? steelDiameterHalfInches * 0.5f * TransitionBrush.UnitsPerInch
                        : 2f * TransitionBrush.SteelRadius;
                    Assert.That(tube.Max(vertex => vertex.y) - tube.Min(vertex => vertex.y),
                        Is.EqualTo(expectedDiameter).Within(1e-5f));
                    int[] triangles = mesh.triangles;
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        Assert.That(region[triangles[i]], Is.EqualTo(region[triangles[i + 1]]));
                        Assert.That(region[triangles[i]], Is.EqualTo(region[triangles[i + 2]]));
                    }
                }
            }
            finally
            {
                if (sketchScene != null)
                    sketchScene.RestoreFloorHeight(previousFloorHeight);
                Object.DestroyImmediate(parent);
                if (mesh != null)
                    Object.DestroyImmediate(mesh);
                App.Instance = previousApp;
                Config.m_SingletonState = previousConfig;
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }
    }
}
