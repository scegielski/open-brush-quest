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
        internal const float CheckerRepeat = 24f * UnitsPerInch;
        internal const int ArcSegments = 24;
        internal const int ProfilePoints = ArcSegments + 2;
        private const int kMaxStations = 65000 / ProfilePoints;
        private const float kMinPositionDelta = 0.0001f;

        private readonly List<Knot> m_Stations = new();
        private bool m_ClosedSweep;
        internal int NewStrokeQuarterTurns { get; set; }
        internal bool TrackControllerForNewStroke { get; set; }
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

        internal static string FormatRadius(float inches)
        {
            int rounded = Mathf.RoundToInt(inches);
            return $"{rounded / 12} ft {rounded % 12} in";
        }

        public override GeometryPool.VertexLayout GetVertexLayout(BrushDescriptor desc)
        {
            return new GeometryPool.VertexLayout
            {
                bUseColors = true,
                bUseNormals = true,
                uv0Size = 2,
                uv0Semantic = GeometryPool.Semantic.XyIsUv
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

        // The cross section starts at the coping and runs inward to a tangent on sketch Y=0.
        // When the coping is above the full quarter circle, row 0 adds the vertical extension.
        internal static Vector2 ProfilePoint(float copingHeight, float radius, int row)
        {
            float height = Mathf.Max(0, copingHeight);
            if (row == 0)
                return new Vector2(0, height);

            float arcHeight = Mathf.Min(height, radius);
            float angle = Mathf.Acos(Mathf.Clamp01(1 - arcHeight / radius));
            if (row == 1)
                return new Vector2(0, arcHeight);
            if (row == ProfilePoints - 1)
                return new Vector2(radius * Mathf.Sin(angle), 0);

            float sampleAngle = angle * (1 - (row - 1f) / ArcSegments);
            return new Vector2(radius * (Mathf.Sin(angle) - Mathf.Sin(sampleAngle)),
                radius * (1 - Mathf.Cos(sampleAngle)));
        }

        internal static Vector3 ProfileVertex(Vector3 coping, Vector3 across, float radius, int row)
        {
            Vector2 profile = ProfilePoint(coping.y, radius, row);
            return new Vector3(coping.x, profile.y, coping.z) + across * profile.x;
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
            for (int station = 0; station < count; station++)
            {
                Vector3 coping = m_Stations[station].point.m_Pos;
                if (station > 0)
                    totalDistance += Vector3.Distance(coping, m_Stations[station - 1].point.m_Pos);
                Vector3 heading = GetStationHeading(station, tracksController, hasOffsets, fixedHeading);
                Vector3 across = Across(heading, turns);
                for (int row = 0; row < ProfilePoints; row++)
                    vertices[station * ProfilePoints + row] = ProfileVertex(coping, across, radius, row);
            }

            float repeatsPerUnit = closed
                ? Mathf.Max(1, Mathf.RoundToInt(totalDistance / CheckerRepeat)) / totalDistance
                : 1f / CheckerRepeat;
            float distance = 0f;
            for (int station = 0; station < count; station++)
            {
                Vector3 coping = m_Stations[station].point.m_Pos;
                if (station > 0)
                    distance += Vector3.Distance(coping, m_Stations[station - 1].point.m_Pos);
                float straightLength = Mathf.Max(0f, coping.y - radius);
                float arcStep = radius * Mathf.Acos(Mathf.Clamp01(1 - Mathf.Max(0f, coping.y) / radius)) / ArcSegments;
                int first = station * ProfilePoints;
                int previous = station > 0 ? station - 1 : (closed ? count - 2 : 0);
                int next = station < count - 1 ? station + 1 : (closed ? 1 : station);
                for (int row = 0; row < ProfilePoints; row++)
                {
                    Vector3 along = vertices[next * ProfilePoints + row] - vertices[previous * ProfilePoints + row];
                    int upper = Mathf.Max(0, row - 1);
                    int lower = Mathf.Min(ProfilePoints - 1, row + 1);
                    // The extra top row coincides with the arc when there is no vertical face.
                    if (row == 0 && vertices[first] == vertices[first + 1])
                        lower = 2;
                    Vector3 down = vertices[first + lower] - vertices[first + upper];
                    Vector3 normal = (reverseWinding ? Vector3.Cross(down, along) : Vector3.Cross(along, down)).normalized;
                    SetVert(first, row, vertices[first + row], normal == Vector3.zero ? Vector3.up : normal,
                        m_Stations[station].color, 1f);
                    float profileDistance = row == 0 ? 0f : straightLength + (row - 1) * arcStep;
                    SetUv0(first, row, new Vector2(distance * repeatsPerUnit, profileDistance / CheckerRepeat));
                    if (station == count - 1 || row == ProfilePoints - 1)
                        continue;
                    // The last ring has the same positions as the first ring of a loop, with its own UVs.
                    int nextFirst = first + ProfilePoints;
                    int top = first + row;
                    int nextTop = nextFirst + row;
                    // Weld each collapsed top row to its arc.
                    if (row == 0 && vertices[top] == vertices[top + 1])
                        top++;
                    if (row == 0 && vertices[nextTop] == vertices[nextTop + 1])
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
