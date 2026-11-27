// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
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
    public class TestTransitionSolidBrush
    {
        private const float kPositionTolerance = 0.0001f;
        private readonly List<Mesh> m_Meshes = new();
        private Scene m_Scene;
        private bool m_OpenedScene;
        private App m_PreviousApp;
        private Config m_PreviousConfig;
        private GameObject m_Parent;
        private int m_PreviousFloorHeight;

        [SetUp]
        public void SetUp()
        {
            const string scenePath = "Assets/Scenes/Main.unity";
            m_Scene = SceneManager.GetSceneByPath(scenePath);
            m_OpenedScene = !m_Scene.isLoaded;
            if (m_OpenedScene)
                m_Scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            m_PreviousApp = App.Instance;
            m_PreviousConfig = Config.m_SingletonState;
            App.Instance = m_Scene.GetRootGameObjects().Select(root => root.GetComponent<App>()).First(app => app != null);
            m_Parent = new GameObject("Solid transition test canvas");
            Config.m_SingletonState = m_Parent.AddComponent<Config>();
            m_PreviousFloorHeight = App.Scene.FloorHeightHalfInches;
            App.Scene.RestoreFloorHeight(0);
        }

        [TearDown]
        public void TearDown()
        {
            App.Scene.RestoreFloorHeight(m_PreviousFloorHeight);
            Object.DestroyImmediate(m_Parent);
            foreach (Mesh mesh in m_Meshes)
                Object.DestroyImmediate(mesh);
            m_Meshes.Clear();
            App.Instance = m_PreviousApp;
            Config.m_SingletonState = m_PreviousConfig;
            if (m_OpenedScene)
                EditorSceneManager.CloseScene(m_Scene, removeScene: true);
        }

        [TestCase("ConcreteTransition", 0, false)]
        [TestCase("Transition", 0, false)]
        [TestCase("PoolCopingTransition", 2, false)]
        [TestCase("SteelTransition", 0, true)]
        public void OpenSweepCapsOnlyTheTwoEnds(string asset, int turns, bool steel)
        {
            float[] stations = { 0f, 3f, 8f, 12f };
            var samples = stations.Select(z => TrTransform.T(new Vector3(0f, 8f, z))).ToArray();
            Mesh mesh = DrawSavedTransition(asset, samples, 6f, turns, deckInches: 36f);
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var regions = new List<Vector3>();
            mesh.GetUVs(1, regions);
            AssertClosedComponent(vertices, triangles, regions, steel: false);
            if (steel)
                AssertClosedComponent(vertices, triangles, regions, steel: true);

            // A straight sweep's end faces are planar, unlike any of its longitudinal faces.
            var concreteFaces = new int[stations.Length];
            var steelFaces = new int[stations.Length];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]];
                Vector3 b = vertices[triangles[i + 1]];
                Vector3 c = vertices[triangles[i + 2]];
                for (int station = 0; station < stations.Length; station++)
                {
                    float z = stations[station];
                    if (!Near(a.z, z) || !Near(b.z, z) || !Near(c.z, z))
                        continue;

                    bool isSteel = regions[triangles[i]].z > 0.5f;
                    (isSteel ? steelFaces : concreteFaces)[station]++;
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    if (station == 0)
                        Assert.That(normal.z, Is.LessThan(0f), "The start cap faces away from the sweep.");
                    if (station == stations.Length - 1)
                        Assert.That(normal.z, Is.GreaterThan(0f), "The end cap faces away from the sweep.");
                }
            }
            Assert.That(concreteFaces[0], Is.GreaterThan(0));
            Assert.That(concreteFaces[^1], Is.GreaterThan(0));
            Assert.That(concreteFaces[1], Is.Zero);
            Assert.That(concreteFaces[2], Is.Zero);
            Assert.That(steelFaces[0] > 0 && steelFaces[^1] > 0, Is.EqualTo(steel));
            Assert.That(steelFaces[1], Is.Zero);
            Assert.That(steelFaces[2], Is.Zero);
        }

        [TestCase("Transition", false)]
        [TestCase("SteelTransition", true)]
        public void ClosedSplineJoinsItsSeamWithoutEndCaps(string asset, bool steel)
        {
            TrTransform[] controls =
            {
                TrTransform.T(new Vector3(35f, 8f, 0f)),
                TrTransform.T(new Vector3(0f, 8f, 35f)),
                TrTransform.T(new Vector3(-35f, 8f, 0f)),
                TrTransform.T(new Vector3(0f, 8f, -35f)),
            };
            var samples = new List<TrTransform>();
            SplineCurve.Sample(controls, samples, closed: true);
            Mesh mesh = DrawSavedTransition(asset, samples, 6f, 2, deckInches: 24f, controls: controls);
            var regions = new List<Vector3>();
            mesh.GetUVs(1, regions);
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            // Caps at the coincident endpoints would introduce extra incident faces at the seam.
            AssertClosedComponent(vertices, triangles, regions, steel: false);
            if (steel)
                AssertClosedComponent(vertices, triangles, regions, steel: true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShortClosedSplineRetainsItsClosingStation(bool draft)
        {
            float inch = TransitionBrush.UnitsPerInch;
            var controls = new[]
            {
                TrTransform.T(new Vector3(0f, 8f, 0f)),
                TrTransform.T(new Vector3(inch, 8f, 0f)),
                TrTransform.T(new Vector3(0f, 8f, inch))
            };
            var samples = new List<TrTransform>();
            SplineCurve.Sample(controls, samples, closed: true);
            Mesh mesh = DrawSavedTransition("ConcreteTransition", samples, 6f, 0, deckInches: 0f,
                controls: draft ? null : controls, draftClosed: draft ? true : null);
            var uvs = mesh.uv;
            var vertices = mesh.vertices;
            float seamU = uvs.Max(uv => uv.x);
            var first = vertices.Where((_, i) => Near(uvs[i].x, 0f)).ToArray();
            var last = vertices.Where((_, i) => Near(uvs[i].x, seamU)).ToArray();
            Assert.That(seamU, Is.GreaterThan(0f));
            Assert.That(last, Is.EqualTo(first), "The last profile meets the first profile.");
        }

        [TestCase("ConcreteTransition", 3f, 54f)]
        [TestCase("Transition", 30f, 84f)]
        [TestCase("SteelTransition", 60f, 48f)]
        public void DeckThicknessStaysFiveInches(string asset, float heightInches, float radiusInches)
        {
            const float deckDepth = 36f;
            float inch = TransitionBrush.UnitsPerInch;
            float height = heightInches * inch;
            var samples = new[] { TrTransform.T(Vector3.up * height), TrTransform.T(new Vector3(0f, height, 12f)) };
            Mesh mesh = DrawSavedTransition(asset, samples, radiusInches * inch, 0, deckDepth);
            var vertices = mesh.vertices;
            float back = vertices.Min(vertex => vertex.x);
            var edge = vertices.Where(vertex => Near(vertex.x, back)).ToArray();
            Assert.That(edge.Max(vertex => vertex.y) - edge.Min(vertex => vertex.y),
                Is.EqualTo(5f * inch).Within(kPositionTolerance));
        }

        [TestCase(4f, 6f)]
        [TestCase(9f, 6f)]
        public void AngularRearWidensTowardTheLevelFloorAndTheDeckOverhangsIt(float height, float radius)
        {
            App.Scene.RestoreFloorHeight(13);
            float floor = App.Scene.FloorHeightCanvasUnits;
            float crown = floor + height;
            var samples = new[] { TrTransform.T(Vector3.up * crown), TrTransform.T(new Vector3(0f, crown, 12f)) };
            Mesh body = DrawSavedTransition("ConcreteTransition", samples, radius, 0, deckInches: 0f);
            float TopBack(Mesh mesh) => mesh.vertices.Where(vertex => Near(vertex.y, crown)).Min(vertex => vertex.x);
            float floorBack = body.vertices.Where(vertex => Near(vertex.y, floor)).Min(vertex => vertex.x);
            float topBack = TopBack(body);
            Assert.That(floorBack, Is.LessThan(topBack - kPositionTolerance));
            Assert.That(body.bounds.min.y, Is.EqualTo(floor).Within(kPositionTolerance));
            Assert.That(HasDownwardFace(body, (a, b, c) => Near(a.y, floor) && Near(b.y, floor) && Near(c.y, floor)),
                Is.True, "The underside lies flat on the sketch floor.");

            const float deckDepth = 36f;
            Mesh withDeck = DrawSavedTransition("ConcreteTransition", samples, radius, 0, deckDepth);
            float deckBack = TopBack(withDeck);
            Assert.That(topBack - deckBack,
                Is.EqualTo(deckDepth * TransitionBrush.UnitsPerInch).Within(kPositionTolerance));
            Assert.That(HasDownwardFace(withDeck, (a, b, c) =>
                Mathf.Min(a.y, b.y, c.y) > floor + kPositionTolerance &&
                Mathf.Min(a.x, b.x, c.x) < topBack - kPositionTolerance), Is.True,
                "The deck has an elevated underside projecting behind the tapered support.");

            var back = withDeck.vertices.Where(vertex => Near(vertex.z, 0f) && vertex.x < 0f).ToArray();
            Assert.That(back.Any(vertex => Near(vertex.x, floorBack) &&
                vertex.y > floor + 0.07f * height && vertex.y < floor + 0.2f * height), Is.True,
                "The short heel rises vertically from the back of the footing.");
            float underside = back.Where(vertex => Near(vertex.x, deckBack) && vertex.y < crown - kPositionTolerance)
                .Max(vertex => vertex.y);
            var rearCorners = back.Where(vertex => vertex.y > floor + kPositionTolerance &&
                vertex.y < underside - kPositionTolerance).Distinct().OrderBy(vertex => vertex.y).ToArray();
            Assert.That(rearCorners, Has.Length.EqualTo(2), "Only the vertical heel and the sharp knee break the rear faces.");
            Assert.That(rearCorners[1].x, Is.GreaterThan(topBack + 0.02f * height),
                "The long face rises to an inset knee, then a straight face returns to the deck underside.");
            var normals = withDeck.normals;
            var kneeNormals = withDeck.vertices.Select((vertex, index) => (Position: vertex, Normal: normals[index]))
                .Where(vertex => Vector3.Distance(vertex.Position, rearCorners[1]) < kPositionTolerance &&
                    Mathf.Abs(vertex.Normal.z) < kPositionTolerance).Select(vertex => vertex.Normal).Distinct().ToArray();
            Assert.That(kneeNormals, Has.Length.EqualTo(2), "Both rear faces keep their own normal at the knee.");
        }

        [Test]
        public void DraftPreviewColorMatchesTheSamePaletteColorAfterBrushCreation()
        {
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                "Assets/Resources/Brushes/Basic/Transition/Transition.asset");
            var paletteColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            LogAssert.Expect(LogType.Error,
                "Instantiating mesh due to calling MeshFilter.mesh during edit mode. This will leak meshes. Please use MeshFilter.sharedMesh instead.");
            var brush = BaseBrushScript.Create(m_Parent.transform, TrTransform.T(Vector3.up), descriptor,
                paletteColor, 54f * TransitionBrush.UnitsPerInch);
            m_Meshes.Add(brush.GetComponent<MeshFilter>().sharedMesh);

            Assert.That(brush.MatchesColor(paletteColor), Is.True);
            Assert.That(brush.MatchesColor(new Color(0.52f, 0.5f, 0.5f, 1f)), Is.False);
        }

        private Mesh DrawSavedTransition(string asset, IReadOnlyList<TrTransform> samples, float radius,
            int turns, float deckInches, TrTransform[] controls = null, bool? draftClosed = null)
        {
            var descriptor = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                $"Assets/Resources/Brushes/Basic/Transition/{asset}.asset");
            Assert.That(descriptor, Is.Not.Null);
            LogAssert.Expect(LogType.Error,
                "Instantiating mesh due to calling MeshFilter.mesh during edit mode. This will leak meshes. Please use MeshFilter.sharedMesh instead.");
            var brush = BaseBrushScript.Create(m_Parent.transform, samples[0], descriptor, Color.gray, radius);
            Mesh mesh = brush.GetComponent<MeshFilter>().sharedMesh;
            m_Meshes.Add(mesh);

            var flags = TransitionBrush.FlagsForQuarterTurns(turns) | StrokeFlags.SplineAutoOrient;
            if (TransitionBrush.OptionsFor(descriptor).Deck || deckInches > 0f)
                flags |= StrokeFlags.TransitionVariableDeck;
            if (asset == "ConcreteTransition" && deckInches > 0f)
                flags = TransitionBrush.FlagsWithSurfaceOptions(flags,
                    new TransitionBrush.SurfaceOptions(TransitionBrush.CopingKind.None, tile: false, deck: true));
            float pressure = TransitionBrush.EncodeDeckDepth(deckInches);
            var points = samples.Select(sample => new PointerManager.ControlPoint
                { m_Pos = sample.translation, m_Orient = sample.rotation, m_Pressure = pressure }).ToArray();
            if (draftClosed.HasValue)
            {
                var transition = (TransitionBrush)brush;
                transition.PreviewClosedSweep = draftClosed;
                transition.NewStrokeAutoOrient = true;
            }
            else
            {
                brush.SetStrokeData(new StrokeData
                {
                    m_Flags = flags,
                    m_SplineKnotCount = controls?.Length ?? 0,
                    m_SplineControlPoints = controls,
                    m_ControlPoints = points
                });
            }
            for (int i = 1; i < samples.Count; i++)
                brush.UpdatePosition_LS(samples[i], pressure);
            brush.ApplyChangesToVisuals();
            brush.FinalizeSolitaryBrush();
            Assert.That(mesh.GetIndexCount(0), Is.GreaterThan(0));
            return mesh;
        }

        private static void AssertClosedComponent(Vector3[] vertices, int[] triangles, List<Vector3> regions, bool steel)
        {
            // Hard edges, materials and texture seams may use separate indices at the same position.
            var welded = new Dictionary<Vector3Int, int>();
            var edges = new Dictionary<(int Start, int End), (int Count, int Balance)>();
            int Weld(int index)
            {
                Vector3Int position = Vector3Int.RoundToInt(vertices[index] / kPositionTolerance);
                if (!welded.TryGetValue(position, out int id))
                {
                    id = welded.Count;
                    welded.Add(position, id);
                }
                return id;
            }

            void AddEdge(int from, int to)
            {
                bool forward = from < to;
                var key = forward ? (from, to) : (to, from);
                edges.TryGetValue(key, out var edge);
                edges[key] = (edge.Count + 1, edge.Balance + (forward ? 1 : -1));
            }

            for (int i = 0; i < triangles.Length; i += 3)
            {
                if ((regions[triangles[i]].z > 0.5f) != steel)
                    continue;

                int a = Weld(triangles[i]);
                int b = Weld(triangles[i + 1]);
                int c = Weld(triangles[i + 2]);
                Assert.That(a != b && b != c && c != a, Is.True, "Welded faces are nondegenerate.");
                AddEdge(a, b);
                AddEdge(b, c);
                AddEdge(c, a);
            }

            Assert.That(edges, Is.Not.Empty);
            var invalid = edges.Where(edge => edge.Value.Count != 2 || edge.Value.Balance != 0).Take(4).ToArray();
            Assert.That(invalid, Is.Empty,
                $"Every welded {(steel ? "steel" : "concrete")} edge must have two incident faces with opposite winding.");
        }

        private static bool HasDownwardFace(Mesh mesh, System.Func<Vector3, Vector3, Vector3, bool> matches)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]];
                Vector3 b = vertices[triangles[i + 1]];
                Vector3 c = vertices[triangles[i + 2]];
                if (matches(a, b, c) && Vector3.Cross(b - a, c - a).y < -kPositionTolerance)
                    return true;
            }
            return false;
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) < kPositionTolerance;
    }
}
