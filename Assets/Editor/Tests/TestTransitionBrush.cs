// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TiltBrush
{
    public class TestTransitionBrush
    {
        private const string kBrush = "Assets/Resources/Brushes/Basic/Transition/Transition.asset";

        [TestCase(3f, 6f, 3f, 5.196152f)]
        [TestCase(6f, 6f, 6f, 6f)]
        [TestCase(9f, 6f, 6f, 6f)]
        public void ProfileConnectsCopingAndFloorAtTheRequestedRadius(
            float height, float radius, float arcHeight, float run)
        {
            Assert.That(TransitionBrush.ProfilePoint(height, radius, 0), Is.EqualTo(new Vector2(0, height)));
            Assert.That(TransitionBrush.ProfilePoint(height, radius, 1), Is.EqualTo(new Vector2(0, arcHeight)));
            Vector2 floor = TransitionBrush.ProfilePoint(height, radius, TransitionBrush.ProfilePoints - 1);
            Assert.That(floor.y, Is.Zero);
            Assert.That(floor.x, Is.EqualTo(run).Within(1e-5f));

            for (int row = 1; row < TransitionBrush.ProfilePoints; row++)
            {
                Vector2 point = TransitionBrush.ProfilePoint(height, radius, row);
                Assert.That(Vector2.Distance(point, new Vector2(run, radius)), Is.EqualTo(radius).Within(1e-5f));
            }
        }

        [Test]
        public void RadiusDisplaysWholeInchesAcrossFootBoundaries()
        {
            Assert.That(TransitionBrush.FormatFeetAndInches(71), Is.EqualTo("5 ft 11 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(72), Is.EqualTo("6 ft 0 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(73), Is.EqualTo("6 ft 1 in"));
            Assert.That(TransitionBrush.FormatFeetAndInches(-14), Is.EqualTo("-1 ft 2 in"));
        }

        [Test]
        public void ProfileReachesTheFloorOfATransformedCanvas()
        {
            var roomFromCanvas = TrTransform.TRS(new Vector3(4f, 2f, 3f),
                Quaternion.Euler(35f, 50f, 0f), 2.5f);
            var coping = new Vector3(1f, 8f, 2f);
            var across = Vector3.back;
            Vector3 top = roomFromCanvas * TransitionBrush.ProfileVertex(coping, across, 6f, 0);
            Vector3 floor = roomFromCanvas *
                TransitionBrush.ProfileVertex(coping, across, 6f, TransitionBrush.ProfilePoints - 1);

            Assert.That(Vector3.Distance(top, roomFromCanvas * coping), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(floor, roomFromCanvas * new Vector3(1f, 0f, -4f)),
                Is.LessThan(1e-5f));
        }

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        public void PreviewAndMeasurementsRenderAgainstATransparentBackground(bool tipBehindViewer, int turns)
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

                var coping = new Vector3(0f, 30f * TransitionBrush.UnitsPerInch, tipBehindViewer ? -15f : 0f);
                tool.DrawTransitionPreview(TrTransform.identity, coping,
                    Vector3.forward, camera, 72, turns);
                var label = tool.GetComponentInChildren<TMPro.TextMeshPro>();
                Assert.That(label.text, Is.EqualTo($"Radius 6 ft 0 in\nHeight 2 ft 6 in\nOrientation {turns * 90}°"));
                label.ForceMeshUpdate();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0f, 0f, 512f, 512f), 0, 0);
                var pixels = image.GetPixels32();
                Assert.That(pixels.Count(p => p.a > 128 && p.b > 140 && p.g > 140 && p.r < 150), Is.GreaterThan(30),
                    "The arc must write visible cyan pixels with alpha.");
                Assert.That(pixels.Count(p => p.a > 128 && p.r > 200 && p.g > 200 && p.b > 200), Is.GreaterThan(30),
                    "The measurement readout must write visible white pixels with alpha.");

                coping.y = -2f * TransitionBrush.UnitsPerInch;
                tool.DrawTransitionPreview(TrTransform.identity, coping, Vector3.forward, camera, 72, turns);
                Assert.That(label.text, Is.EqualTo($"Radius 6 ft 0 in\nHeight -0 ft 2 in\nOrientation {turns * 90}°"));
            }
            finally
            {
                RenderTexture.active = previousTarget;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(cameraObject);
                var preview = tool.transform.Find("Transition profile preview");
                if (preview != null)
                {
                    Object.DestroyImmediate(preview.GetComponent<LineRenderer>().sharedMaterial);
                    Object.DestroyImmediate(preview.GetComponentInChildren<TMPro.TextMeshPro>().fontSharedMaterial);
                    Object.DestroyImmediate(preview.gameObject);
                }
                tool.gameObject.layer = layer;
                tool.gameObject.SetActive(wasActive);
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
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
            try
            {
                tile.layer = 30;
                tile.GetComponent<MeshFilter>().sharedMesh = mesh;
                tile.GetComponent<MeshRenderer>().sharedMaterial = descriptor.Material;
                mesh.colors32 = Enumerable.Repeat(new Color32(156, 114, 76, 255), mesh.vertexCount).ToArray();

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
            }
            finally
            {
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

        [TestCase(0, 1, 0, 0, 6, 0, 10, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(1, 0, -1, 0, 0, -6, 10, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(2, -1, 0, -6, 0, 0, 10, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(3, 0, 1, 0, 0, 0, 16, SketchMemoryScript.StrokeFlags.None)]
        [TestCase(0, 0, -1, 0, 0, -6, 10, SketchMemoryScript.StrokeFlags.TransitionFixedHeading)]
        [TestCase(0, 0, -1, 0, 6, 0, 10, SketchMemoryScript.StrokeFlags.TransitionControllerHeading)]
        [TestCase(0, 0, -1, 0, 6, 0, 10, SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets)]
        public void BrushPrefabProducesATexturedSurfaceAtItsSavedAngle(
            int turns, int acrossX, int acrossZ, int minX, int maxX, int minZ, int maxZ,
            SketchMemoryScript.StrokeFlags headingMode)
        {
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(kBrush);
            Assert.That(descriptor.Description, Is.EqualTo(TransitionBrush.BrushName));
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
                var flags = TransitionBrush.FlagsForQuarterTurns(turns) | headingMode;
                var points = new[]
                {
                    new PointerManager.ControlPoint { m_Pos = start.translation, m_Orient = start.rotation },
                    new PointerManager.ControlPoint { m_Pos = end.translation, m_Orient = end.rotation }
                };
                Vector3 across = new Vector3(acrossX, 0, acrossZ);
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionFixedHeading)
                    points[0].m_Orient = Quaternion.LookRotation(Vector3.right);
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
                    Vector3 expectedFloor = TransitionBrush.ProfileVertex(end.translation, across, 6f,
                        TransitionBrush.ProfilePoints - 1);
                    Assert.That(Vector3.Distance(drawnVertices[TransitionBrush.ProfilePoints - 1],
                        new Vector3(6f, 0f, 0f)), Is.LessThan(1e-5f));
                    Assert.That(Vector3.Distance(drawnVertices[^1], expectedFloor), Is.LessThan(1e-5f));
                }
                // Recreated strokes take precedence over the current painting orientation.
                transition.NewStrokeQuarterTurns = (turns + 1) % 4;
                transition.TrackControllerForNewStroke = false;
                brush.SetStrokeData(new StrokeData { m_Flags = flags, m_ControlPoints = points });
                brush.ResetBrushForPreview(start);
                brush.UpdatePosition_LS(end, 1f);
                brush.ApplyChangesToVisuals();
                mesh = brush.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.EqualTo(2 * TransitionBrush.ProfilePoints));
                Assert.That(mesh.GetIndexCount(0), Is.GreaterThan(0));
                Assert.That(mesh.bounds.min.y, Is.Zero.Within(1e-5f));
                Assert.That(mesh.bounds.max.y, Is.EqualTo(8f).Within(1e-5f));
                Assert.That(mesh.bounds.min.x, Is.EqualTo(minX).Within(1e-5f));
                Assert.That(mesh.bounds.max.x, Is.EqualTo(maxX).Within(1e-5f));
                Assert.That(mesh.bounds.min.z, Is.EqualTo(minZ).Within(1e-5f));
                Assert.That(mesh.bounds.max.z, Is.EqualTo(maxZ).Within(1e-5f));
                var vertices = mesh.vertices;
                if (headingMode == SketchMemoryScript.StrokeFlags.TransitionControllerHeading ||
                    headingMode == SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets)
                    Assert.That(Vector3.Distance(vertices[TransitionBrush.ProfilePoints - 1],
                        new Vector3(6f, 0f, 0f)), Is.LessThan(1e-5f));
                Vector2[] uv = mesh.uv;
                const float tileSize = 3f * App.METERS_TO_UNITS;
                Assert.That(uv[TransitionBrush.ProfilePoints].x,
                    Is.EqualTo(10f / tileSize).Within(1e-5f));
                Assert.That(uv[TransitionBrush.ProfilePoints - 1].y,
                    Is.EqualTo((2f + 3f * Mathf.PI) / tileSize).Within(1e-5f));
                Vector4 tangent = mesh.tangents[0];
                var tangentDirection = new Vector3(tangent.x, tangent.y, tangent.z);
                Assert.That(tangentDirection.magnitude, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(Vector3.Dot(tangentDirection, mesh.normals[0]), Is.Zero.Within(1e-5f));
                for (int row = 0; row < TransitionBrush.ProfilePoints; row++)
                {
                    Vector3 preview = TransitionBrush.ProfileVertex(new Vector3(0f, 8f, 10f), across, 6f, row);
                    Assert.That(Vector3.Distance(vertices[TransitionBrush.ProfilePoints + row], preview),
                        Is.LessThan(1e-5f));
                }
                int[] triangles = mesh.triangles;
                Vector3 facing = Vector3.Cross(vertices[triangles[1]] - vertices[triangles[0]],
                    vertices[triangles[2]] - vertices[triangles[0]]);
                Assert.That(Vector3.Dot(facing, turns >= 2 ? Vector3.left : Vector3.right), Is.GreaterThan(0f));
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
    }
}
