// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    public sealed class TransitionBrush : GeometryBrush
    {
        internal const string BrushName = "Transition";
        internal const int MinRadiusInches = 1;
        internal const int MaxRadiusInches = 600;
        private const int kDefaultRadiusInches = 72;
        private const string kRadiusPreference = "TransitionRadiusInches";
        private const string kSweepLeftPreference = "TransitionSweepLeft";
        private const string kOrientationPreference = "TransitionQuarterTurns";
        internal const float UnitsPerInch = 0.0254f * App.METERS_TO_UNITS;
        private const float kConcreteRepeat = 3f * App.METERS_TO_UNITS;
        private const int kNoseSegments = 8;
        private const int kArcSegments = 24;
        private const float kCopingWidth = 10f * UnitsPerInch;
        private const float kNoseRadius = 0.75f * UnitsPerInch;
        internal const int DefaultDeckDepthInches = 36;
        internal const int MaxDeckDepthInches = 240;
        private const int kCopingStart = 2;
        internal const int TopOfCoping = kCopingStart + 1;
        internal const int WallStart = TopOfCoping + kNoseSegments;
        internal const int ProfilePoints = WallStart + kArcSegments + 2;
        private const int kMaxStations = 65000 / ProfilePoints;
        private const float kMinPositionDelta = 0.0001f;

        private readonly List<Knot> m_Stations = new();
        private bool m_ClosedSweep;
        internal int NewStrokeQuarterTurns { get; set; }
        internal bool TrackControllerForNewStroke { get; set; }
        internal float? PreviewStartDeckPressure { get; set; }
        internal Quaternion CurrentControllerOrientation { get; set; }
        internal int QuarterTurns => m_StrokeData == null ? NewStrokeQuarterTurns :
            QuarterTurnsFromFlags(m_StrokeData.m_Flags);
        internal Quaternion? StrokeOrientation => GetSavedOrientation(m_StrokeData);
        internal bool TracksController => m_StrokeData == null ? TrackControllerForNewStroke :
            HasControllerHeading(m_StrokeData);

        internal static bool HasControllerHeading(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionControllerHeading) != 0;
        internal static bool HasHeadingOffsets(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionHeadingOffsets) != 0;
        internal static bool HasVariableDeck(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionVariableDeck) != 0;

        internal static float DecodeDeckDepth(float pressure) =>
            float.IsFinite(pressure) ? Mathf.Clamp01(pressure) * MaxDeckDepthInches : DefaultDeckDepthInches;

        internal static Quaternion? GetSavedOrientation(StrokeData stroke)
        {
            if (stroke == null || (stroke.m_Flags & StrokeFlags.TransitionFixedHeading) == 0 ||
                stroke.m_ControlPoints == null || stroke.m_ControlPoints.Length == 0)
                return null;
            return HorizontalOrientation(stroke.m_ControlPoints[0].m_Orient);
        }

        internal static Quaternion HorizontalOrientation(Quaternion orientation)
        {
            return Quaternion.LookRotation(HorizontalHeading(orientation), Vector3.up);
        }

        private static Vector3 HorizontalHeading(Quaternion orientation)
        {
            Vector3 heading = Horizontal(orientation * Vector3.forward);
            if (heading.sqrMagnitude < 0.01f)
                heading = Vector3.Cross(orientation * Vector3.right, Vector3.up);
            return heading.normalized;
        }

        internal static int PreferredQuarterTurns
        {
            get => PlayerPrefs.GetInt(kOrientationPreference, PlayerPrefs.GetInt(kSweepLeftPreference, 1) == 0 ? 0 : 2) & 3;
            set => PlayerPrefs.SetInt(kOrientationPreference, value & 3);
        }

        internal static StrokeFlags FlagsForQuarterTurns(int turns) =>
            ((turns & 2) != 0 ? StrokeFlags.TransitionSweepLeft : StrokeFlags.None) |
            ((turns & 1) != 0 ? StrokeFlags.TransitionSweepQuarterTurn : StrokeFlags.None);

        internal static int QuarterTurnsFromFlags(StrokeFlags flags) =>
            ((flags & StrokeFlags.TransitionSweepLeft) != 0 ? 2 : 0) +
            ((flags & StrokeFlags.TransitionSweepQuarterTurn) != 0 ? 1 : 0);

        public TransitionBrush() : base(bCanBatch: true, upperBoundVertsPerKnot: ProfilePoints,
            bDoubleSided: false, bSmoothPositions: false) { }

        internal static bool IsTransition(BrushDescriptor brush) => brush != null && brush.m_DurableName == BrushName;

        internal static int RadiusInches
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(kRadiusPreference, kDefaultRadiusInches),
                MinRadiusInches, MaxRadiusInches);
            set => PlayerPrefs.SetInt(kRadiusPreference, Mathf.Clamp(value, MinRadiusInches, MaxRadiusInches));
        }

        internal static string FormatFeetAndInches(int inches)
        {
            int absolute = Mathf.Abs(inches);
            return $"{(inches < 0 ? "-" : "")}{absolute / 12} ft {absolute % 12} in";
        }

        public override GeometryPool.VertexLayout GetVertexLayout(BrushDescriptor desc)
        {
            return new GeometryPool.VertexLayout
            {
                bUseColors = true,
                bUseNormals = true,
                bUseTangents = true,
                uv0Size = 2,
                uv0Semantic = GeometryPool.Semantic.XyIsUv,
                uv1Size = 3,
                uv1Semantic = GeometryPool.Semantic.XyIsUv
            };
        }

        protected override void InitBrush(BrushDescriptor desc, TrTransform localPointerXf)
        {
            base.InitBrush(desc, localPointerXf);
            m_geometry.Layout = GetVertexLayout(desc);
        }

        public override float GetSpawnInterval(float pressure01) => 0.02f * App.METERS_TO_UNITS * POINTER_TO_LOCAL;
        public override bool IsOutOfVerts() => false; // Station sampling caps the mesh before allocation.
        public override bool ShouldCurrentLineEnd() => false;

        // Sample the coping from the deck side to the bowl, then follow the original transition to Y=0.
        // The rounded lip covers the upper part of the wall without changing the radius or floor run.
        internal readonly struct SurfaceProfile
        {
            private readonly float m_Height, m_Radius, m_Nose, m_Width, m_JoinHeight, m_JoinRun;
            private readonly float m_WallAngle, m_ArcRun, m_FloorRun, m_StraightLength;
            private readonly float m_DeckDepth;

            internal SurfaceProfile(float height, float radius, float deckDepth = DefaultDeckDepthInches * UnitsPerInch)
            {
                m_Height = Mathf.Max(0f, height);
                m_Radius = radius;
                m_Nose = Mathf.Min(kNoseRadius, m_Height * 0.2f);
                m_Width = Mathf.Min(kCopingWidth, m_Height * 4f);
                m_DeckDepth = m_Height > 0f ? deckDepth : 0f;
                m_JoinHeight = m_Height - 2f * m_Nose;
                m_FloorRun = radius * Mathf.Sin(ArcAngle(m_Height, radius));
                m_WallAngle = ArcAngle(m_JoinHeight, radius);
                m_ArcRun = radius * Mathf.Sin(m_WallAngle);
                m_JoinRun = m_FloorRun - m_ArcRun;
                m_StraightLength = Mathf.Max(0f, m_JoinHeight - radius);
            }

            private static float ArcAngle(float height, float radius) =>
                Mathf.Acos(Mathf.Clamp01(1f - height / radius));

            internal Vector2 Sample(int row, out float distance)
            {
                if (row < TopOfCoping)
                {
                    distance = -m_Width - m_Nose * Mathf.PI;
                    if (row == 0)
                        distance -= m_DeckDepth;
                    return new Vector2(-m_Width - (row == 0 ? m_DeckDepth : 0f), m_Height);
                }
                if (row < WallStart)
                {
                    float fraction = (row - TopOfCoping) / (float)kNoseSegments;
                    float angle = fraction * Mathf.PI;
                    distance = -m_Nose * (Mathf.PI - angle);
                    return new Vector2(m_Nose * Mathf.Sin(angle) + m_JoinRun * fraction,
                        m_Height - m_Nose * (1f - Mathf.Cos(angle)));
                }
                if (row == WallStart)
                {
                    distance = 0f;
                    return new Vector2(m_JoinRun, m_JoinHeight);
                }
                float arcFraction = (row - WallStart - 1f) / kArcSegments;
                float sampleAngle = m_WallAngle * (1f - arcFraction);
                distance = m_StraightLength + m_Radius * m_WallAngle * arcFraction;
                if (row == WallStart + 1)
                    return new Vector2(m_JoinRun, Mathf.Min(m_JoinHeight, m_Radius));
                return new Vector2(m_FloorRun - m_Radius * Mathf.Sin(sampleAngle),
                    m_Radius * (1f - Mathf.Cos(sampleAngle)));
            }

            internal Vector3 Vertex(Vector3 coping, Vector3 across, int row, out float distance)
            {
                Vector2 point = Sample(row, out distance);
                return new Vector3(coping.x, point.y, coping.z) + across * point.x;
            }
        }

        internal static Vector3 Across(Vector3 heading, int turns)
        {
            Vector3 right = Vector3.Cross(Vector3.up, heading);
            return turns switch
            {
                1 => -heading,
                2 => -right,
                3 => heading,
                _ => right
            };
        }

        protected override void ControlPointsChanged(int firstChanged)
        {
            m_Stations.Clear();
            int step = Mathf.Max(1, Mathf.CeilToInt((m_knots.Count - 1f) / (kMaxStations - 1)));
            for (int i = 0; i < m_knots.Count; i += step)
                AddStation(m_knots[i]);
            if ((m_knots.Count - 1) % step != 0)
                AddStation(m_knots[m_knots.Count - 1]);

            int count = m_Stations.Count;
            float radius = BaseSize_LS;
            m_geometry.m_Tris.SetCount(0);
            if (count < 2 || !float.IsFinite(radius) || radius <= 0f)
            {
                m_geometry.NumVerts = 0;
                return;
            }

            m_geometry.NumVerts = count * ProfilePoints;
            int turns = QuarterTurns;
            Quaternion? orientation = StrokeOrientation;
            bool tracksController = TracksController;
            bool variableDeck = PreviewStartDeckPressure.HasValue || (m_StrokeData != null && HasVariableDeck(m_StrokeData));
            bool hasOffsets = m_StrokeData != null && HasHeadingOffsets(m_StrokeData);
            Vector3? fixedHeading = orientation.HasValue ? HorizontalHeading(orientation.Value) : null;
            bool reverseWinding = turns >= 2;
            bool closed = count > 3 && (m_Stations[0].point.m_Pos -
                m_Stations[count - 1].point.m_Pos).sqrMagnitude < kMinPositionDelta * kMinPositionDelta;
            if (closed && (tracksController || hasOffsets))
                closed = Quaternion.Angle(m_Stations[0].point.m_Orient, m_Stations[count - 1].point.m_Orient) < 0.1f;
            m_ClosedSweep = closed;
            var vertices = m_geometry.m_Vertices;
            float totalDistance = 0f;
            for (int station = 1; station < count; station++)
                totalDistance += Vector3.Distance(m_Stations[station].point.m_Pos, m_Stations[station - 1].point.m_Pos);

            // Double the previous whole-tile count on loops so the seam stays aligned.
            float repeatsPerUnit = closed
                ? 2f * Mathf.Max(1, Mathf.RoundToInt(totalDistance / (2f * kConcreteRepeat))) / totalDistance
                : 1f / kConcreteRepeat;
            float distance = 0f;
            for (int station = 0; station < count; station++)
            {
                Vector3 coping = m_Stations[station].point.m_Pos;
                if (station > 0)
                    distance += Vector3.Distance(coping, m_Stations[station - 1].point.m_Pos);
                Vector3 heading = GetStationHeading(station, tracksController, hasOffsets, fixedHeading);
                Vector3 across = Across(heading, turns);
                float depthInches = DefaultDeckDepthInches;
                if (variableDeck)
                {
                    // GeometryBrush takes the first moving sample's pressure for the initial knot.
                    float pressure = station == 0
                        ? PreviewStartDeckPressure ?? m_StrokeData.m_ControlPoints[0].m_Pressure
                        : m_Stations[station].point.m_Pressure;
                    depthInches = DecodeDeckDepth(pressure);
                }
                var profile = new SurfaceProfile(coping.y, radius, depthInches * UnitsPerInch * POINTER_TO_LOCAL);
                int first = station * ProfilePoints;
                for (int row = 0; row < ProfilePoints; row++)
                {
                    vertices[first + row] = profile.Vertex(coping, across, row, out float profileDistance);
                    SetUv0(first, row, new Vector2(distance * repeatsPerUnit, profileDistance / kConcreteRepeat));
                    SetUv1(first, row, row < kCopingStart ? Vector3.right : Vector3.zero);
                }
            }

            for (int station = 0; station < count; station++)
            {
                int first = station * ProfilePoints;
                int previous = station > 0 ? station - 1 : (closed ? count - 2 : 0);
                int next = station < count - 1 ? station + 1 : (closed ? 1 : station);
                bool collapsedJoin = vertices[first + WallStart] == vertices[first + WallStart + 1];
                for (int row = 0; row < ProfilePoints; row++)
                {
                    Vector3 along = vertices[next * ProfilePoints + row] - vertices[previous * ProfilePoints + row];
                    int upper = Mathf.Max(0, row - 1);
                    int lower = Mathf.Min(ProfilePoints - 1, row + 1);
                    // The deck and coping share an edge with separate material coordinates.
                    if (row == kCopingStart - 1)
                        lower++;
                    if (row == kCopingStart)
                        upper--;
                    // The join and first arc vertex coincide when there is no vertical face.
                    if (collapsedJoin && row == WallStart)
                        lower++;
                    if (collapsedJoin && row == WallStart + 1)
                        upper--;
                    Vector3 down = vertices[first + lower] - vertices[first + upper];
                    Vector3 normal = (reverseWinding ? Vector3.Cross(down, along) : Vector3.Cross(along, down)).normalized;
                    if (normal == Vector3.zero)
                        normal = Vector3.up;
                    SetVert(first, row, vertices[first + row], normal, m_Stations[station].color, 1f);
                    Vector3 tangent = Vector3.ProjectOnPlane(along, normal);
                    if (tangent.sqrMagnitude < 1e-10f)
                        tangent = Vector3.Cross(normal, Mathf.Abs(normal.z) < 0.9f ? Vector3.forward : Vector3.right);
                    float tangentSign = Vector3.Dot(Vector3.Cross(normal, tangent), down) < 0f ? -1f : 1f;
                    SetTangent(first, row, tangent, tangentSign);
                    if (station == count - 1 || row == ProfilePoints - 1 || row == kCopingStart - 1)
                        continue;
                    // The last ring has the same positions as the first ring of a loop, with its own UVs.
                    int nextFirst = first + ProfilePoints;
                    int top = first + row;
                    int nextTop = nextFirst + row;
                    // Weld each collapsed top row to its arc.
                    if (row == WallStart && collapsedJoin)
                        top++;
                    if (row == WallStart && vertices[nextTop] == vertices[nextTop + 1])
                        nextTop++;
                    AddTriangle(top, nextTop, first + row + 1, reverseWinding);
                    AddTriangle(first + row + 1, nextTop, nextFirst + row + 1, reverseWinding);
                }
            }
            if (m_geometry.m_Tris.Count == 0)
                m_geometry.NumVerts = 0;
        }

        private void AddStation(Knot knot)
        {
            if (m_Stations.Count > 0 && Horizontal(knot.point.m_Pos -
                m_Stations[m_Stations.Count - 1].point.m_Pos).sqrMagnitude < kMinPositionDelta * kMinPositionDelta)
            {
                m_Stations[m_Stations.Count - 1] = knot;
                return;
            }
            m_Stations.Add(knot);
        }

        private Vector3 StationTangent(int station, bool closed)
        {
            int last = m_Stations.Count - 1;
            // Average the heading over several kept points so tiny tip motions cannot spin a whole cross section.
            int span = Mathf.Min(3, Mathf.Max(1, (last - 1) / 2));
            int previous = closed ? (station - span + last) % last : Mathf.Max(0, station - span);
            int next = closed ? (station + span) % last : Mathf.Min(last, station + span);
            Vector3 position = m_Stations[station].point.m_Pos;
            Vector3 arriving = Horizontal(position - m_Stations[previous].point.m_Pos).normalized;
            Vector3 leaving = Horizontal(m_Stations[next].point.m_Pos - position).normalized;
            Vector3 average = (arriving + leaving).normalized;
            return average == Vector3.zero ? (leaving == Vector3.zero ? arriving : leaving) : average;
        }

        private Vector3 GetStationHeading(int station, bool tracksController, bool hasOffsets, Vector3? fixedHeading)
        {
            Quaternion orientation = m_Stations[station].point.m_Orient;
            if (tracksController)
                return HorizontalHeading(orientation);
            Vector3 heading = fixedHeading ?? StationTangent(station, m_ClosedSweep);
            return hasOffsets ? orientation * heading : heading;
        }

        internal Vector3 GetHeadingNear(Vector3 position)
        {
            if (m_Stations.Count < 2)
                return Vector3.zero;
            int nearest = 0;
            float nearestDistance = float.PositiveInfinity;
            for (int i = 0; i < m_Stations.Count; i++)
            {
                float distance = (m_Stations[i].point.m_Pos - position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
            }
            Vector3? fixedHeading = StrokeOrientation is Quaternion orientation ? HorizontalHeading(orientation) : null;
            return GetStationHeading(nearest, TracksController, m_StrokeData != null && HasHeadingOffsets(m_StrokeData),
                fixedHeading);
        }

        private static Vector3 Horizontal(Vector3 vector) => new Vector3(vector.x, 0, vector.z);

        private void AddTriangle(int a, int b, int c, bool reverse)
        {
            var vertices = m_geometry.m_Vertices;
            if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude <= 1e-14f)
                return;
            m_geometry.m_Tris.Add(a);
            m_geometry.m_Tris.Add(reverse ? c : b);
            m_geometry.m_Tris.Add(reverse ? b : c);
        }
    }
}
