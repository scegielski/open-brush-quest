// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    public sealed partial class TransitionBrush : GeometryBrush
    {
        internal enum SweepStyle
        {
            None, PoolTransition, UntiledPoolTransition, ConcreteTransition, SteelTransition,
            PoolCoping, PoolTile, SteelCoping
        }

        internal enum CopingKind { None, Pool, Steel }

        internal readonly struct SurfaceOptions
        {
            internal readonly CopingKind Coping;
            internal readonly bool Tile;
            internal readonly bool Deck;

            internal SurfaceOptions(CopingKind coping, bool tile, bool deck)
            {
                Coping = coping;
                Tile = tile;
                Deck = deck;
            }
        }

        internal const StrokeFlags SurfaceOptionFlags = StrokeFlags.TransitionSurfaceOverride |
            StrokeFlags.TransitionPoolCoping | StrokeFlags.TransitionSteelCoping |
            StrokeFlags.TransitionTile | StrokeFlags.TransitionDeck;

        internal const string BrushName = "Transition";
        internal const string UntiledPoolBrushName = "Transition with Pool Coping";
        internal const int MinRadiusInches = 1;
        internal const int MaxRadiusInches = 600;
        private const int kDefaultRadiusInches = 54;
        private const string kRadiusPreference = "TransitionRadiusInches";
        private const string kDeckDepthPreference = "TransitionDeckDepthInches";
        private const string kRadiusHalfInchesPreference = "TransitionRadiusHalfInches";
        private const string kDeckDepthHalfInchesPreference = "TransitionDeckDepthHalfInches";
        private const string kSteelDiameterHalfInchesPreference = "SteelCopingDiameterHalfInches";
        private const string kSweepLeftPreference = "TransitionSweepLeft";
        private const string kOrientationPreference = "TransitionQuarterTurns";
        internal const float UnitsPerInch = 0.0254f * App.METERS_TO_UNITS;
        private const float kConcreteRepeat = 3f * App.METERS_TO_UNITS;
        private const int kCopingRampSegments = 4;
        private const int kNoseSegments = 8;
        private const int kArcSegments = 24;
        private const float kCopingWidth = 10f * UnitsPerInch;
        private const float kNoseProjection = 1.5f * UnitsPerInch;
        private const float kNoseDrop = 2.25f * UnitsPerInch;
        private const float kCopingRise = 0.6f * UnitsPerInch;
        internal const int DefaultDeckDepthInches = 12;
        internal const int LegacyPoolDeckDepthInches = 36;
        internal const int MaxDeckDepthInches = 240;
        internal const int DeckPoints = 2;
        private const int kCopingStart = DeckPoints;
        internal const int TopOfCoping = kCopingStart + kCopingRampSegments;
        internal const int WallStart = TopOfCoping + kNoseSegments;
        internal const int WallPoints = kArcSegments + 2;
        internal const int ProfilePoints = WallStart + WallPoints;
        private const int kTubeSides = 16;
        internal const int TubePoints = kTubeSides + 1;
        private const int kCopingFaceEnd = kCopingRampSegments + kNoseSegments;
        private const int kPoolCopingPoints = kCopingFaceEnd + 3;
        internal const float DefaultSteelDiameterInches = 2f;
        internal const int MinSteelDiameterHalfInches = 1;
        internal const int MaxSteelDiameterHalfInches = 127;
        private const int kDefaultSteelDiameterHalfInches = (int)(DefaultSteelDiameterInches * 2f);
        private const int kSteelDiameterShift = 15;
        internal const float SteelRadius = DefaultSteelDiameterInches * 0.5f * UnitsPerInch;
        internal const float TileBandWidth = 0.15f * App.METERS_TO_UNITS;
        private const float kMinPositionDelta = 0.0001f;

        private readonly List<Knot> m_Stations = new();
        private bool m_ClosedSweep;
        private SweepStyle m_Style;
        internal int NewStrokeQuarterTurns { get; set; }
        internal bool TrackControllerForNewStroke { get; set; }
        internal bool NewStrokeAutoOrient { get; set; }
        internal SurfaceOptions? NewStrokeSurfaceOptions { get; set; }
        internal float? NewStrokeDeckDepthInches { get; set; }
        internal int NewStrokeSteelDiameterHalfInches { get; set; } = kDefaultSteelDiameterHalfInches;
        internal float? PreviewStartDeckPressure { get; set; }
        internal Quaternion CurrentControllerOrientation { get; set; }
        internal int QuarterTurns => m_StrokeData == null ? NewStrokeQuarterTurns :
            QuarterTurnsFromFlags(m_StrokeData.m_Flags);
        internal Quaternion? StrokeOrientation => GetSavedOrientation(m_StrokeData);
        internal bool TracksController => m_StrokeData == null ? TrackControllerForNewStroke :
            HasControllerHeading(m_StrokeData);
        private bool AutoOrient => m_StrokeData == null ? NewStrokeAutoOrient : SplineCurve.AutoOrients(m_StrokeData);

        internal static bool HasControllerHeading(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionControllerHeading) != 0;
        internal static bool HasHeadingOffsets(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionHeadingOffsets) != 0;
        internal static bool HasVariableDeck(StrokeData stroke) =>
            (stroke.m_Flags & StrokeFlags.TransitionVariableDeck) != 0;

        internal static float DecodeDeckDepth(float pressure) =>
            float.IsFinite(pressure) ? Mathf.Clamp01(pressure) * MaxDeckDepthInches : DefaultDeckDepthInches;

        internal static float EncodeDeckDepth(float inches) => Mathf.Clamp01(inches / MaxDeckDepthInches);

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

        public TransitionBrush() : base(bCanBatch: true, upperBoundVertsPerKnot: kSolidMaxRows,
            bDoubleSided: false, bSmoothPositions: false) { }

        internal static SweepStyle StyleFor(BrushDescriptor brush) => brush?.m_DurableName switch
        {
            BrushName => SweepStyle.PoolTransition,
            UntiledPoolBrushName => SweepStyle.UntiledPoolTransition,
            "Concrete Transition" => SweepStyle.ConcreteTransition,
            "Steel Transition" => SweepStyle.SteelTransition,
            "Pool Coping" => SweepStyle.PoolCoping,
            "Pool Tile" => SweepStyle.PoolTile,
            "Steel Coping" => SweepStyle.SteelCoping,
            _ => SweepStyle.None
        };

        internal static bool IsTransition(BrushDescriptor brush) => IsTransition(StyleFor(brush));

        internal static bool IsStandaloneSteelCoping(BrushDescriptor brush) => StyleFor(brush) == SweepStyle.SteelCoping;

        internal static bool HasSteelCoping(BrushDescriptor brush, StrokeData stroke = null) =>
            IsStandaloneSteelCoping(brush) || OptionsFor(brush, stroke).Coping == CopingKind.Steel;

        internal static int SteelDiameterHalfInches
        {
            get => ClampSteelDiameter(PlayerPrefs.GetInt(kSteelDiameterHalfInchesPreference, kDefaultSteelDiameterHalfInches));
            set => PlayerPrefs.SetInt(kSteelDiameterHalfInchesPreference, ClampSteelDiameter(value));
        }

        internal static float SteelDiameterInches => SteelDiameterHalfInches * 0.5f;

        internal static int ClampSteelDiameter(int halfInches) =>
            Mathf.Clamp(halfInches, MinSteelDiameterHalfInches, MaxSteelDiameterHalfInches);

        internal static StrokeFlags FlagsWithSteelDiameter(StrokeFlags flags, int halfInches)
        {
            halfInches = ClampSteelDiameter(halfInches);
            var encoded = halfInches == kDefaultSteelDiameterHalfInches
                ? StrokeFlags.None : (StrokeFlags)(halfInches << kSteelDiameterShift);
            return (flags & ~StrokeFlags.SteelCopingDiameterMask) | encoded;
        }

        internal static int SteelDiameterHalfInchesFor(StrokeData stroke)
        {
            var flags = stroke?.m_Flags ?? StrokeFlags.None;
            int encoded = (int)(flags & StrokeFlags.SteelCopingDiameterMask) >> kSteelDiameterShift;
            return encoded == 0 ? kDefaultSteelDiameterHalfInches : encoded;
        }

        internal static float SteelDiameterInchesFor(StrokeData stroke) => SteelDiameterHalfInchesFor(stroke) * 0.5f;

        private static bool IsTransition(SweepStyle style) =>
            style is SweepStyle.PoolTransition or SweepStyle.UntiledPoolTransition
                or SweepStyle.ConcreteTransition or SweepStyle.SteelTransition;

        internal static bool HasDeck(BrushDescriptor brush) => HasDeck(StyleFor(brush));

        internal static bool HasDeck(SweepStyle style) => OptionsFor(style).Deck;

        internal static bool UsesPoolCoping(SweepStyle style) =>
            style is SweepStyle.PoolTransition or SweepStyle.UntiledPoolTransition;

        internal static SurfaceOptions OptionsFor(SweepStyle style) => style switch
        {
            SweepStyle.PoolTransition => new SurfaceOptions(CopingKind.Pool, tile: true, deck: true),
            SweepStyle.UntiledPoolTransition => new SurfaceOptions(CopingKind.Pool, tile: false, deck: true),
            SweepStyle.SteelTransition => new SurfaceOptions(CopingKind.Steel, tile: false, deck: true),
            _ => default
        };

        internal static SurfaceOptions OptionsFor(BrushDescriptor brush, StrokeData stroke = null)
        {
            SweepStyle style = StyleFor(brush);
            StrokeFlags flags = stroke?.m_Flags ?? StrokeFlags.None;
            return IsTransition(style) ? OptionsFor(style, flags) : default;
        }

        private static SurfaceOptions OptionsFor(SweepStyle style, StrokeFlags flags)
        {
            if ((flags & StrokeFlags.TransitionSurfaceOverride) == 0)
                return OptionsFor(style);
            CopingKind coping = (flags & StrokeFlags.TransitionSteelCoping) != 0 ? CopingKind.Steel :
                (flags & StrokeFlags.TransitionPoolCoping) != 0 ? CopingKind.Pool : CopingKind.None;
            return new SurfaceOptions(coping, (flags & StrokeFlags.TransitionTile) != 0,
                (flags & StrokeFlags.TransitionDeck) != 0);
        }

        internal static StrokeFlags FlagsWithSurfaceOptions(StrokeFlags flags, SurfaceOptions options)
        {
            flags = (flags & ~SurfaceOptionFlags) | StrokeFlags.TransitionSurfaceOverride;
            flags |= options.Coping switch
            {
                CopingKind.Pool => StrokeFlags.TransitionPoolCoping,
                CopingKind.Steel => StrokeFlags.TransitionSteelCoping,
                _ => StrokeFlags.None
            };
            SketchMemoryScript.SetFlag(ref flags, StrokeFlags.TransitionTile, options.Tile);
            SketchMemoryScript.SetFlag(ref flags, StrokeFlags.TransitionDeck, options.Deck);
            return flags;
        }

        internal static int LegacyDeckDepth(SweepStyle style) => UsesPoolCoping(style) ? LegacyPoolDeckDepthInches : 0;

        internal static float DeckDepthInches => DeckDepthHalfInches * 0.5f;

        internal static int DeckDepthHalfInches
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(kDeckDepthHalfInchesPreference,
                Mathf.Clamp(PlayerPrefs.GetInt(kDeckDepthPreference, DefaultDeckDepthInches), 0, MaxDeckDepthInches) * 2),
                0, MaxDeckDepthInches * 2);
            set => PlayerPrefs.SetInt(kDeckDepthHalfInchesPreference, Mathf.Clamp(value, 0, MaxDeckDepthInches * 2));
        }

        internal static float RadiusInches => RadiusHalfInches * 0.5f;

        internal static int RadiusHalfInches
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(kRadiusHalfInchesPreference,
                Mathf.Clamp(PlayerPrefs.GetInt(kRadiusPreference, kDefaultRadiusInches), MinRadiusInches, MaxRadiusInches) * 2),
                MinRadiusInches * 2, MaxRadiusInches * 2);
            set => PlayerPrefs.SetInt(kRadiusHalfInchesPreference,
                Mathf.Clamp(value, MinRadiusInches * 2, MaxRadiusInches * 2));
        }

        internal static string FormatFeetAndInches(float inches)
        {
            int halfInches = Mathf.RoundToInt(Mathf.Abs(inches) * 2f);
            string fraction = halfInches % 2 != 0 ? ".5" : "";
            return $"{(inches < 0f && halfInches > 0 ? "-" : "")}{halfInches / 24} ft {halfInches % 24 / 2}{fraction} in";
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
            m_Style = StyleFor(desc);
            m_geometry.Layout = GetVertexLayout(desc);
        }

        public override float GetSpawnInterval(float pressure01) => 0.02f * App.METERS_TO_UNITS * POINTER_TO_LOCAL;
        public override bool IsOutOfVerts() => false; // Station sampling caps the mesh before allocation.
        public override bool ShouldCurrentLineEnd() => false;

        // Sample the coping from the deck side to the bowl, then follow the original transition to Y=0.
        // The rounded lip covers the upper part of the wall without changing the radius or floor run.
        internal readonly struct SurfaceProfile
        {
            private readonly float m_Radius, m_JoinHeight, m_JoinRun;
            private readonly float m_WallAngle, m_ArcRun, m_FloorRun, m_StraightLength;
            private readonly float m_DeckDepth;
            private readonly CopingProfile m_Coping;

            internal SurfaceProfile(float height, float radius, float deckDepth = DefaultDeckDepthInches * UnitsPerInch,
                bool poolCoping = true)
            {
                height = Mathf.Max(0f, height);
                m_Radius = radius;
                float drop = poolCoping ? Mathf.Min(kNoseDrop, height * 0.4f) : 0f;
                float width = poolCoping ? Mathf.Min(kCopingWidth, height * 4f) : 0f;
                m_DeckDepth = poolCoping && height > 0f ? deckDepth : 0f;
                m_JoinHeight = height - drop;
                m_FloorRun = radius * Mathf.Sin(ArcAngle(height, radius));
                m_WallAngle = ArcAngle(m_JoinHeight, radius);
                m_ArcRun = radius * Mathf.Sin(m_WallAngle);
                m_JoinRun = m_FloorRun - m_ArcRun;
                m_Coping = new CopingProfile(height, width, drop, m_JoinRun);
                m_StraightLength = Mathf.Max(0f, m_JoinHeight - radius);
            }

            internal float Radius => m_Radius;

            private static float ArcAngle(float height, float radius) =>
                Mathf.Acos(Mathf.Clamp01(1f - height / radius));

            internal Vector2 Sample(int row, out float distance)
            {
                if (row < kCopingStart)
                {
                    Vector2 edge = m_Coping.Sample(0, out distance);
                    float depth = row == 0 ? m_DeckDepth : 0f;
                    distance -= depth;
                    return edge - Vector2.right * depth;
                }
                if (row < WallStart)
                    return m_Coping.Sample(row - kCopingStart, out distance);
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

        // The same raised cap and bullnose sit above both pool transitions and the standalone block.
        private readonly struct CopingProfile
        {
            private readonly float m_Height, m_Width, m_Drop, m_JoinRun;

            internal CopingProfile(float height, float width, float drop, float joinRun)
            {
                m_Height = height;
                m_Width = width;
                m_Drop = drop;
                m_JoinRun = joinRun;
            }

            internal Vector2 Sample(int row, out float distance)
            {
                float projection = m_Drop * (kNoseProjection / kNoseDrop);
                float lipLength = Mathf.PI * (projection + 0.5f * m_Drop) * 0.5f + m_JoinRun;
                if (row < kCopingRampSegments)
                {
                    float fraction = row / (float)kCopingRampSegments;
                    float rise = m_Drop * (kCopingRise / kNoseDrop);
                    distance = -lipLength - m_Width * (1f - fraction);
                    return new Vector2(-m_Width * (1f - fraction),
                        m_Height - Mathf.SmoothStep(rise, 0f, fraction));
                }
                if (row == kCopingFaceEnd)
                {
                    distance = 0f;
                    return new Vector2(m_JoinRun, m_Height - m_Drop);
                }
                float noseFraction = (row - kCopingRampSegments) / (float)kNoseSegments;
                float angle = noseFraction * Mathf.PI;
                float dropFraction = 0.5f * (1f - Mathf.Cos(angle));
                distance = -lipLength * (1f - noseFraction);
                return new Vector2(projection * Mathf.Sin(angle) + m_JoinRun * dropFraction,
                    m_Height - m_Drop * dropFraction);
            }
        }

        private static Vector2 StandaloneCopingPoint(int row, out float distance)
        {
            // Keep the entire standalone block in the shader's coping region.
            const float copingUvOffset = 0.001f;
            if (row == kPoolCopingPoints - 2)
            {
                distance = -copingUvOffset - kCopingWidth;
                return new Vector2(-kCopingWidth, -kNoseDrop);
            }
            var profile = new CopingProfile(0f, kCopingWidth, kNoseDrop, 0f);
            bool closing = row == kPoolCopingPoints - 1;
            Vector2 point = profile.Sample(closing ? 0 : row, out distance);
            distance = closing ? -copingUvOffset - kCopingWidth - (point.y + kNoseDrop)
                : distance - copingUvOffset;
            return point;
        }

        internal static Vector2 SteelProfilePoint(int row, float radius) =>
            new Vector2(Mathf.Sin((row % kTubeSides) * (2f * Mathf.PI / kTubeSides)) * radius,
                Mathf.Cos((row % kTubeSides) * (2f * Mathf.PI / kTubeSides)) * radius);

        internal static int PreviewPointCount(SurfaceOptions options, float deckDepth) =>
            BodyPointCount(options.Coping == CopingKind.Pool) + 1;

        internal static Vector2 PreviewPoint(SurfaceOptions options, SurfaceProfile profile, int row, float height,
            float scale, float deckDepth, float steelDiameterInches = DefaultSteelDiameterInches)
        {
            float tubeRadius = options.Coping == CopingKind.Steel ? steelDiameterInches * 0.5f * UnitsPerInch * scale : 0f;
            return MakeBodyProfile(options, height, profile.Radius, deckDepth, tubeRadius, scale).Sample(row, out _);
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
            m_geometry.m_Tris.SetCount(0);
            m_geometry.NumVerts = 0;
            float radius = BaseSize_LS;
            if (m_knots.Count < 2 || !float.IsFinite(radius) || radius <= 0f)
            {
                m_Stations.Clear();
                return;
            }

            if (IsTransition(m_Style))
            {
                float floor = App.Instance != null ? App.Scene.FloorHeightCanvasUnits : 0f;
                var options = m_StrokeData == null ? NewStrokeSurfaceOptions ?? OptionsFor(m_Style) :
                    OptionsFor(m_Style, m_StrokeData.m_Flags);
                BuildSolidTransition(floor, options, radius);
            }
            else
            {
                BuildStandaloneSweep();
            }
        }

        private void CollectStations(int rowCount, int reserve, bool allowVertical)
        {
            m_Stations.Clear();
            int maxStations = (65000 - reserve) / rowCount;
            int step = Mathf.Max(1, Mathf.CeilToInt((m_knots.Count - 1f) / (maxStations - 1)));
            for (int i = 0; i < m_knots.Count; i += step)
                AddStation(m_knots[i], allowVertical);
            if ((m_knots.Count - 1) % step != 0)
                AddStation(m_knots[m_knots.Count - 1], allowVertical);
        }

        private void BuildStandaloneSweep()
        {
            bool steel = m_Style == SweepStyle.SteelCoping;
            int rows = m_Style switch
            {
                SweepStyle.PoolCoping => kPoolCopingPoints,
                SweepStyle.SteelCoping => TubePoints,
                SweepStyle.PoolTile => 2,
                _ => 0
            };
            if (rows == 0)
                return;

            CollectStations(rows, reserve: 0, allowVertical: true);
            int count = m_Stations.Count;
            if (count < 2)
                return;

            m_geometry.NumVerts = count * rows;
            bool closed = GetSolidSweepClosed();
            m_ClosedSweep = closed;

            float totalDistance = 0f;
            for (int i = 1; i < count; i++)
                totalDistance += Vector3.Distance(m_Stations[i].point.m_Pos, m_Stations[i - 1].point.m_Pos);
            float repeatsPerUnit = closed
                ? 2f * Mathf.Max(1, Mathf.RoundToInt(totalDistance / (2f * kConcreteRepeat))) / totalDistance : 1f / kConcreteRepeat;
            float distance = 0f;
            float widthScale = POINTER_TO_LOCAL;
            float steelDiameter = m_StrokeData == null
                ? NewStrokeSteelDiameterHalfInches * 0.5f : SteelDiameterInchesFor(m_StrokeData);
            float tubeRadius = steelDiameter * 0.5f * UnitsPerInch * widthScale;
            var vertices = m_geometry.m_Vertices;
            for (int station = 0; station < count; station++)
            {
                Vector3 center = m_Stations[station].point.m_Pos;
                if (station > 0)
                    distance += Vector3.Distance(center, m_Stations[station - 1].point.m_Pos);
                MakeTubeFrame(StationTangent(station, closed, horizontal: false), out var right, out var up);
                int first = station * rows;
                for (int row = 0; row < rows; row++)
                {
                    float profileDistance;
                    Vector2 point;
                    if (steel)
                    {
                        point = SteelProfilePoint(row, tubeRadius);
                        profileDistance = row * (2f * Mathf.PI / kTubeSides) * tubeRadius;
                    }
                    else if (m_Style == SweepStyle.PoolCoping)
                    {
                        point = StandaloneCopingPoint(row, out profileDistance) * widthScale;
                        profileDistance *= widthScale;
                    }
                    else
                    {
                        profileDistance = row * TileBandWidth;
                        point = Vector2.down * (profileDistance * widthScale);
                    }
                    vertices[first + row] = center + right * point.x + up * point.y;
                    SetUv0(first, row, new Vector2(distance * repeatsPerUnit, profileDistance / kConcreteRepeat));
                    SetUv1(first, row, steel ? Vector3.forward : Vector3.zero);
                }
            }

            for (int station = 0; station < count; station++)
            {
                int first = station * rows;
                int previous = station > 0 ? station - 1 : (closed ? count - 2 : 0);
                int next = station < count - 1 ? station + 1 : (closed ? 1 : station);
                for (int row = 0; row < rows; row++)
                {
                    Vector3 along = vertices[next * rows + row] - vertices[previous * rows + row];
                    int upper = Mathf.Max(0, row - 1);
                    int lower = Mathf.Min(rows - 1, row + 1);
                    if (steel && (row == 0 || row == rows - 1))
                    {
                        upper = rows - 2;
                        lower = 1;
                    }
                    Vector3 down = vertices[first + lower] - vertices[first + upper];
                    Vector3 normal = Vector3.Cross(along, down).normalized;
                    if (normal == Vector3.zero)
                        normal = Vector3.up;
                    SetVert(first, row, vertices[first + row], normal, m_Stations[station].color, 1f);
                    Vector3 tangent = Vector3.ProjectOnPlane(along, normal);
                    if (tangent.sqrMagnitude < 1e-10f)
                        tangent = Vector3.Cross(normal, Mathf.Abs(normal.z) < 0.9f ? Vector3.forward : Vector3.right);
                    float sign = Vector3.Dot(Vector3.Cross(normal, tangent), down) < 0f ? -1f : 1f;
                    SetTangent(first, row, tangent, sign);
                    if (station < count - 1 && row < rows - 1)
                        AddSolidQuad(first + row, first + rows + row, reverse: false);
                }
            }
            if (m_geometry.m_Tris.Count == 0)
                m_geometry.NumVerts = 0;
        }

        private void AddStation(Knot knot, bool allowVertical)
        {
            Vector3 delta = m_Stations.Count == 0 ? Vector3.one :
                knot.point.m_Pos - m_Stations[m_Stations.Count - 1].point.m_Pos;
            if ((allowVertical ? delta : Horizontal(delta)).sqrMagnitude < kMinPositionDelta * kMinPositionDelta)
            {
                m_Stations[m_Stations.Count - 1] = knot;
                return;
            }
            m_Stations.Add(knot);
        }

        private Vector3 StationTangent(int station, bool closed, bool horizontal = true)
        {
            int last = m_Stations.Count - 1;
            // Average the heading over several kept points so tiny tip motions cannot spin a whole cross section.
            int span = Mathf.Min(3, Mathf.Max(1, (last - 1) / 2));
            int previous = closed ? (station - span + last) % last : Mathf.Max(0, station - span);
            int next = closed ? (station + span) % last : Mathf.Min(last, station + span);
            Vector3 position = m_Stations[station].point.m_Pos;
            Vector3 arriving = position - m_Stations[previous].point.m_Pos;
            Vector3 leaving = m_Stations[next].point.m_Pos - position;
            arriving = (horizontal ? Horizontal(arriving) : arriving).normalized;
            leaving = (horizontal ? Horizontal(leaving) : leaving).normalized;
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
