// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public void GridSnapsEachEnabledAxisAndPreservesDisabledAxes()
        {
            var point = new Vector3(1.48f, 2.82f, -1.48f);
            Assert.That(FreePaintTool.RoundGridPosition(point, 1f, true, false, true),
                Is.EqualTo(new Vector3(1f, 2.82f, -1f)));
            Assert.That(FreePaintTool.RoundGridPosition(point, 0.5f, true, true, true),
                Is.EqualTo(new Vector3(1.5f, 3f, -1.5f)));
        }

        private readonly List<TrTransform> m_Samples = new();

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
                    m_BrushSize = 0.15f,
                    m_SplineKnotCount = knots.Length,
                    m_SplineControlPoints = legacy ? null : knots,
                    m_ControlPoints = m_Samples.Select(point => new PointerManager.ControlPoint
                    {
                        m_Pos = point.translation,
                        m_Orient = point.rotation,
                        m_Pressure = 1
                    }).ToArray()
                };
                var ordinaryStroke = new StrokeData(stroke) { m_SplineKnotCount = 0, m_SplineControlPoints = null };
                var snapshots = new[]
                {
                    new SketchWriter.AdjustedMemoryBrushStroke { strokeData = stroke },
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
                for (int i = 0; i < knots.Length; i++)
                    Assert.That(SplineCurve.GetAuthoredPoint(loaded[0], i), Is.EqualTo(knots[i]));
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
