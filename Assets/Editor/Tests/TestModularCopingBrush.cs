// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    public sealed class TestModularCopingBrush
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void ManualQuarterTurnsKeepTheFreePlacementAnchorFixed(int quarterTurns)
        {
            var worldFromCanvas = TrTransform.TRS(new Vector3(2f, 1f, 3f), Quaternion.Euler(8f, 30f, 12f), 1.5f);
            var anchor = TrTransform.TRS(new Vector3(4f, 2f, 1f), Quaternion.Euler(14f, 50f, 21f), 0.75f);

            var rotated = FreePaintTool.RotateModularCopingAnchor(worldFromCanvas, anchor, quarterTurns);
            Assert.That(rotated.translation, Is.EqualTo(anchor.translation));
            Assert.That(rotated.scale, Is.EqualTo(anchor.scale));
            Quaternion baseHeading = TransitionBrush.HorizontalOrientation(worldFromCanvas.rotation * anchor.rotation);
            Quaternion expected = Quaternion.AngleAxis(quarterTurns * 90f, Vector3.up) * baseHeading;
            Assert.That(Quaternion.Angle(worldFromCanvas.rotation * rotated.rotation, expected), Is.LessThan(0.1f));
        }

        [TestCase(false, false, 0, 100)]
        [TestCase(false, true, 100, 100)]
        [TestCase(true, false, 100, 100)]
        [TestCase(true, true, 0, 100)]
        [TestCase(false, false, 64, 37)]
        [TestCase(false, true, 0, 37)]
        [TestCase(true, false, 64, 37)]
        [TestCase(true, true, 64, 100)]
        public void AnOppositeShapeJoinsEitherEndWithOneMortarLayer(bool existingEnd, bool existingMortar,
            int existingLength, int addedLength)
        {
            var existing = ModularCopingPiece.Concave;
            var added = ModularCopingPiece.Convex;
            var existingPose = TrTransform.TRS(new Vector3(2f, 3f, 4f), Quaternion.Euler(0f, 30f, 0f), 0.5f);
            var targetFlags = existingLength == 0 ? StrokeFlags.None : ModularCopingBrush.FlagsFor(existing, existingLength);
            if (existingMortar)
                targetFlags |= ModularCopingBrush.MortarFlags(existingEnd);
            var target = new StrokeData
            {
                m_ControlPoints = ModularCopingBrush.CreateControlPoints(existing, existingPose, 0, targetFlags),
                m_BrushScale = existingPose.scale,
                m_Flags = targetFlags,
            };
            var targetFace = target.m_ControlPoints[existingEnd ? 1 : 0];
            var targetSocket = FreePaintTool.GetModularCopingSocket(target, existingEnd);
            bool attachEnd = !existingEnd;
            var placement = FreePaintTool.JoinModularCopingEndpoint(added, targetSocket, existingEnd,
                existingMortar, out var flags, addedLength);
            var extensionStroke = new StrokeData
            {
                m_ControlPoints = ModularCopingBrush.CreateControlPoints(added, placement, 10, flags),
                m_BrushScale = placement.scale,
                m_Flags = flags,
            };
            var addedFace = extensionStroke.m_ControlPoints[attachEnd ? 1 : 0];
            var addedSocket = FreePaintTool.GetModularCopingSocket(extensionStroke, attachEnd);
            Assert.That(ModularCopingBrush.HasMortar(extensionStroke, attachEnd), Is.EqualTo(!existingMortar));
            Assert.That(ModularCopingBrush.HasMortar(extensionStroke, !attachEnd), Is.False);
            Assert.That(ModularCopingBrush.PieceFor(extensionStroke), Is.EqualTo(added));
            Assert.That(ModularCopingBrush.LengthPercent(extensionStroke.m_Flags), Is.EqualTo(addedLength));
            Assert.That(Vector3.Distance(addedSocket.translation, targetSocket.translation), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(addedSocket.rotation, targetSocket.rotation), Is.LessThan(0.1f));

            Vector3 outward = targetFace.m_Orient * (existingEnd ? Vector3.forward : Vector3.back);
            Vector3 expectedGap = outward * (0.005f * App.METERS_TO_UNITS * existingPose.scale);
            Assert.That(Vector3.Distance(addedFace.m_Pos - targetFace.m_Pos, expectedGap), Is.LessThan(0.0001f));
            // The tangent faces away from the seam on the extending side of the existing chain.
            var extension = extensionStroke.m_ControlPoints[attachEnd ? 0 : 1].m_Pos - targetSocket.translation;
            Assert.That(Vector3.Dot(outward, extension), Is.GreaterThan(0f));
        }

        [TestCase(false, false, 38f)]
        [TestCase(false, true, 33.5f)]
        [TestCase(true, false, 60f)]
        [TestCase(true, true, 60f)]
        public void FullPieceKeepsTheMeasuredOuterChord(bool convex, bool legacy, float turnDegrees)
        {
            var piece = convex ? ModularCopingPiece.Convex : ModularCopingPiece.Concave;
            var flags = legacy
                ? (convex ? StrokeFlags.ModularCopingConvex : StrokeFlags.None)
                : ModularCopingBrush.FlagsFor(piece);
            Assert.That(ModularCopingBrush.LengthPercent(flags), Is.EqualTo(100));
            var start = ModularCopingBrush.Connector(piece, end: false, flags);
            var end = ModularCopingBrush.Connector(piece, end: true, flags);
            var midpoint = ModularCopingBrush.Midpoint(piece, flags);
            float inches = TransitionBrush.UnitsPerInch;
            // The rounded lip is the outer edge on the convex piece. The
            // square heel is the outer edge on the concave one.
            var outer = convex ? Vector3.zero : Vector3.left * (12.5f * inches);
            float chord = Vector3.Distance(start * outer, end * outer);
            Assert.That(chord / inches, Is.EqualTo(24.5f).Within(0.0001f));
            Assert.That(Quaternion.Angle(start.rotation, end.rotation), Is.EqualTo(turnDegrees).Within(0.001f));
            Assert.That(Mathf.Sign(midpoint.translation.x), Is.EqualTo(convex ? -1f : 1f));
            Assert.That(Vector3.Distance(start.translation, midpoint.translation),
                Is.EqualTo(Vector3.Distance(midpoint.translation, end.translation)).Within(0.0001f));

            var mesh = ModularCopingBrush.CreatePreviewMesh(piece, flags, Color.white);
            try
            {
                // Find both outer-edge vertices in the actual mesh, including the
                // rounded lip's furthest projection at one inch over the underside.
                Vector3 crossSection = outer + Vector3.up * ((convex ? -1.25f : -2.25f) * inches);
                Vector3[] vertices = mesh.vertices;
                Assert.That(System.Array.Exists(vertices, v => Vector3.Distance(v, start * crossSection) < 0.0001f));
                Assert.That(System.Array.Exists(vertices, v => Vector3.Distance(v, end * crossSection) < 0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(false, 1)]
        [TestCase(false, 37)]
        [TestCase(true, 50)]
        [TestCase(true, 99)]
        public void ShortenedPieceTrimsTheArcAndKeepsBothFullSectionCaps(bool convex, int lengthPercent)
        {
            var piece = convex ? ModularCopingPiece.Convex : ModularCopingPiece.Concave;
            var flags = ModularCopingBrush.FlagsFor(piece, lengthPercent);
            var fullStart = ModularCopingBrush.Connector(piece, end: false);
            var fullEnd = ModularCopingBrush.Connector(piece, end: true);
            var start = ModularCopingBrush.Connector(piece, end: false, flags);
            var end = ModularCopingBrush.Connector(piece, end: true, flags);
            float fullHalfTurn = (convex ? 60f : 38f) * Mathf.Deg2Rad * 0.5f;
            Vector3 outer = convex ? Vector3.zero : Vector3.left * (12.5f * TransitionBrush.UnitsPerInch);
            float expectedChord = 24.5f * Mathf.Sin(fullHalfTurn * (lengthPercent / 100f)) / Mathf.Sin(fullHalfTurn);
            Assert.That(Vector3.Distance(start * outer, end * outer) / TransitionBrush.UnitsPerInch,
                Is.EqualTo(expectedChord).Within(0.0001f));
            var midpoint = ModularCopingBrush.Midpoint(piece, flags);
            Assert.That(Vector3.Distance(start.translation, midpoint.translation),
                Is.EqualTo(Vector3.Distance(midpoint.translation, end.translation)).Within(0.0001f));

            var full = ModularCopingBrush.CreatePreviewMesh(piece);
            var shortened = ModularCopingBrush.CreatePreviewMesh(piece, flags, Color.white);
            try
            {
                foreach (bool atEnd in new[] { false, true })
                {
                    var fullSection = CapProfile(full, atEnd ? fullEnd : fullStart, atEnd);
                    var cutSection = CapProfile(shortened, atEnd ? end : start, atEnd);
                    Assert.That(cutSection.Length, Is.EqualTo(fullSection.Length).And.GreaterThan(2));
                    foreach (Vector3 point in cutSection)
                        Assert.That(fullSection.Any(original => Vector3.Distance(point, original) < 0.0001f), Is.True);
                }
            }
            finally
            {
                Object.DestroyImmediate(shortened);
                Object.DestroyImmediate(full);
            }
        }

        private static Vector3[] CapProfile(Mesh mesh, TrTransform frame, bool end)
        {
            Vector3 axis = frame.rotation * (end ? Vector3.forward : Vector3.back);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            int[] triangles = mesh.triangles;
            var cap = new HashSet<int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                if (Vector3.Dot(normals[a], axis) < 0.9999f || Vector3.Dot(normals[b], axis) < 0.9999f ||
                    Vector3.Dot(normals[c], axis) < 0.9999f)
                    continue;
                Assert.That(Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), axis),
                    Is.GreaterThan(0f));
                cap.Add(a);
                cap.Add(b);
                cap.Add(c);
            }
            return cap.Select(index => frame.inverse * vertices[index]).ToArray();
        }

        [TestCase(false, 100)]
        [TestCase(true, 32)]
        public void SavedPointsMatchAnalyticalConnectorsAfterScaling(bool convex, int lengthPercent)
        {
            var piece = convex ? ModularCopingPiece.Convex : ModularCopingPiece.Concave;
            var pose = TrTransform.TRS(new Vector3(1f, 3f, 5f), Quaternion.Euler(8f, 27f, 13f), 0.5f);
            var flags = ModularCopingBrush.FlagsFor(piece, lengthPercent);
            var points = ModularCopingBrush.CreateControlPoints(piece, pose, 123u, flags);
            Assert.That(points.Length, Is.EqualTo(2));
            for (int i = 0; i < points.Length; i++)
            {
                var expected = pose * ModularCopingBrush.Connector(piece, end: i == 1, flags);
                Assert.That(Vector3.Distance(points[i].m_Pos, expected.translation), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(points[i].m_Orient, expected.rotation), Is.LessThan(0.1f));
                Assert.That(points[i].m_TimestampMs, Is.EqualTo(123u));
            }

            var stroke = new StrokeData { m_Flags = flags };
            Assert.That(ModularCopingBrush.PieceFor(stroke), Is.EqualTo(piece));
            Assert.That(ModularCopingBrush.LengthPercent(flags), Is.EqualTo(lengthPercent));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreviewMeshHasOutwardFacesAndConcreteRouting(bool convex)
        {
            var mesh = ModularCopingBrush.CreatePreviewMesh(convex ? ModularCopingPiece.Convex : ModularCopingPiece.Concave);
            try
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var indices = mesh.triangles;
                var surface = new List<Vector3>();
                mesh.GetUVs(1, surface);
                Assert.That(surface.Count, Is.EqualTo(vertices.Length));
                Assert.That(surface, Is.All.EqualTo(Vector3.up));
                Assert.That(indices.Length, Is.GreaterThan(0));
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    Assert.That(Vector3.Dot(normal, normals[a] + normals[b] + normals[c]), Is.GreaterThan(0f),
                        $"Triangle {i / 3} must be non-degenerate and face out.");
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(false, 100)]
        [TestCase(true, 100)]
        [TestCase(false, 37)]
        [TestCase(true, 37)]
        public void MortarMeshFillsExactlyFiveMillimetersAtTheSnappedEnd(bool end, int lengthPercent)
        {
            var piece = ModularCopingPiece.Convex;
            var flags = ModularCopingBrush.FlagsFor(piece, lengthPercent) | ModularCopingBrush.MortarFlags(end);
            var mesh = ModularCopingBrush.CreatePreviewMesh(piece, flags, Color.gray);
            try
            {
                var joint = ModularCopingBrush.Connector(piece, end, flags);
                Vector3 axis = joint.rotation * (end ? Vector3.forward : Vector3.back);
                var surface = new List<Vector3>();
                mesh.GetUVs(1, surface);
                Vector3[] vertices = mesh.vertices;
                float nearest = float.PositiveInfinity;
                float furthest = float.NegativeInfinity;
                for (int i = 0; i < surface.Count; i++)
                {
                    if (surface[i].y < 1.5f)
                        continue;
                    float offset = Vector3.Dot(vertices[i] - joint.translation, axis);
                    nearest = Mathf.Min(nearest, offset);
                    furthest = Mathf.Max(furthest, offset);
                }
                Assert.That(nearest, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(furthest / App.METERS_TO_UNITS, Is.EqualTo(0.005f).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(false, 0)]
        [TestCase(false, 100)]
        [TestCase(true, 43)]
        public void SavedBrushPrefabRebuildsTheSameMeshAsItsPreview(bool convex, int lengthPercent)
        {
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                "Assets/Resources/Brushes/Basic/Transition/ModularCoping.asset");
            var manifest = AssetDatabase.LoadAssetAtPath<TiltBrushManifest>("Assets/Manifest.asset");
            Assert.That(descriptor, Is.Not.Null);
            Assert.That(manifest.Brushes, Does.Contain(descriptor));
            Assert.That(ModularCopingBrush.IsModular(descriptor), Is.True);

            const string mainScene = "Assets/Scenes/Main.unity";
            var scene = SceneManager.GetSceneByPath(mainScene);
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene(mainScene, OpenSceneMode.Additive);

            var previousApp = App.Instance;
            var previousConfig = Config.m_SingletonState;
            var parent = new GameObject("Modular coping test canvas");
            Mesh mesh = null;
            Mesh preview = null;
            try
            {
                App.Instance = scene.GetRootGameObjects().Select(g => g.GetComponent<App>()).First(a => a != null);
                Config.m_SingletonState = parent.AddComponent<Config>();
                var piece = convex ? ModularCopingPiece.Convex : ModularCopingPiece.Concave;
                var pose = TrTransform.TRS(new Vector3(1f, 3f, 5f), Quaternion.Euler(8f, 27f, 13f), 0.55f);
                var flags = lengthPercent == 0 ? StrokeFlags.None : ModularCopingBrush.FlagsFor(piece, lengthPercent);
                if (lengthPercent is > 0 and < 100)
                    flags |= ModularCopingBrush.MortarFlags(atEnd: true);
                var points = ModularCopingBrush.CreateControlPoints(piece, pose, 10u, flags);
                var stroke = new StrokeData
                {
                    m_BrushGuid = descriptor.m_Guid,
                    m_Flags = flags,
                    m_Color = Color.gray,
                    m_BrushScale = pose.scale,
                    m_BrushSize = descriptor.m_BrushSizeRange.x,
                    m_ControlPoints = points
                };
                using var bytes = new MemoryStream();
                var snapshot = new SketchWriter.AdjustedMemoryBrushStroke { strokeData = stroke, adjustedStrokeFlags = flags };
                SketchWriter.WriteMemory(bytes, new[] { snapshot }, new GroupIdMapping());
                bytes.Position = 0;
                var loaded = SketchWriter.GetStrokes(bytes, allowFastPath: true).Single();
                Assert.That(loaded.m_Flags, Is.EqualTo(flags));
                Assert.That(loaded.m_ControlPoints[1].m_Pos, Is.EqualTo(points[1].m_Pos));

                LogAssert.Expect(LogType.Error,
                    "Instantiating mesh due to calling MeshFilter.mesh during edit mode. This will leak meshes. Please use MeshFilter.sharedMesh instead.");
                var brush = BaseBrushScript.Create(parent.transform, pose, descriptor, stroke.m_Color, stroke.m_BrushSize);
                mesh = brush.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(brush, Is.TypeOf<ModularCopingBrush>());
                brush.SetIsLoading();
                brush.SetStrokeData(loaded);
                foreach (var point in loaded.m_ControlPoints)
                {
                    brush.UpdatePosition_LS(TrTransform.TRS(point.m_Pos, point.m_Orient, pose.scale),
                        point.m_Pressure);
                }
                brush.ApplyChangesToVisuals();
                brush.FinalizeSolitaryBrush();

                preview = ModularCopingBrush.CreatePreviewMesh(piece, flags, Color.gray);
                Assert.That(mesh.vertexCount, Is.EqualTo(preview.vertexCount));
                Assert.That(mesh.triangles, Is.EqualTo(preview.triangles));
                Assert.That(mesh.uv, Is.EqualTo(preview.uv));
                Vector3[] vertices = mesh.vertices;
                Vector3[] previewVertices = preview.vertices;
                for (int i = 0; i < vertices.Length; i++)
                    Assert.That(Vector3.Distance(vertices[i], pose * previewVertices[i]), Is.LessThan(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(parent);
                if (mesh != null)
                    Object.DestroyImmediate(mesh);
                if (preview != null)
                    Object.DestroyImmediate(preview);
                App.Instance = previousApp;
                Config.m_SingletonState = previousConfig;
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }
    }
}
