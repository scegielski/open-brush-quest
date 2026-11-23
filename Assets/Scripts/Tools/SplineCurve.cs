// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    internal static class SplineCurve
    {
        // This stride is part of the saved spline representation.
        internal const int SamplesPerSegment = 32;
        internal const int MaxKnots = 128;

        internal static bool IsEditable(StrokeData stroke)
        {
            int count = stroke.m_SplineKnotCount;
            return count >= 2 && count <= MaxKnots &&
                (stroke.m_ControlPoints?.Length == GetSampleCount(count, closed: false) || IsClosed(stroke));
        }

        // Closed splines bake one extra segment ending at the first knot.
        internal static bool IsClosed(StrokeData stroke) => stroke.m_SplineKnotCount >= 3 &&
            stroke.m_SplineKnotCount <= MaxKnots &&
            stroke.m_ControlPoints?.Length == GetSampleCount(stroke.m_SplineKnotCount, closed: true);

        internal static int GetSampleCount(int knotCount, bool closed) =>
            1 + (closed ? knotCount : knotCount - 1) * SamplesPerSegment;

        // Map an edited sample back to the old stroke for pressure, timestamps and point colors.
        internal static int GetSourceSampleIndex(int index, int oldCount, int newCount, int changed,
            float insertT, bool oldClosed = false)
        {
            if (changed < 0 || oldCount == newCount)
                return Mathf.Min(index, GetSampleCount(oldCount, oldClosed) - 1);

            int stride = SamplesPerSegment;
            int start = (changed - 1) * stride;
            if (newCount > oldCount)
            {
                if (changed == 0)
                    return Mathf.Max(0, index - stride);
                if (changed == oldCount && !oldClosed)
                    return Mathf.Min(index, (oldCount - 1) * stride);
                if (index <= start)
                    return index;
                int relative = index - start;
                if (relative >= 2 * stride)
                    return index - stride;
                float oldMiddle = start + insertT * stride;
                return Mathf.RoundToInt(relative <= stride
                    ? Mathf.Lerp(start, oldMiddle, (float)relative / stride)
                    : Mathf.Lerp(oldMiddle, start + stride, (float)(relative - stride) / stride));
            }

            if (changed == 0)
                return index + stride;
            if ((!oldClosed && changed == oldCount - 1) || index <= start)
                return index;
            return index < start + stride ? start + 2 * (index - start) : index + stride;
        }

        // Centripetal Catmull–Rom passes through each placed knot and handles uneven spacing.
        internal static void Sample(IReadOnlyList<TrTransform> knots, List<TrTransform> samples, bool closed = false)
        {
            samples.Clear();
            if (knots.Count == 0)
                return;

            samples.Add(knots[0]);
            int knotCount = knots.Count;
            int segmentCount = closed ? knotCount : knotCount - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                var start = knots[i];
                var end = knots[(i + 1) % knotCount];
                Vector3 chord = end.translation - start.translation;
                float length = chord.magnitude;
                if (length < 0.000001f)
                    continue;

                Vector3 before = i > 0 || closed
                    ? knots[(i + knotCount - 1) % knotCount].translation : start.translation - chord;
                Vector3 after = i + 2 < knotCount || closed
                    ? knots[(i + 2) % knotCount].translation : end.translation + chord;
                float span = Mathf.Sqrt(length);
                Vector3 startTangent = ChordTangent(start.translation - before, chord, span);
                Vector3 endTangent = ChordTangent(after - end.translation, chord, span);
                for (int step = 1; step < SamplesPerSegment; step++)
                {
                    float t = (float)step / SamplesPerSegment;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    Vector3 position = start.translation + (3 * t2 - 2 * t3) * chord +
                        (t3 - 2 * t2 + t) * startTangent + (t3 - t2) * endTangent;
                    samples.Add(TrTransform.TR(position, Quaternion.Slerp(start.rotation, end.rotation, t)));
                }
                samples.Add(end);
            }
        }

        private static Vector3 ChordTangent(Vector3 neighbor, Vector3 chord, float span)
        {
            float neighborSpan = Mathf.Sqrt(neighbor.magnitude);
            if (neighborSpan < 0.001f)
                return chord;

            return (neighbor * (span / neighborSpan) + chord * (neighborSpan / span)) *
                (span / (neighborSpan + span));
        }
    }
}
