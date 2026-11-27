// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TiltBrush
{
    public class TestSplineCurve
    {
        [Test]
        public void GrabbedKnotKeepsItsOrientationUntilTheControllerTurns()
        {
            Quaternion knot = Quaternion.Euler(15f, 35f, 25f);
            Quaternion controller = Quaternion.Euler(20f, 55f, 10f);

            Quaternion initial = SplineCurve.RotateGrabbedKnot(knot, controller, controller);
            Assert.That(Quaternion.Angle(initial, knot), Is.LessThan(0.1f));

            Quaternion turn = Quaternion.Euler(0f, 75f, 0f);
            Quaternion edited = SplineCurve.RotateGrabbedKnot(knot, controller, turn * controller);
            Assert.That(Quaternion.Angle(edited, turn * knot), Is.LessThan(0.1f));
        }

        [Test]
        public void GrabbedTransitionKnotFollowsYawThroughAVerticalControllerPose()
        {
            Quaternion knot = Quaternion.Euler(0f, 65f, 0f);
            Quaternion controller = Quaternion.Euler(85f, 35f, 0f);
            Quaternion tilted = Quaternion.AngleAxis(20f, controller * Vector3.right) * controller;
            Quaternion heading = SplineCurve.RotateGrabbedKnotAroundUp(knot, controller, tilted, knot);
            Assert.That(Quaternion.Angle(heading, knot), Is.LessThan(0.1f));

            Quaternion yaw = Quaternion.AngleAxis(32f, Vector3.up);
            heading = SplineCurve.RotateGrabbedKnotAroundUp(knot, controller, yaw * tilted, knot);
            Assert.That(Quaternion.Angle(heading, yaw * knot), Is.LessThan(0.1f));
            Quaternion inverted = Quaternion.AngleAxis(180f, Vector3.right) * controller;
            Assert.That(SplineCurve.RotateGrabbedKnotAroundUp(knot, controller, inverted, heading),
                Is.EqualTo(heading));
        }

        [TestCase(0f, 7.8f, 15f, true, 0f)]
        [TestCase(0f, 8.2f, 15f, true, 15f)]
        [TestCase(15f, 7.2f, 0f, true, 15f)]
        [TestCase(15f, 6.8f, 0f, true, 0f)]
        [TestCase(359f, 0.5f, 0.5f, false, 0.5f)]
        [TestCase(10f, 10.1f, 10.1f, false, 10f)]
        public void GrabbedTransitionHeadingDoesNotChatterAroundSnapBoundaries(float previous, float desired,
            float quantized, bool snap, float expected)
        {
            Quaternion Yaw(float angle) => Quaternion.AngleAxis(angle, Vector3.up);
            Quaternion heading = SplineCurve.StabilizeGrabbedHeading(Yaw(previous), Yaw(desired), Yaw(quantized), snap);
            Assert.That(Quaternion.Angle(heading, Yaw(expected)), Is.LessThan(0.1f));
        }

        [Test]
        public void GridSnapsEachEnabledAxisAndPreservesDisabledAxes()
        {
            var point = new Vector3(1.48f, 2.82f, -1.48f);
            Assert.That(FreePaintTool.RoundGridPosition(point, 1f, true, false, true),
                Is.EqualTo(new Vector3(1f, 2.82f, -1f)));
            Assert.That(FreePaintTool.RoundGridPosition(point, 0.5f, true, true, true),
                Is.EqualTo(new Vector3(1.5f, 3f, -1.5f)));
        }

        [Test]
        public void CurveSelectionRayHitsTheRearDeckAndEndOfTheRenderedMesh()
        {
            var original = new GameObject("Hidden original stroke");
            var preview = new GameObject("Live spline mesh", typeof(MeshFilter), typeof(MeshRenderer));
            var mesh = new Mesh();
            try
            {
                original.SetActive(false);
                mesh.vertices = new[]
                {
                    new Vector3(-2f, 0f, -1f), new Vector3(2f, 0f, -1f),
                    new Vector3(2f, 2f, -1f), new Vector3(-2f, 2f, -1f),
                    new Vector3(-2f, 0f, 1f), new Vector3(2f, 0f, 1f),
                    new Vector3(2f, 2f, 1f), new Vector3(-2f, 2f, 1f),
                };
                mesh.triangles = new[] { 3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 4, 5, 6, 4, 6, 7 };
                preview.GetComponent<MeshFilter>().sharedMesh = mesh;
                preview.transform.SetPositionAndRotation(new Vector3(2f, 1f, 5f), Quaternion.Euler(8f, 43f, 14f));
                preview.transform.localScale = new Vector3(1.6f, 2f, 0.5f);
                var stroke = new Stroke { m_Type = Stroke.Type.BrushStroke, m_Object = original };
                var picker = new SplineStrokePicker();
                var aims = new[]
                {
                    (new Vector3(-1f, 2f, 0.25f), Vector3.up),
                    (new Vector3(-2f, 1f, 0.25f), Vector3.left),
                    (new Vector3(-0.5f, 1f, 1f), Vector3.forward),
                };
                foreach (var (surface, normal) in aims)
                {
                    picker.BeginQuery();
                    Vector3 point = preview.transform.TransformPoint(surface);
                    Vector3 outward = preview.transform.TransformDirection(normal);
                    var ray = new Ray(point + outward * 3f, -outward);
                    float depth = 2.9f;
                    Assert.That(picker.TryHitStroke(stroke, preview, ray, ref depth), Is.False);
                    depth = 10f;
                    Assert.That(picker.TryHitStroke(stroke, null, ray, ref depth), Is.False);
                    Assert.That(picker.TryHitStroke(stroke, preview, ray, ref depth), Is.True);
                    Assert.That(depth, Is.EqualTo(3f).Within(1e-4f));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(preview);
                UnityEngine.Object.DestroyImmediate(original);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurveSelectionReadsOnlyTheStrokeRangeInAHotOrColdBatch(bool cold)
        {
            var owner = new GameObject("Spline batch selection test");
            var mesh = new Mesh();
            var geometry = new GeometryPool { Layout = new GeometryPool.VertexLayout() };
            try
            {
                geometry.m_Vertices.AddRange(new[]
                {
                    new Vector3(-1f, -1f, 2f), new Vector3(0f, 1f, 2f), new Vector3(1f, -1f, 2f),
                    new Vector3(-1f, -1f, 4f), new Vector3(0f, 1f, 4f), new Vector3(1f, -1f, 4f),
                });
                geometry.m_Tris.AddRange(new[] { 0, 1, 2, 3, 4, 5 });
                mesh.SetVertices(geometry.m_Vertices);
                mesh.SetTriangles(geometry.m_Tris, 0);
                if (cold)
                    geometry.MakeGeometryNotResident(mesh);
                var batch = owner.AddComponent<Batch>();
                typeof(Batch).GetField("m_Geometry", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(batch, geometry);
                Stroke StrokeAt(int index, float z) => new Stroke
                {
                    m_Type = Stroke.Type.BatchedBrushStroke,
                    m_BatchSubset = new BatchSubset
                    {
                        m_ParentBatch = batch, m_Active = true, m_iTriIndex = index, m_nTriIndex = 3,
                        m_Bounds = new Bounds(new Vector3(0f, 0f, z), new Vector3(2f, 2f, 0f)),
                    },
                };
                var far = StrokeAt(3, 4f);
                var near = StrokeAt(0, 2f);
                var picker = new SplineStrokePicker();
                picker.BeginQuery();
                var ray = new Ray(Vector3.zero, Vector3.forward);
                float depth = 5f;
                Assert.That(picker.TryHitStroke(far, null, ray, ref depth), Is.True);
                Assert.That(depth, Is.EqualTo(4f));
                Assert.That(picker.TryHitStroke(near, null, ray, ref depth), Is.True);
                Assert.That(depth, Is.EqualTo(2f));
                Assert.That(picker.TryHitStroke(far, null, ray, ref depth), Is.False);
                Assert.That(geometry.IsGeometryResident, Is.EqualTo(!cold));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                geometry.Destroy();
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void CurveSelectionBeamUsesTheSolidOverlayAndEndsAtItsHit()
        {
            var owner = new GameObject("Spline ray test");
            owner.SetActive(false);
            Material beamMaterial = null;
            try
            {
                var tool = owner.AddComponent<FreePaintTool>();
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var toolType = typeof(FreePaintTool);
                var preview = new GameObject("Spline preview");
                preview.transform.SetParent(owner.transform, false);
                toolType.GetField("m_SplinePreview", fields).SetValue(tool, preview);
                var updateRay = toolType.GetMethod("UpdateSplineSelectionRay", fields);
                var ray = new Ray(new Vector3(2f, 1f, 4f), Vector3.forward);
                updateRay.Invoke(tool, new object[] { ray, 12f, false });
                var beam = preview.GetComponentInChildren<LineRenderer>(true);
                beamMaterial = beam.sharedMaterial;
                Assert.That(beamMaterial.shader.name, Is.EqualTo("Custom/TransitionPreviewLine"));
                Assert.That(UnityEditor.ShaderUtil.ShaderHasError(beamMaterial.shader), Is.False);
                Assert.That(beamMaterial.GetFloat("_Dashed"), Is.Zero);
                Assert.That(beamMaterial.color.b, Is.GreaterThan(beamMaterial.color.r));
                Assert.That(beam.useWorldSpace, Is.True);

                updateRay.Invoke(tool, new object[] { ray, 3f, true });
                Assert.That(beamMaterial.color.r, Is.GreaterThan(beamMaterial.color.b));
                Assert.That(beam.GetPosition(0), Is.EqualTo(ray.origin));
                Assert.That(beam.GetPosition(1), Is.EqualTo(ray.GetPoint(3f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(beamMaterial);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private readonly List<TrTransform> m_Samples = new();

        [Test]
        public void AutoOrientationFollowsTheTangentAndMatchesAtTheLoopSeam()
        {
            var square = new[]
            {
                TrTransform.T(Vector3.zero), TrTransform.T(Vector3.right),
                TrTransform.T(Vector3.right + Vector3.forward), TrTransform.T(Vector3.forward)
            };
            SplineCurve.Sample(square, m_Samples);
            Quaternion first = SplineCurve.GetAutoOrientation(m_Samples, 0, closed: false);
            Quaternion last = SplineCurve.GetAutoOrientation(m_Samples, m_Samples.Count - 1, closed: false);
            Assert.That(Vector3.Dot(first * Vector3.forward, Vector3.right), Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(last * Vector3.forward, Vector3.left), Is.GreaterThan(0.99f));

            SplineCurve.Sample(square, m_Samples, closed: true);
            first = SplineCurve.GetAutoOrientation(m_Samples, 0, closed: true);
            last = SplineCurve.GetAutoOrientation(m_Samples, m_Samples.Count - 1, closed: true);
            Assert.That(Quaternion.Angle(first, last), Is.LessThan(0.01f));
        }

        [Test]
        public void OpenCubicCurveClampsTheEndsAndBlendsTheInteriorControls()
        {
            var knots = new[]
            {
                TrTransform.T(Vector3.zero),
                TrTransform.T(new Vector3(0, 4, 0)),
                TrTransform.T(new Vector3(4, 4, 0)),
                TrTransform.T(new Vector3(4, 0, 0))
            };
            SplineCurve.Sample(knots, m_Samples);
            int middle = m_Samples.Count / 2;
            Assert.That(m_Samples[middle].translation, Is.EqualTo(new Vector3(2, 3, 0)));
            knots[1].translation += 8 * Vector3.up;
            SplineCurve.Sample(knots, m_Samples);
            Assert.That(m_Samples[0].translation, Is.EqualTo(knots[0].translation));
            Assert.That(m_Samples[m_Samples.Count - 1].translation, Is.EqualTo(knots[3].translation));
            Assert.That(m_Samples[middle].translation, Is.EqualTo(new Vector3(2, 6, 0)));
        }

        [Test]
        public void CoincidentKnotsDoNotCreateAZeroLengthStrokeOrInvalidSamples()
        {
            var origin = TrTransform.T(Vector3.zero);
            SplineCurve.Sample(new[] { origin, origin }, m_Samples);
            Assert.That(m_Samples.Count, Is.EqualTo(1));

            SplineCurve.Sample(new[] { origin, origin, TrTransform.T(Vector3.forward) }, m_Samples);
            Assert.That(m_Samples.Count, Is.GreaterThan(2));
            Assert.That(m_Samples.All(p => float.IsFinite(p.translation.z) &&
                p.translation.z >= 0 && p.translation.z <= 1), Is.True);
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void SavedSplineKeepsEditableControls(bool embedBrushGuids, bool closed, bool legacy)
        {
            var scene = SceneManager.GetSceneByPath("Assets/Scenes/Main.unity");
            bool openedScene = !scene.isLoaded;
            if (openedScene)
                scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Additive);
            var previousApp = App.Instance;
            try
            {
                App.Instance = scene.GetRootGameObjects().Select(g => g.GetComponent<App>()).First(a => a != null);
                var knots = new[]
                {
                    TrTransform.T(Vector3.zero),
                    TrTransform.TR(new Vector3(0.2f, 0.8f, 2f), Quaternion.Euler(15, 32, 9)),
                    TrTransform.T(new Vector3(3f, 1f, 2f))
                };
                var deckDepths = new[] { 0f, 58.5f, 91f };
                const float radiusInches = 72.5f;
                if (!legacy)
                {
                    for (int i = 0; i < knots.Length; i++)
                        knots[i].scale = deckDepths[i] / TransitionBrush.DefaultDeckDepthInches;
                }
                SplineCurve.Sample(knots, m_Samples, closed);
                if (legacy)
                {
                    for (int i = 0; i < knots.Length; i++)
                        m_Samples[i * SplineCurve.SamplesPerSegment] = knots[i];
                    if (closed)
                        m_Samples[m_Samples.Count - 1] = knots[0];
                }
                var stroke = new StrokeData
                {
                    m_BrushGuid = Guid.NewGuid(),
                    m_Color = Color.cyan,
                    m_BrushScale = 1,
                    m_BrushSize = legacy ? 0.15f : radiusInches * TransitionBrush.UnitsPerInch,
                    m_SplineKnotCount = knots.Length,
                    m_SplineControlPoints = legacy ? null : knots,
                    m_Flags = legacy ? SketchMemoryScript.StrokeFlags.None : TransitionBrush.FlagsWithSurfaceOptions(
                        SketchMemoryScript.StrokeFlags.TransitionVariableDeck | SketchMemoryScript.StrokeFlags.SplineAutoOrient,
                        new TransitionBrush.SurfaceOptions(TransitionBrush.CopingKind.Steel, tile: true, deck: false)),
                    m_ControlPoints = m_Samples.Select((point, i) => new PointerManager.ControlPoint
                    {
                        m_Pos = point.translation,
                        m_Orient = point.rotation,
                        m_Pressure = legacy ? 1 :
                            SplineCurve.SampleDeckScale(knots, i, closed) *
                            (TransitionBrush.DefaultDeckDepthInches / (float)TransitionBrush.MaxDeckDepthInches)
                    }).ToArray()
                };
                if (!legacy)
                    stroke.m_Flags = TransitionBrush.FlagsWithSteelDiameter(stroke.m_Flags, 13);
                var ordinaryStroke = new StrokeData(stroke) { m_SplineKnotCount = 0, m_SplineControlPoints = null };
                var snapshots = new[]
                {
                    new SketchWriter.AdjustedMemoryBrushStroke { strokeData = stroke, adjustedStrokeFlags = stroke.m_Flags },
                    new SketchWriter.AdjustedMemoryBrushStroke { strokeData = ordinaryStroke }
                };
                using var bytes = new MemoryStream();
                var groupMapping = new GroupIdMapping();
                List<Guid> brushList = null;
                if (embedBrushGuids)
                    SketchWriter.WriteMemory(bytes, snapshots, groupMapping);
                else
                    SketchWriter.WriteMemory(bytes, snapshots, groupMapping, out brushList);
                bytes.Position = 0;
                var loaded = embedBrushGuids
                    ? SketchWriter.GetStrokes(bytes, allowFastPath: true)
                    : SketchWriter.GetStrokes(bytes, brushList.ToArray(), allowFastPath: true,
                        targetLayer: 0, timestampOffset: 0);
                Assert.That(loaded.Count, Is.EqualTo(2));
                Assert.That(SplineCurve.IsEditable(loaded[0]), Is.True);
                Assert.That(SplineCurve.IsClosed(loaded[0]), Is.EqualTo(closed));
                Assert.That(SplineCurve.IsEditable(loaded[1]), Is.False);
                Assert.That(loaded[0].m_SplineControlPoints == null, Is.EqualTo(legacy));
                Assert.That(loaded[0].m_BrushGuid, Is.EqualTo(stroke.m_BrushGuid));
                Assert.That(loaded[0].m_Flags, Is.EqualTo(stroke.m_Flags));
                Assert.That(TransitionBrush.SteelDiameterInchesFor(loaded[0]), Is.EqualTo(legacy ? 2f : 6.5f));
                Assert.That(SplineCurve.AutoOrients(loaded[0]), Is.EqualTo(!legacy));
                Assert.That(loaded[0].m_BrushSize, Is.EqualTo(stroke.m_BrushSize));
                if (!legacy)
                    Assert.That(loaded[0].m_BrushSize / TransitionBrush.UnitsPerInch,
                        Is.EqualTo(radiusInches).Within(1e-4f));
                Assert.That(TransitionBrush.HasVariableDeck(loaded[0]), Is.EqualTo(!legacy));
                int stride = SplineCurve.SamplesPerSegment;
                for (int i = 0; i < knots.Length; i++)
                {
                    TrTransform loadedKnot = SplineCurve.GetAuthoredPoint(loaded[0], i);
                    Assert.That(loadedKnot.translation, Is.EqualTo(knots[i].translation));
                    Assert.That(loadedKnot.rotation, Is.EqualTo(knots[i].rotation));
                    if (!legacy)
                    {
                        // Saved splines read deck depths back from the sampled stroke pressures.
                        Assert.That(TransitionBrush.DecodeDeckDepth(loaded[0].m_ControlPoints[i * stride].m_Pressure),
                            Is.EqualTo(deckDepths[i]).Within(1e-4f));
                    }
                }
                if (!legacy)
                {
                    Assert.That(TransitionBrush.DecodeDeckDepth(loaded[0].m_ControlPoints[stride / 2].m_Pressure),
                        Is.EqualTo(29.25f).Within(1e-4f));
                    Assert.That(TransitionBrush.DecodeDeckDepth(loaded[0].m_ControlPoints[^1].m_Pressure),
                        Is.EqualTo(closed ? deckDepths[0] : deckDepths[^1]).Within(1e-4f));
                }
            }
            finally
            {
                App.Instance = previousApp;
                if (openedScene)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        [Test]
        public void InsertingAControlPointRemapsOnlyTheDividedSegment()
        {
            int stride = SplineCurve.SamplesPerSegment;
            Assert.That(SplineCurve.GetSourceSampleIndex(stride - 1, 4, 5, 2, 0.25f), Is.EqualTo(stride - 1));
            Assert.That(SplineCurve.GetSourceSampleIndex(2 * stride, 4, 5, 2, 0.25f),
                Is.EqualTo(stride + stride / 4));
            Assert.That(SplineCurve.GetSourceSampleIndex(3 * stride, 4, 5, 2, 0.25f), Is.EqualTo(2 * stride));
            Assert.That(SplineCurve.GetSourceSampleIndex(4 * stride, 4, 5, 2, 0.25f), Is.EqualTo(3 * stride));
        }

        [Test]
        public void AppendingAndDeletingPreserveTheRemainingEndpointSamples()
        {
            int stride = SplineCurve.SamplesPerSegment;
            Assert.That(SplineCurve.GetSourceSampleIndex(3 * stride, 3, 4, 3, 0f), Is.EqualTo(2 * stride));
            Assert.That(SplineCurve.GetSourceSampleIndex(stride / 2, 3, 4, 0, 0f), Is.EqualTo(0));
            Assert.That(SplineCurve.GetSourceSampleIndex(3 * stride, 3, 4, 0, 0f), Is.EqualTo(2 * stride));
            Assert.That(SplineCurve.GetSourceSampleIndex(0, 3, 2, 0, 0f), Is.EqualTo(stride));
            Assert.That(SplineCurve.GetSourceSampleIndex(stride, 3, 2, 1, 0f), Is.EqualTo(2 * stride));
            Assert.That(SplineCurve.GetSourceSampleIndex(stride, 3, 2, 2, 0f), Is.EqualTo(stride));
        }

        [Test]
        public void LoopAddsASegmentAndAllowsInsertionAlongTheSeam()
        {
            var knots = new[]
            {
                TrTransform.T(Vector3.zero),
                TrTransform.T(Vector3.right),
                TrTransform.T(Vector3.up)
            };
            SplineCurve.Sample(knots, m_Samples, closed: true);
            int stride = SplineCurve.SamplesPerSegment;
            Assert.That(m_Samples.Count, Is.EqualTo(3 * stride + 1));
            Assert.That(m_Samples[3 * stride], Is.EqualTo(m_Samples[0]));
            Assert.That(Vector3.Distance(m_Samples[0].translation, (Vector3.right + Vector3.up) / 6f),
                Is.LessThan(1e-6f));
            Assert.That(SplineCurve.GetSourceSampleIndex(3 * stride, 3, 4, 3, 0.5f, oldClosed: true),
                Is.EqualTo(2 * stride + stride / 2));
            Assert.That(SplineCurve.GetSourceSampleIndex(4 * stride, 3, 4, 3, 0.5f, oldClosed: true),
                Is.EqualTo(3 * stride));
        }

        [Test]
        public void SplinePanelBindsEveryActionInOneGridWithoutTooltips()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/SplinePanel.prefab");
            var panel = prefab.GetComponent<SplinePanel>();
            var serializedPanel = new UnityEditor.SerializedObject(panel);
            var actions = serializedPanel.FindProperty("m_Actions");
            Assert.That(actions.arraySize, Is.EqualTo(Enum.GetValues(typeof(FreePaintTool.SplineAction)).Length));

            var grid = new[]
            {
                (FreePaintTool.SplineAction.Draw, "Draw"),
                (FreePaintTool.SplineAction.MoveAndRotate, "Edit"),
                (FreePaintTool.SplineAction.SelectCurve, "Select Curve"),
                (FreePaintTool.SplineAction.NewSpline, "New Spline"),
                (FreePaintTool.SplineAction.Append, "Append Point"),
                (FreePaintTool.SplineAction.Insert, "Insert Point"),
                (FreePaintTool.SplineAction.Delete, "Delete Point"),
                (FreePaintTool.SplineAction.DeckDepth, "Deck Depth"),
                (FreePaintTool.SplineAction.ToggleLoop, "Loop: Open"),
                (FreePaintTool.SplineAction.ToggleAutoOrient, "Auto Orient: Off"),
            };
            Assert.That(actions.arraySize, Is.EqualTo(grid.Length));
            for (int slot = 0; slot < grid.Length; ++slot)
            {
                var (action, label) = grid[slot];
                int index = (int)action;
                var button = (TextActionButton)actions.GetArrayElementAtIndex(index).objectReferenceValue;
                Assert.That(button.m_ButtonLabel, Is.EqualTo(label));
                Assert.That(button.gameObject.activeSelf, Is.True, label);
                Assert.That(button.transform.IsChildOf(panel.transform), Is.True, label);
                var position = new Vector3(slot % 2 == 0 ? -0.63f : 0.63f, 0.84f - (slot / 2) * 0.42f, -0.11f);
                Assert.That(Vector3.Distance(button.transform.localPosition, position), Is.LessThan(0.0001f), label);
                Assert.That(button.transform.localScale, Is.EqualTo(new Vector3(1.25f, 1.25f, 1f)), label);
                var serializedButton = new UnityEditor.SerializedObject(button);
                var descriptionType = serializedButton.FindProperty("m_DescriptionType");
                Assert.That(descriptionType.enumNames[descriptionType.enumValueIndex], Is.EqualTo(nameof(DescriptionType.None)), label);
                var calls = serializedButton.FindProperty("m_Action.m_PersistentCalls.m_Calls");
                var call = calls.GetArrayElementAtIndex(0);
                Assert.That(call.FindPropertyRelative("m_Target").objectReferenceValue, Is.EqualTo(panel));
                Assert.That(call.FindPropertyRelative("m_MethodName").stringValue, Is.EqualTo(nameof(SplinePanel.ChooseAction)));
                Assert.That(call.FindPropertyRelative("m_Arguments.m_IntArgument").intValue, Is.EqualTo(index));
            }
        }

        [TestCase(typeof(EraserTool), BaseTool.ToolType.EraserTool, true)]
        [TestCase(typeof(DropperTool), BaseTool.ToolType.DropperTool, true)]
        [TestCase(typeof(SelectionTool), BaseTool.ToolType.SelectionTool, true)]
        [TestCase(typeof(BaseTool), BaseTool.ToolType.Selection, true)]
        [TestCase(typeof(BaseTool), BaseTool.ToolType.ColorPicker, false)]
        public void StrokeToolsRequireFinishedSplines(Type component, BaseTool.ToolType type, bool finishesSpline)
        {
            var owner = new GameObject("Spline tool switch test");
            owner.SetActive(false);
            try
            {
                var tool = (BaseTool)owner.AddComponent(component);
                tool.m_Type = type;
                Assert.That(SketchSurfacePanel.IsSplineFinishingTool(tool), Is.EqualTo(finishesSpline));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        public void SelectCurveKeepsDraftHistoryWhenThereIsNoSavableGeometry(int knotCount, bool allowSelect)
        {
            var owner = new GameObject("Spline selection lifecycle test");
            owner.SetActive(false);
            try
            {
                var tool = owner.AddComponent<FreePaintTool>();
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var toolType = typeof(FreePaintTool);
                var knots = (List<TrTransform>)toolType.GetField("m_SplineKnots", fields).GetValue(tool);
                for (int i = 0; i < knotCount; i++)
                    knots.Add(TrTransform.identity);
                var undo = (Stack<(TrTransform[], bool, bool)>)toolType.GetField("m_SplineDraftUndo", fields)
                    .GetValue(tool);
                undo.Push((Array.Empty<TrTransform>(), false, false));
                toolType.GetField("m_SplineAction", fields).SetValue(tool, FreePaintTool.SplineAction.Draw);

                // Coincident knots have no baked samples. They must leave Draw active when finishing fails.
                var finish = toolType.GetMethod("TryFinishSplineAndStartAnother", fields);
                var result = (bool)finish.Invoke(tool, new object[] { true, FreePaintTool.SplineAction.SelectCurve, true });
                Assert.That(result, Is.EqualTo(allowSelect));
                Assert.That(tool.IsSplineActionSelected(FreePaintTool.SplineAction.SelectCurve), Is.EqualTo(allowSelect));
                Assert.That(knots.Count, Is.EqualTo(knotCount));
                Assert.That(tool.CanUndoSplineDraft, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void ActionMenuMaterialLoadsWithAllFourTiles()
        {
            var material = Resources.Load<Material>("SplineActionMenu");
            Assert.That(material, Is.Not.Null);
            Assert.That(material.shader.name, Is.EqualTo("Custom/TextureNoFogNoCull"));
            Assert.That(material.mainTexture, Is.Not.Null);
            Assert.That(material.mainTexture.width, Is.EqualTo(1024));
            Assert.That(material.mainTexture.height, Is.EqualTo(768));
        }

        [Test]
        public void LightBrushUsesThePassthroughShaderAndItsOriginalTexture()
        {
            var brush = UnityEditor.AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                "Assets/Resources/Brushes/Basic/Light/Light.asset");
            Assert.That(brush.Material.shader.name, Is.EqualTo("Brush/LightPassthrough"));
            Assert.That(brush.Material.mainTexture, Is.Not.Null);
        }
    }
}
