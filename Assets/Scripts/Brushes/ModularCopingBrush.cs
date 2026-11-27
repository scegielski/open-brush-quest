// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;
using ControlPoint = TiltBrush.PointerManager.ControlPoint;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    internal enum ModularCopingPiece { Concave, Convex }

    /// A concrete bullnose module. Its first saved point anchors the mesh,
    /// and both saved points carry the tangent frames used to join neighboring pieces.
    public sealed class ModularCopingBrush : GeometryBrush
    {
        internal const string BrushName = "Modular Coping";
        internal const float WidestChordInches = 24.5f;
        internal const float DepthInches = 12.5f;
        private const float kNoseHeightInches = 2.25f;
        private const float kHeelHeightInches = 1.375f;
        private const float kUnitsPerInch = TransitionBrush.UnitsPerInch;
        private const float kConcreteRepeat = 0.75f * App.METERS_TO_UNITS;
        internal const float MortarThickness = 0.005f * App.METERS_TO_UNITS;
        private const StrokeFlags kMortarStart = (StrokeFlags)(1 << 22);
        private const StrokeFlags kMortarEnd = (StrokeFlags)(1 << 23);
        private const int kLengthShift = 24;
        private const float kLegacyConcaveAngle = 33.5f;
        private const float kConcaveAngle = 38f; // Estimated from the heel arc in the reference photo.
        private static readonly Vector2[] kProfile = CreateProfile();
        private static readonly PieceMesh[,] kFullMeshes = new PieceMesh[3, 4];
        private static (ModularCopingPiece Piece, int Length, int Mortar) s_PartialKey;
        private static PieceMesh s_PartialMesh;

        private readonly struct Sweep
        {
            private readonly float m_TurnDegrees;
            private readonly float m_SignedRadius;
            public float Degrees => Mathf.Abs(m_TurnDegrees);
            public float ArcLength => Degrees * Mathf.Deg2Rad * Mathf.Abs(m_SignedRadius);

            public Sweep(ModularCopingPiece piece, StrokeFlags flags)
            {
                bool convex = piece == ModularCopingPiece.Convex;
                float fullAngle = convex ? 60f : HasSavedLength(flags) ? kConcaveAngle : kLegacyConcaveAngle;
                float outerRadius = WidestChordInches / (2f * Mathf.Sin(fullAngle * Mathf.Deg2Rad * 0.5f));
                m_SignedRadius = (convex ? -outerRadius : outerRadius - DepthInches) * kUnitsPerInch;
                m_TurnDegrees = (convex ? -fullAngle : fullAngle) * LengthPercent(flags) * 0.01f;
            }

            public TrTransform Station(float t)
            {
                Quaternion rotation = Quaternion.AngleAxis(t * m_TurnDegrees, Vector3.up);
                Vector3 position = m_SignedRadius * (Vector3.right - rotation * Vector3.right);
                return TrTransform.TR(position, rotation);
            }
        }

        private sealed class PieceMesh
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector4> Tangents = new();
            public readonly List<Vector2> UVs = new();
            public readonly List<Vector3> Surfaces = new();
            public readonly List<int> Triangles = new();

            public void AddVertex(TrTransform frame, Vector2 profile, Vector3 normal,
                Vector3 tangent, float tangentSign, Vector2 uv, bool mortar = false)
            {
                Vector3 local = new Vector3(-profile.x, profile.y - kNoseHeightInches, 0f) * kUnitsPerInch;
                Vertices.Add(frame * local);
                Normals.Add(frame.rotation * normal);
                tangent = frame.rotation * tangent;
                Tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, tangentSign));
                UVs.Add(uv);
                Surfaces.Add(mortar ? Vector3.up * 2f : Vector3.up);
            }
        }

        public ModularCopingBrush() : base(bCanBatch: true, upperBoundVertsPerKnot: 1200,
            bDoubleSided: false, bSmoothPositions: false) { }

        internal static bool IsModular(BrushDescriptor brush) => brush?.m_DurableName == BrushName;

        internal static StrokeFlags FlagsFor(ModularCopingPiece piece, int lengthPercent = 100) =>
            (piece == ModularCopingPiece.Convex ? StrokeFlags.ModularCopingConvex : StrokeFlags.None) |
            (StrokeFlags)(Mathf.Clamp(lengthPercent, 1, 100) << kLengthShift);

        private static bool HasSavedLength(StrokeFlags flags) => (flags & StrokeFlags.ModularCopingLengthMask) != 0;

        internal static int LengthPercent(StrokeFlags flags) => HasSavedLength(flags)
            ? Mathf.Min(100, (int)(flags & StrokeFlags.ModularCopingLengthMask) >> kLengthShift) : 100;

        internal static ModularCopingPiece PieceFor(StrokeData stroke) =>
            ((stroke?.m_Flags ?? StrokeFlags.None) & StrokeFlags.ModularCopingConvex) != 0
                ? ModularCopingPiece.Convex : ModularCopingPiece.Concave;

        internal static StrokeFlags MortarFlags(bool atEnd) => atEnd ? kMortarEnd : kMortarStart;

        internal static bool HasMortar(StrokeData stroke, bool end) =>
            stroke != null && (stroke.m_Flags & MortarFlags(end)) != 0;

        internal static TrTransform Connector(ModularCopingPiece piece, bool end, StrokeFlags? flags = null) =>
            end ? new Sweep(piece, flags ?? FlagsFor(piece)).Station(1f) : TrTransform.identity;

        internal static TrTransform Midpoint(ModularCopingPiece piece, StrokeFlags? flags = null) =>
            new Sweep(piece, flags ?? FlagsFor(piece)).Station(0.5f);

        internal static ControlPoint[] CreateControlPoints(ModularCopingPiece piece, TrTransform canvasFromPiece,
            uint timestamp, StrokeFlags? flags = null)
        {
            return new[]
            {
                ControlPointAt(canvasFromPiece, timestamp),
                ControlPointAt(canvasFromPiece * Connector(piece, end: true, flags), timestamp)
            };
        }

        private static ControlPoint ControlPointAt(TrTransform frame, uint timestamp) => new()
        {
            m_Pos = frame.translation,
            m_Orient = frame.rotation,
            m_Pressure = 1f,
            m_TimestampMs = timestamp
        };

        internal static Mesh CreatePreviewMesh(ModularCopingPiece piece) =>
            CreatePreviewMesh(piece, FlagsFor(piece), Color.white);

        internal static Mesh CreatePreviewMesh(ModularCopingPiece piece, StrokeFlags flags, Color32 color)
        {
            PieceMesh data = GetMesh(piece, flags);
            var mesh = new Mesh { name = $"{piece} modular coping preview" };
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetTangents(data.Tangents);
            mesh.SetUVs(0, data.UVs);
            var colors = new Color32[data.Vertices.Count];
            System.Array.Fill(colors, color);
            mesh.SetUVs(1, data.Surfaces);
            mesh.colors32 = colors;
            mesh.SetTriangles(data.Triangles, 0);
            return mesh;
        }

        public override GeometryPool.VertexLayout GetVertexLayout(BrushDescriptor desc) => new()
        {
            bUseColors = true,
            bUseNormals = true,
            bUseTangents = true,
            uv0Size = 2,
            uv0Semantic = GeometryPool.Semantic.XyIsUv,
            uv1Size = 3,
            uv1Semantic = GeometryPool.Semantic.XyIsUv
        };

        protected override void InitBrush(BrushDescriptor desc, TrTransform localPointerXf)
        {
            base.InitBrush(desc, localPointerXf);
            m_geometry.Layout = GetVertexLayout(desc);
        }

        public override float GetSpawnInterval(float pressure01) => 0.001f * POINTER_TO_LOCAL;

        protected override void ControlPointsChanged(int firstChanged)
        {
            // The placement tool uses a dedicated preview mesh. One valid
            // anchor is enough to rebuild a stamp when repainting older strokes.
            if (m_StrokeData == null)
            {
                m_geometry.NumVerts = 0;
                m_geometry.m_Tris.SetCount(0);
                return;
            }

            PieceMesh data = GetMesh(PieceFor(m_StrokeData), m_StrokeData.m_Flags);
            ControlPoint first = m_knots[0].point;
            TrTransform canvasFromPiece = TrTransform.TRS(first.m_Pos, first.m_Orient, m_StrokeData.m_BrushScale);
            Color32 color = m_StrokeData.GetColor(0);
            m_geometry.NumVerts = data.Vertices.Count;
            for (int i = 0; i < data.Vertices.Count; i++)
            {
                SetVert(0, i, canvasFromPiece * data.Vertices[i], canvasFromPiece.rotation * data.Normals[i], color, 1f);
                Vector4 tangent = data.Tangents[i];
                SetTangent(0, i, canvasFromPiece.rotation * (Vector3)tangent, tangent.w);
                SetUv0(0, i, data.UVs[i]);
                SetUv1(0, i, data.Surfaces[i]);
            }
            m_geometry.m_Tris.SetCount(data.Triangles.Count);
            for (int i = 0; i < data.Triangles.Count; i++)
                m_geometry.m_Tris[i] = data.Triangles[i];
        }

        private static Vector2[] CreateProfile()
        {
            var profile = new List<Vector2> { new(DepthInches, 0f), new(0.55f, 0f) };
            AddCurve(profile, new Vector2(0.26f, 0f), new Vector2(0f, 0.57f), new Vector2(0f, 1f), 6);
            AddCurve(profile, new Vector2(0f, 1.72f), new Vector2(0.62f, kNoseHeightInches),
                new Vector2(1.4f, kNoseHeightInches), 8);
            AddCurve(profile, new Vector2(2.95f, kNoseHeightInches), new Vector2(4.7f, 1.50f),
                new Vector2(7f, 1.45f), 12);
            AddCurve(profile, new Vector2(8.8f, 1.41f), new Vector2(11f, kHeelHeightInches),
                new Vector2(DepthInches, kHeelHeightInches), 4);
            return profile.ToArray();
        }

        private static void AddCurve(List<Vector2> profile, Vector2 b, Vector2 c, Vector2 d, int steps)
        {
            Vector2 a = profile[profile.Count - 1];
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float s = 1f - t;
                profile.Add(s * s * s * a + 3f * s * s * t * b + 3f * s * t * t * c + t * t * t * d);
            }
        }

        private static PieceMesh GetMesh(ModularCopingPiece piece, StrokeFlags flags)
        {
            bool mortarStart = (flags & kMortarStart) != 0;
            bool mortarEnd = (flags & kMortarEnd) != 0;
            int mortar = (mortarStart ? 1 : 0) | (mortarEnd ? 2 : 0);
            int length = LengthPercent(flags);
            if (length == 100)
            {
                int shape = piece == ModularCopingPiece.Convex ? 2 : HasSavedLength(flags) ? 1 : 0;
                return kFullMeshes[shape, mortar] ??= BuildMesh(new Sweep(piece, flags), mortarStart, mortarEnd);
            }

            // Retain the last cut preview without caching every percentage as the arrows repeat.
            var key = (piece, length, mortar);
            if (s_PartialMesh == null || s_PartialKey != key)
            {
                s_PartialMesh = BuildMesh(new Sweep(piece, flags), mortarStart, mortarEnd);
                s_PartialKey = key;
            }
            return s_PartialMesh;
        }

        private static PieceMesh BuildMesh(Sweep sweep, bool mortarStart, bool mortarEnd)
        {
            var data = new PieceMesh();
            int steps = Mathf.Max(1, Mathf.CeilToInt(sweep.Degrees / 2.5f));
            float arcLength = sweep.ArcLength;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                AddProfileRing(data, sweep.Station(t), t * arcLength / kConcreteRepeat, mortar: false);
                if (i > 0)
                    JoinLastRings(data);
            }

            AddEnd(data, TrTransform.identity, end: false, mortar: mortarStart);
            AddEnd(data, sweep.Station(1f), end: true, mortar: mortarEnd);
            return data;
        }

        private static void AddEnd(PieceMesh data, TrTransform connector, bool end, bool mortar)
        {
            if (!mortar)
            {
                AddCap(data, connector, end, mortar: false);
                return;
            }

            var outer = connector * TrTransform.T(Vector3.forward * (end ? MortarThickness : -MortarThickness));
            AddProfileRing(data, end ? connector : outer, 0f, mortar: true);
            AddProfileRing(data, end ? outer : connector, MortarThickness / kConcreteRepeat, mortar: true);
            JoinLastRings(data);
            AddCap(data, outer, end, mortar: true);
        }

        private static void AddProfileRing(PieceMesh data, TrTransform frame, float u, bool mortar)
        {
            int profileCount = kProfile.Length;
            float perimeter = 0f;
            for (int j = 0; j < profileCount; j++)
            {
                if (j > 0)
                    perimeter += Vector2.Distance(kProfile[j - 1], kProfile[j]);
                Vector2 across = (kProfile[Mathf.Min(j + 1, profileCount - 1)] -
                    kProfile[Mathf.Max(j - 1, 0)]).normalized;
                Vector3 normal = new Vector3(across.y, across.x, 0f);
                data.AddVertex(frame, kProfile[j], normal, Vector3.forward, -1f,
                    new Vector2(u, perimeter * kUnitsPerInch / kConcreteRepeat), mortar);
            }

            // Give the square heel a hard normal shared by the concrete and the joint.
            data.AddVertex(frame, kProfile[profileCount - 1], Vector3.left, Vector3.forward, -1f,
                new Vector2(u, perimeter * kUnitsPerInch / kConcreteRepeat), mortar);
            data.AddVertex(frame, kProfile[0], Vector3.left, Vector3.forward, -1f,
                new Vector2(u, (perimeter + kHeelHeightInches) * kUnitsPerInch / kConcreteRepeat), mortar);
        }

        private static void JoinLastRings(PieceMesh data)
        {
            int rowCount = kProfile.Length + 2;
            int previous = data.Vertices.Count - 2 * rowCount;
            for (int j = 0; j < kProfile.Length - 1; j++)
                AddSideQuad(data.Triangles, previous + j, rowCount);
            AddSideQuad(data.Triangles, previous + kProfile.Length, rowCount);
        }
        private static void AddSideQuad(List<int> triangles, int a, int rowCount)
        {
            int next = a + rowCount;
            triangles.Add(a);
            triangles.Add(a + 1);
            triangles.Add(next);
            triangles.Add(a + 1);
            triangles.Add(next + 1);
            triangles.Add(next);
        }

        private static void AddCap(PieceMesh data, TrTransform frame, bool end, bool mortar)
        {
            int centerIndex = data.Vertices.Count;
            Vector3 normal = end ? Vector3.forward : Vector3.back;
            float sign = end ? -1f : 1f;
            float uvScale = kUnitsPerInch / kConcreteRepeat;
            var center = new Vector2(DepthInches * 0.5f, kHeelHeightInches * 0.5f);
            data.AddVertex(frame, center, normal, Vector3.left, sign, center * uvScale, mortar);
            foreach (Vector2 point in kProfile)
                data.AddVertex(frame, point, normal, Vector3.left, sign, point * uvScale, mortar);

            // The entire profile is visible from this interior point, including
            // the shallow roll between the bullnose and the deck.
            for (int i = 0; i < kProfile.Length; i++)
            {
                int a = centerIndex + 1 + i;
                int b = centerIndex + 1 + (i + 1) % kProfile.Length;
                data.Triangles.Add(centerIndex);
                data.Triangles.Add(end ? a : b);
                data.Triangles.Add(end ? b : a);
            }
        }
    }
}
