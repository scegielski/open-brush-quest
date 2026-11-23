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
        private readonly List<TrTransform> m_Samples = new();

        [Test]
        public void CurvePassesThroughKnotsInOrderAfterMovingOne()
        {
            var knots = new[]
            {
                TrTransform.T(Vector3.zero),
                TrTransform.T(new Vector3(1, 2, 3)),
                TrTransform.T(new Vector3(4, 3, 2))
            };
            SplineCurve.Sample(knots, m_Samples);
            var previous = m_Samples.ToArray();
            knots[1].translation += Vector3.up;
            SplineCurve.Sample(knots, m_Samples);
            Assert.That(m_Samples[0].translation, Is.EqualTo(knots[0].translation));
            Assert.That(m_Samples[m_Samples.Count - 1].translation, Is.EqualTo(knots[2].translation));
            Assert.That(m_Samples.Any(p => p.translation == knots[1].translation), Is.True);
            Assert.That(previous.Any(p => p.translation == knots[1].translation), Is.False);
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

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void SavedSplineKeepsEditableKnots(bool embedBrushGuids, bool closed)
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
                    TrTransform.T(new Vector3(0.2f, 0.8f, 2f)),
                    TrTransform.T(new Vector3(3f, 1f, 2f))
                };
                SplineCurve.Sample(knots, m_Samples, closed);
                var stroke = new StrokeData
                {
                    m_BrushGuid = Guid.NewGuid(),
                    m_Color = Color.cyan,
                    m_BrushScale = 1,
                    m_BrushSize = 0.15f,
                    m_SplineKnotCount = knots.Length,
                    m_ControlPoints = m_Samples.Select(point => new PointerManager.ControlPoint
                    {
                        m_Pos = point.translation,
                        m_Orient = point.rotation,
                        m_Pressure = 1
                    }).ToArray()
                };
                var ordinaryStroke = new StrokeData(stroke) { m_SplineKnotCount = 0 };
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
                Assert.That(loaded[0].m_BrushGuid, Is.EqualTo(stroke.m_BrushGuid));
                for (int i = 0; i < knots.Length; i++)
                    Assert.That(loaded[0].m_ControlPoints[i * SplineCurve.SamplesPerSegment].m_Pos,
                        Is.EqualTo(knots[i].translation));
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
            Assert.That(m_Samples[3 * stride], Is.EqualTo(knots[0]));
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
