// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using UnityEngine;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    public sealed partial class TransitionBrush
    {
        private const int kFrontStart = 2;
        private const int kBackPoints = 5;
        private const int kSolidMaxRows = 2 * (ProfilePoints + kBackPoints) + TubePoints;
        private const float kDeckThickness = 5f * UnitsPerInch;
        private Vector2[] m_BodySamples = Array.Empty<Vector2>();
        private float[] m_BodyDistances = Array.Empty<float>();
        private SolidStation[] m_SolidStations = Array.Empty<SolidStation>();
        private readonly List<int> m_CapOutline = new();

        internal bool? PreviewClosedSweep { get; set; }

        private bool? AuthoredSweepClosed => PreviewClosedSweep ??
            (m_StrokeData != null && SplineCurve.IsEditable(m_StrokeData) ? SplineCurve.IsClosed(m_StrokeData) : null);

        protected override bool ForceAllKnots => IsTransition(m_Style) && AuthoredSweepClosed == true;

        private struct SolidStation
        {
            public Vector3 Position;
            public Vector3 Across;
            public Vector3 TubeRight;
            public Vector3 TubeUp;
            public float Floor;
            public float Distance;
        }

        private readonly struct BodyProfile
        {
            public readonly SurfaceProfile Front;
            public readonly bool Pool;
            public readonly float CrownX;
            public readonly float CrownHeight;
            public readonly float CrownDistance;
            public readonly float DeckDepth;
            private readonly float m_DeckUnderside;
            private readonly float m_RearFootX;
            private readonly float m_HeelHeight;
            private readonly float m_NeckHeight;
            private readonly float m_NeckX;

            public BodyProfile(SurfaceProfile front, bool pool, float height, float radius, float deckDepth,
                float deckThickness)
            {
                Front = front;
                Pool = pool;
                // The back widens toward the floor. Limit the batter on tall, tight profiles.
                Vector2 edge = front.Sample(DeckPoints, out float distance);
                CrownX = pool ? edge.x : -Mathf.Min(height * 0.18f, radius * 0.18f);
                CrownHeight = pool ? edge.y : height;
                CrownDistance = pool ? distance : 0f;
                DeckDepth = CrownHeight > 0f ? deckDepth : 0f;
                m_DeckUnderside = DeckDepth > 0f ? CrownHeight - deckThickness : CrownHeight;
                // At very low heights the slab can extend below the floor. Stop the brace at the
                // slab so the floor face and the underside cannot cross.
                float braceHeight = Mathf.Max(0f, m_DeckUnderside);
                float rearRun = Mathf.Min(Mathf.Min(CrownHeight * 0.37f, radius * 0.45f), braceHeight * 0.75f);
                m_RearFootX = CrownX - rearRun;
                m_HeelHeight = 0.15f * braceHeight;
                m_NeckHeight = 0.80f * braceHeight;
                // Keep the recess behind the riding surface, even when the coping is narrow.
                m_NeckX = CrownX + Mathf.Min(0.42f * rearRun, -0.75f * CrownX);
            }

            public int Count => BodyPointCount(Pool);
            private int FrontEnd => Count - kBackPoints;

            public Vector2 Sample(int row, out float distance)
            {
                distance = 0f;
                if (row == 0 || row == Count)
                {
                    distance = CrownDistance - DeckDepth;
                    return new Vector2(CrownX - DeckDepth, CrownHeight);
                }
                if (row == 1)
                {
                    distance = CrownDistance;
                    return new Vector2(CrownX, CrownHeight);
                }
                if (row < FrontEnd)
                    return Front.Sample((Pool ? DeckPoints : WallStart) + row - kFrontStart, out distance);
                return SampleBack(row - FrontEnd);
            }

            // The reference shows a vertical heel and two straight faces that meet at the recessed knee.
            private Vector2 SampleBack(int row) => row switch
            {
                0 => new Vector2(m_RearFootX, 0f),
                1 => new Vector2(m_RearFootX, m_HeelHeight),
                2 => new Vector2(m_NeckX, m_NeckHeight),
                3 => new Vector2(CrownX, m_DeckUnderside),
                _ => new Vector2(CrownX - DeckDepth, m_DeckUnderside)
            };
        }

        private static int BodyPointCount(bool pool) =>
            kFrontStart + (pool ? ProfilePoints - DeckPoints : WallPoints) + kBackPoints;

        private static BodyProfile MakeBodyProfile(SurfaceOptions options, float height, float radius,
            float deckDepth, float tubeRadius, float scale)
        {
            bool pool = options.Coping == CopingKind.Pool;
            float wallHeight = height - Mathf.Min(tubeRadius, height * 0.5f);
            return new BodyProfile(new SurfaceProfile(wallHeight, radius, 0f, pool), pool, wallHeight, radius,
                options.Deck ? deckDepth : 0f, kDeckThickness * scale);
        }

        private bool GetSolidSweepClosed()
        {
            int last = m_Stations.Count - 1;
            bool endpointsMeet = last >= 3 && (m_Stations[0].point.m_Pos - m_Stations[last].point.m_Pos)
                .sqrMagnitude < kMinPositionDelta * kMinPositionDelta;
            if (!endpointsMeet)
                return false;
            if (AuthoredSweepClosed is bool authoredClosed)
                return authoredClosed;
            return (!TracksController && (m_StrokeData == null || !HasHeadingOffsets(m_StrokeData))) ||
                Quaternion.Angle(m_Stations[0].point.m_Orient, m_Stations[last].point.m_Orient) < 0.1f;
        }

        private void BuildSolidTransition(float floorHeight, SurfaceOptions options, float radius)
        {
            bool pool = options.Coping == CopingKind.Pool;
            bool steel = options.Coping == CopingKind.Steel;
            int points = BodyPointCount(pool);
            int rearStart = points - kBackPoints;
            int rearEnd = points - 2;
            int tubeStart = 2 * points;
            int stride = tubeStart + (steel ? TubePoints : 0);
            int capCapacity = 2 * (points + (steel ? TubePoints : 0));
            CollectStations(stride, capCapacity, allowVertical: AuthoredSweepClosed == true);
            int count = m_Stations.Count;
            if (count < 2)
                return;

            m_ClosedSweep = GetSolidSweepClosed();
            int turns = AutoOrient ? QuarterTurns & 2 : QuarterTurns;
            bool tracksController = !AutoOrient && TracksController;
            bool hasOffsets = !AutoOrient && m_StrokeData != null && HasHeadingOffsets(m_StrokeData);
            Vector3? fixedHeading = !AutoOrient && StrokeOrientation is Quaternion orientation
                ? HorizontalHeading(orientation) : null;
            StrokeFlags flags = m_StrokeData?.m_Flags ?? StrokeFlags.None;
            float defaultDepth = NewStrokeDeckDepthInches ?? ((flags & StrokeFlags.TransitionSurfaceOverride) != 0
                ? DefaultDeckDepthInches : LegacyDeckDepth(m_Style));
            bool variableDeck = options.Deck &&
                (PreviewStartDeckPressure.HasValue || (m_StrokeData != null && HasVariableDeck(m_StrokeData)));
            float tubeRadius = steel ? (m_StrokeData == null ? NewStrokeSteelDiameterHalfInches * 0.5f :
                SteelDiameterInchesFor(m_StrokeData)) * 0.5f * UnitsPerInch * POINTER_TO_LOCAL : 0f;
            if (m_BodySamples.Length < count * points)
            {
                Array.Resize(ref m_BodySamples, count * points);
                Array.Resize(ref m_BodyDistances, count * points);
            }
            if (m_SolidStations.Length < count)
                Array.Resize(ref m_SolidStations, count);

            float distance = 0f;
            bool hasDeck = false;
            for (int station = 0; station < count; station++)
            {
                Vector3 position = m_Stations[station].point.m_Pos;
                if (station > 0)
                    distance += Vector3.Distance(position, m_SolidStations[station - 1].Position);

                // Reuse the first station at the UV seam, including its brace and deck shape.
                if (m_ClosedSweep && station == count - 1)
                {
                    m_SolidStations[station] = m_SolidStations[0];
                    m_SolidStations[station].Distance = distance;
                    Array.Copy(m_BodySamples, 0, m_BodySamples, station * points, points);
                    Array.Copy(m_BodyDistances, 0, m_BodyDistances, station * points, points);
                    continue;
                }

                float deckDepth = defaultDepth;
                if (variableDeck)
                {
                    float pressure = station == 0
                        ? PreviewStartDeckPressure ?? m_StrokeData.m_ControlPoints[0].m_Pressure
                        : m_Stations[station].point.m_Pressure;
                    deckDepth = DecodeDeckDepth(pressure);
                }
                var frame = new SolidStation
                {
                    Position = position,
                    Across = Across(GetStationHeading(station, tracksController, hasOffsets, fixedHeading), turns),
                    Floor = Mathf.Min(position.y, floorHeight),
                    Distance = distance
                };
                if (steel)
                    MakeTubeFrame(StationTangent(station, m_ClosedSweep, horizontal: false),
                        out frame.TubeRight, out frame.TubeUp);
                m_SolidStations[station] = frame;
                var profile = MakeBodyProfile(options, position.y - frame.Floor, radius,
                    deckDepth * UnitsPerInch * POINTER_TO_LOCAL, tubeRadius, POINTER_TO_LOCAL);
                hasDeck |= profile.DeckDepth > 0f;
                int sample = station * points;
                float rearDistance = 0f;
                for (int row = 0; row < points; row++)
                {
                    int index = sample + row;
                    m_BodySamples[index] = profile.Sample(row, out m_BodyDistances[index]);
                    if (row > rearStart && row <= rearEnd)
                    {
                        rearDistance += Vector2.Distance(m_BodySamples[index - 1], m_BodySamples[index]);
                        m_BodyDistances[index] = rearDistance;
                    }
                }
            }

            float repeatsPerUnit = m_ClosedSweep
                ? 2f * Mathf.Max(1, Mathf.RoundToInt(distance / (2f * kConcreteRepeat))) / distance : 1f / kConcreteRepeat;
            bool reverse = SolidWinding(count);
            m_geometry.NumVerts = count * stride + (m_ClosedSweep ? 0 : capCapacity);
            int capVertex = count * stride;
            for (int station = 0; station < count; station++)
            {
                int first = station * stride;
                int previous = station > 0 ? station - 1 : m_ClosedSweep ? count - 2 : 0;
                int next = station < count - 1 ? station + 1 : m_ClosedSweep ? 1 : station;
                float u = m_SolidStations[station].Distance * repeatsPerUnit;
                Color32 color = m_Stations[station].color;
                for (int edge = 0; edge < points; edge++)
                {
                    int b = (edge + 1) % points;
                    int sample = station * points;
                    int vertex = first + edge * 2;
                    Vector2 cross = m_BodySamples[sample + b] - m_BodySamples[sample + edge];
                    bool riding = edge >= kFrontStart && edge < rearStart - 1;
                    bool rear = edge >= rearStart && edge < rearEnd;
                    bool deck = edge == 0 && hasDeck;
                    bool tile = riding && options.Tile;
                    Vector3 region = deck ? Vector3.right : tile ? Vector3.zero : Vector3.up;
                    for (int side = 0; side < 2; side++)
                    {
                        int row = side == 0 ? edge : b;
                        Vector3 along = BodyPosition(next, row, points) - BodyPosition(previous, row, points);
                        Vector2 direction = riding
                            ? SmoothProfileDirection(sample, row, kFrontStart, rearStart - 1, cross) : cross;
                        Vector3 profileDirection = m_SolidStations[station].Across * direction.x + Vector3.up * direction.y;
                        float v = riding || deck || rear ? m_BodyDistances[sample + row] : side == 0 ? 0f : cross.magnitude;
                        SetSolidVertex(vertex + side, BodyPosition(station, row, points), along, profileDirection,
                            color, new Vector2(u, v / kConcreteRepeat), region, reverse);
                    }
                    if (station > 0)
                        AddSolidQuad(vertex - stride, vertex, reverse);
                }

                if (steel)
                {
                    for (int row = 0; row < TubePoints; row++)
                    {
                        Vector3 current = TubePosition(station, row, tubeRadius);
                        Vector3 along = TubePosition(next, row, tubeRadius) - TubePosition(previous, row, tubeRadius);
                        int prevRow = (row + TubePoints - 2) % (TubePoints - 1);
                        Vector3 tangent = TubePosition(station, row + 1, tubeRadius) - TubePosition(station, prevRow, tubeRadius);
                        SetSolidVertex(first + tubeStart + row, current, along, tangent, color,
                            new Vector2(u, row * (2f * Mathf.PI / kTubeSides) * tubeRadius / kConcreteRepeat),
                            Vector3.forward, reverse: false);
                        if (station > 0 && row > 0)
                        {
                            int edge = first + tubeStart + row - 1;
                            AddSolidQuad(edge - stride, edge, reverse: false);
                        }
                    }
                }
            }

            if (!m_ClosedSweep)
            {
                AddBodyCap(0, points, reverse, ref capVertex);
                AddBodyCap(count - 1, points, !reverse, ref capVertex);
                if (steel)
                {
                    AddTubeCap(0, tubeRadius, reverse: false, ref capVertex);
                    AddTubeCap(count - 1, tubeRadius, reverse: true, ref capVertex);
                }
            }

            m_geometry.NumVerts = m_geometry.m_Tris.Count > 0 ? capVertex : 0;
        }

        private bool SolidWinding(int count)
        {
            for (int i = 0; i < count - 1; i++)
            {
                float alignment = Vector3.Dot(Vector3.Cross(m_SolidStations[i].Across, Vector3.up),
                    m_SolidStations[i + 1].Position - m_SolidStations[i].Position);
                if (Mathf.Abs(alignment) > 1e-5f)
                    return alignment < 0f;
            }
            return false;
        }

        private Vector2 SmoothProfileDirection(int sample, int row, int first, int last, Vector2 fallback)
        {
            Vector2 p = m_BodySamples[sample + row];
            Vector2 a = Vector2.zero;
            Vector2 b = Vector2.zero;
            for (int i = row - 1; i >= first && a.sqrMagnitude < 1e-12f; i--)
                a = p - m_BodySamples[sample + i];
            for (int i = row + 1; i <= last && b.sqrMagnitude < 1e-12f; i++)
                b = m_BodySamples[sample + i] - p;
            Vector2 direction = a.normalized + b.normalized;
            return direction.sqrMagnitude > 1e-12f ? direction : fallback;
        }

        private Vector3 BodyPosition(int station, int row, int points)
        {
            var frame = m_SolidStations[station];
            Vector2 point = m_BodySamples[station * points + row];
            return new Vector3(frame.Position.x, frame.Floor + point.y, frame.Position.z) + frame.Across * point.x;
        }

        private Vector3 TubePosition(int station, int row, float radius)
        {
            var frame = m_SolidStations[station];
            Vector2 point = SteelProfilePoint(row, radius);
            return frame.Position + frame.TubeRight * point.x + frame.TubeUp * (point.y - radius);
        }

        private void SetSolidVertex(int index, Vector3 position, Vector3 along, Vector3 across, Color32 color,
            Vector2 uv, Vector3 material, bool reverse)
        {
            Vector3 normal = Vector3.Cross(along, across).normalized;
            if (reverse)
                normal = -normal;
            if (normal == Vector3.zero)
                normal = Vector3.up;
            Vector3 tangent = Vector3.ProjectOnPlane(along, normal).normalized;
            if (tangent == Vector3.zero)
                tangent = Vector3.Cross(normal, Mathf.Abs(normal.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
            float sign = Vector3.Dot(Vector3.Cross(normal, tangent), across) < 0f ? -1f : 1f;
            SetVert(0, index, position, normal, color, 1f);
            SetUv0(0, index, uv);
            SetUv1(0, index, material);
            SetTangent(0, index, tangent, sign);
        }

        private void AddSolidQuad(int a, int b, bool reverse)
        {
            AddTriangle(a, b, a + 1, reverse);
            AddTriangle(a + 1, b, b + 1, reverse);
        }

        private void AddBodyCap(int station, int points, bool reverse, ref int vertex)
        {
            int sample = station * points;
            m_CapOutline.Clear();
            for (int row = 0; row < points; row++)
            {
                if (m_CapOutline.Count == 0 ||
                    (m_BodySamples[sample + row] - m_BodySamples[sample + m_CapOutline[^1]]).sqrMagnitude > 1e-12f)
                    m_CapOutline.Add(row);
            }
            if (m_CapOutline.Count > 1 &&
                (m_BodySamples[sample + m_CapOutline[0]] - m_BodySamples[sample + m_CapOutline[^1]]).sqrMagnitude <= 1e-12f)
                m_CapOutline.RemoveAt(m_CapOutline.Count - 1);
            if (m_CapOutline.Count < 3)
                return;

            Vector3 across = m_SolidStations[station].Across;
            int capStart = vertex;
            for (int row = 0; row < points; row++)
            {
                Vector2 uv = m_BodySamples[sample + row];
                uv.x -= m_BodySamples[sample].x;
                SetSolidVertex(vertex++, BodyPosition(station, row, points), across, Vector3.up, m_Stations[station].color,
                    uv / kConcreteRepeat, Vector3.up, !reverse);
            }

            // Ear-clipping retains the concave ledge under the overhanging deck.
            while (m_CapOutline.Count > 2)
            {
                int count = m_CapOutline.Count;
                int ear = -1;
                for (int i = 0; i < count; i++)
                {
                    int a = m_CapOutline[(i + count - 1) % count];
                    int b = m_CapOutline[i];
                    int c = m_CapOutline[(i + 1) % count];
                    if (!IsCapEar(sample, a, b, c))
                        continue;
                    AddTriangle(capStart + a, capStart + b, capStart + c, reverse);
                    ear = i;
                    break;
                }

                if (ear < 0)
                    break;
                m_CapOutline.RemoveAt(ear);
            }
        }

        private bool IsCapEar(int sample, int a, int b, int c)
        {
            Vector2 p = m_BodySamples[sample + a];
            Vector2 q = m_BodySamples[sample + b];
            Vector2 r = m_BodySamples[sample + c];
            if (Cross2D(q - p, r - q) >= -1e-8f)
                return false;
            foreach (int index in m_CapOutline)
            {
                if (index == a || index == b || index == c)
                    continue;
                Vector2 point = m_BodySamples[sample + index];
                if (Cross2D(q - p, point - p) <= 1e-8f && Cross2D(r - q, point - q) <= 1e-8f &&
                    Cross2D(p - r, point - r) <= 1e-8f)
                    return false;
            }
            return true;
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private void AddTubeCap(int station, float radius, bool reverse, ref int vertex)
        {
            var frame = m_SolidStations[station];
            var color = m_Stations[station].color;
            Vector3 center = frame.Position - frame.TubeUp * radius;
            int first = vertex;
            SetSolidVertex(vertex++, center, frame.TubeRight, frame.TubeUp, color,
                Vector2.zero, Vector3.forward, !reverse);
            for (int i = 0; i < kTubeSides; i++)
                SetSolidVertex(vertex++, TubePosition(station, i, radius), frame.TubeRight, frame.TubeUp,
                    color, SteelProfilePoint(i, radius) / kConcreteRepeat, Vector3.forward, !reverse);
            for (int i = 0; i < kTubeSides; i++)
                AddTriangle(first, first + 1 + i, first + 1 + (i + 1) % kTubeSides, reverse);
        }

        private static void MakeTubeFrame(Vector3 path, out Vector3 right, out Vector3 up)
        {
            up = Vector3.ProjectOnPlane(Vector3.up, path);
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.forward, path);
            up.Normalize();
            right = Vector3.Cross(up, path).normalized;
        }
    }
}
