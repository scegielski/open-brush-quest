// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    internal static class SplineCurve
    {
        // This stride is part of the saved spline representation.
        internal const int SamplesPerSegment = 32;
        internal const int MaxKnots = 128;

        internal static Quaternion RotateGrabbedKnot(Quaternion knotAtGrab, Quaternion controllerAtGrab,
            Quaternion currentController) => currentController * Quaternion.Inverse(controllerAtGrab) * knotAtGrab;

        internal static bool IsEditable(StrokeData stroke)
        {
            int count = stroke.m_SplineKnotCount;
            return count >= 2 && count <= MaxKnots &&
                (stroke.m_SplineControlPoints == null || stroke.m_SplineControlPoints.Length == count) &&
                (stroke.m_ControlPoints?.Length == GetSampleCount(count, closed: false) || IsClosed(stroke));
        }

        internal static TrTransform GetAuthoredPoint(StrokeData stroke, int index)
        {
            if (stroke.m_SplineControlPoints != null)
                return stroke.m_SplineControlPoints[index];

            // Legacy Catmull-Rom splines passed through every authored point.
            var sample = stroke.m_ControlPoints[index * SamplesPerSegment];
            return TrTransform.TR(sample.m_Pos, sample.m_Orient);
        }

        // Closed splines bake one extra segment ending at the first knot.
        internal static bool IsClosed(StrokeData stroke) => stroke.m_SplineKnotCount >= 3 &&
            stroke.m_SplineKnotCount <= MaxKnots &&
            stroke.m_ControlPoints?.Length == GetSampleCount(stroke.m_SplineKnotCount, closed: true);

        internal static bool AutoOrients(StrokeData stroke) =>
            stroke != null && (stroke.m_Flags & SketchMemoryScript.StrokeFlags.SplineAutoOrient) != 0;

        internal static Quaternion GetAutoOrientation(IReadOnlyList<TrTransform> samples, int index, bool closed)
        {
            int last = samples.Count - 1;
            if (last < 1)
                return samples[index].rotation;
            int previous = index == 0 && closed ? last - 1 : Mathf.Max(0, index - 1);
            int next = index == last && closed ? 1 : Mathf.Min(last, index + 1);
            Vector3 position = samples[index].translation;
            Vector3 incoming = (position - samples[previous].translation).normalized;
            Vector3 outgoing = (samples[next].translation - position).normalized;
            Vector3 tangent = (incoming + outgoing).normalized;
            if (tangent == Vector3.zero)
                tangent = outgoing == Vector3.zero ? incoming : outgoing;
            if (tangent == Vector3.zero)
                return samples[index].rotation;

            Vector3 up = Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) > 0.999f
                ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(tangent, up);
        }

        internal static int GetSampleCount(int knotCount, bool closed) =>
            1 + (closed ? knotCount : knotCount - 1) * SamplesPerSegment;

        // The deck profile passes through every depth setting at a fixed sample index, so the
        // settings can be recovered from the baked stroke even though its B-spline misses the hull.
        internal static float SampleDeckScale(IReadOnlyList<TrTransform> knots, int sample, bool closed)
        {
            int index = sample / SamplesPerSegment;
            if (!closed && index >= knots.Count - 1)
                return knots[knots.Count - 1].scale;
            index %= knots.Count;
            float t = (sample % SamplesPerSegment) / (float)SamplesPerSegment;
            return Mathf.SmoothStep(knots[index].scale, knots[(index + 1) % knots.Count].scale, t);
        }

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

        // Open curves use a uniform clamped knot vector. Loops use a periodic cubic
        // knot vector with control points wrapped across the seam.
        internal static void Sample(IReadOnlyList<TrTransform> controls, List<TrTransform> samples, bool closed = false)
        {
            samples.Clear();
            int count = controls.Count;
            if (count == 0)
                return;

            int distinct = 1;
            while (distinct < count &&
                (controls[distinct].translation - controls[0].translation).sqrMagnitude < 1e-12f)
                distinct++;
            if (distinct == count)
            {
                samples.Add(controls[0]);
                return;
            }

            closed &= count >= 3;
            int degree = closed ? 3 : Mathf.Min(3, count - 1);
            int spans = closed ? count : count - degree;
            int steps = GetSampleCount(count, closed) - 1;
            Span<TrTransform> work = stackalloc TrTransform[4];

            // De Boor evaluation retains the old baked sample count for stroke attributes.
            for (int sample = 0; sample < steps; sample++)
            {
                float parameter = (float)sample * spans / steps;
                int firstControl = Mathf.Min((int)parameter, spans - 1);
                for (int j = 0; j <= degree; j++)
                {
                    int index = firstControl + j;
                    work[j] = controls[closed ? (index + count - 1) % count : index];
                }

                for (int level = 1; level <= degree; level++)
                {
                    for (int j = degree; j >= level; j--)
                    {
                        int start = firstControl + j - degree;
                        int end = start + degree - level + 1;
                        if (!closed)
                        {
                            start = Mathf.Clamp(start, 0, spans);
                            end = Mathf.Clamp(end, 0, spans);
                        }
                        float t = (parameter - start) / (end - start);
                        work[j] = TrTransform.TR(
                            Vector3.LerpUnclamped(work[j - 1].translation, work[j].translation, t),
                            Quaternion.Lerp(work[j - 1].rotation, work[j].rotation, t));
                    }
                }
                samples.Add(work[degree]);
            }
            samples.Add(closed ? samples[0] : controls[count - 1]);
        }
    }
}
