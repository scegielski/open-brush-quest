// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.XR.CoreUtils;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        private Vector3[] m_TransitionPreviewPoints = new Vector3[TransitionBrush.ProfilePoints];
        private LineRenderer m_TransitionPreviewLine;
        private Material m_TransitionPreviewMaterial;
        private TextMeshPro m_TransitionHeightLabel;
        private Material m_TransitionHeightLabelMaterial;
        private int m_PreviewHeightHalfInches = -1;
        private CanvasScript m_PreviewCanvas;
        private CanvasScript m_TransitionSampleCanvas;
        private int m_TransitionSampleFrame = -1;
        private float m_TransitionSampleYaw;
        private Quaternion m_DisplayedTransitionOrientation;
        private bool m_TransitionHeadingLocked;
        private bool m_TransitionHeadingTracked;
        private string m_TransitionPreviewStatus;

        internal Quaternion CaptureTransitionOrientation(CanvasScript canvas, Quaternion fallback)
        {
            if (m_PreviewCanvas == canvas && m_TransitionPreviewLine != null &&
                m_TransitionPreviewLine.gameObject.activeInHierarchy)
                return m_DisplayedTransitionOrientation;
            return SnapTransitionAngle(TransitionBrush.HorizontalOrientation(fallback), canvas);
        }

        private Quaternion SnapTransitionAngle(Quaternion heading, CanvasScript canvas)
        {
            if (SelectionManager.m_Instance == null || SelectionManager.m_Instance.CurrentSnapAngleIndex == 0)
                return heading;
            int turns = UsesSplinePreview && m_SplineCanvas == canvas
                ? (SplineEditData != null
                    ? TransitionBrush.QuarterTurnsFromFlags(SplineEditData.m_Flags) : DraftSplineQuarterTurns)
                : (PointerManager.m_Instance.MainPointer.CurrentBrushScript as TransitionBrush)?.QuarterTurns
                    ?? TransitionBrush.PreferredQuarterTurns;
            // Snap the actual direction of the sweep, including the thumbstick orientation.
            Quaternion across = Quaternion.AngleAxis(90f * (turns + 1), Vector3.up);
            Quaternion snapped = SnapBrushAngle(heading * across, canvas.Pose.rotation);
            return TransitionBrush.HorizontalOrientation(snapped * Quaternion.Inverse(across));
        }

        internal Quaternion SampleTransitionOrientation(CanvasScript canvas, Quaternion fallback, bool snap = true)
        {
            bool sameCanvas = m_TransitionSampleCanvas == canvas;
            int frame = Time.frameCount;
            if (sameCanvas && m_TransitionSampleFrame == frame)
            {
                Quaternion cached = Quaternion.Euler(0f, m_TransitionSampleYaw, 0f);
                return snap ? SnapTransitionAngle(cached, canvas) : cached;
            }

            Transform attachment = InputManager.m_Instance?.GetBrushControllerAttachPoint();
            Quaternion raw = attachment != null
                ? Quaternion.Inverse(canvas.Pose.rotation) * attachment.rotation * sm_OrientationAdjust
                : fallback;
            Vector3 heading = raw * Vector3.forward;
            heading.y = 0f;
            bool consecutive = sameCanvas && m_TransitionSampleFrame == frame - 1;
            if (heading.sqrMagnitude < 0.01f && !consecutive)
                heading = TransitionBrush.HorizontalOrientation(raw) * Vector3.forward;

            if (heading.sqrMagnitude >= 0.01f)
            {
                float yaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
                float responsiveness = 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.055f);
                m_TransitionSampleYaw = consecutive
                    ? Mathf.LerpAngle(m_TransitionSampleYaw, yaw, responsiveness) : yaw;
            }
            m_TransitionSampleCanvas = canvas;
            m_TransitionSampleFrame = frame;
            Quaternion smoothed = Quaternion.Euler(0f, m_TransitionSampleYaw, 0f);
            return snap ? SnapTransitionAngle(smoothed, canvas) : smoothed;
        }

        internal void UpdateTransitionPreview(PointerScript pointer, Transform parent)
        {
            bool painting = App.Instance != null && App.Instance.IsInStateThatAllowsPainting() &&
                SketchControlsScript.m_Instance?.GridCalibrationActive != true;
            Camera camera = painting && App.VrSdk != null ? App.VrSdk.GetVrCamera() : null;
            var canvas = painting ? App.ActiveCanvas : null;
            bool spline = canvas != null && UsesSplinePreview && m_SplineCanvas == canvas;
            if (spline && SketchSurfacePanel.m_Instance.ActiveTool == this)
                UpdateDraftTransitionDimensions();
            var editedTransition = spline ? SplineEditData : null;
            var descriptor = spline ? SplineBrush : pointer?.CurrentBrush;
            bool transition = TransitionBrush.IsTransition(descriptor);
            if (!transition || canvas == null || camera == null)
            {
                if (m_TransitionPreviewLine != null)
                    m_TransitionPreviewLine.gameObject.SetActive(false);
                m_PreviewCanvas = null;
                m_TransitionPreviewStatus = !transition ? "Transition brush is inactive" :
                    $"Waiting for the sketch or view (painting={painting}, canvas={canvas != null}, view={camera != null})";
                return;
            }

            TrTransform roomFromCanvas = canvas.Pose;
            var canvasFromRoom = roomFromCanvas.inverse;
            // The controller pose exists independently of drawing input. During startup or panel hover,
            // the sketch pointer can still be at its previous position even though the controller has moved.
            Transform attachment = InputManager.m_Instance?.GetBrushControllerAttachPoint();
            var controllerPose = attachment != null
                ? TrTransform.TR(attachment.position, attachment.rotation * sm_OrientationAdjust)
                : Coords.AsRoom[pointer.transform];
            var controller = canvasFromRoom * controllerPose;
            var brush = pointer.CurrentBrushScript as TransitionBrush;
            Vector3 coping = brush != null ? canvasFromRoom * pointer.transform.position : controller.translation;
            Quaternion? lockedOrientation = brush?.StrokeOrientation ?? TransitionBrush.GetSavedOrientation(editedTransition);
            Quaternion? knotOrientation = null;
            int knotIndex = m_DraggedSplineKnot >= 0 ? m_DraggedSplineKnot :
                (m_SplineAction is SplineAction.MoveAndRotate or SplineAction.DeckDepth ? m_HoveredSplineKnot : -1);
            var options = spline ? SplineSurfaceOptions : TransitionBrush.OptionsFor(descriptor);
            float deckDepth = editedTransition != null
                ? SelectedSplineDeckDepthInches
                : spline ? DraftSplineDeckDepth : brush?.NewStrokeDeckDepthInches ?? TransitionBrush.DeckDepthInches;
            if (spline && knotIndex >= 0 && knotIndex < m_SplineKnots.Count)
            {
                TrTransform knot = m_SplineKnots[knotIndex];
                coping = knot.translation;
                deckDepth = KnotDeckDepthInches(knotIndex);
                if (SplineTracksTransitionHeading)
                    knotOrientation = knot.rotation;
                else if (SplineUsesHeadingOffsets && m_SavedSplinePreviewBrush is TransitionBrush savedPreview)
                {
                    Vector3 surfaceHeading = savedPreview.GetHeadingNear(coping);
                    if (surfaceHeading.sqrMagnitude > 0.001f)
                        knotOrientation = Quaternion.LookRotation(surfaceHeading, Vector3.up);
                }
            }
            m_TransitionHeadingTracked = brush != null && brush.TrackControllerForNewStroke;
            m_DisplayedTransitionOrientation = m_TransitionHeadingTracked ? brush.CurrentControllerOrientation :
                knotOrientation ?? lockedOrientation ?? SampleTransitionOrientation(canvas, controller.rotation);
            m_TransitionHeadingTracked |= knotOrientation.HasValue && m_DraggedSplineKnot >= 0;
            m_PreviewCanvas = canvas;
            m_TransitionHeadingLocked = lockedOrientation.HasValue;

            // Keep the draft and controller heading up to date while the panel has the laser.
            if (SketchControlsScript.m_Instance?.IsBrushPointingAtPanel() == true)
            {
                HideTransitionPreviewRenderers();
                m_TransitionPreviewStatus = "Hidden while pointing at a panel";
                return;
            }

            Vector3 heading = m_DisplayedTransitionOrientation * Vector3.forward;
            float inches = editedTransition == null ?
                (spline ? DraftSplineRadiusInches : TransitionBrush.RadiusInches) :
                Mathf.Max(TransitionBrush.MinRadiusInches,
                    editedTransition.m_BrushSize * editedTransition.m_BrushScale / TransitionBrush.UnitsPerInch);
            int turns = editedTransition != null ? TransitionBrush.QuarterTurnsFromFlags(editedTransition.m_Flags) :
                spline ? DraftSplineQuarterTurns : brush?.QuarterTurns ?? TransitionBrush.PreferredQuarterTurns;
            if (spline && SplineAutoOrientEnabled)
            {
                turns &= 2;
                if (m_SplineSamples.Count > 1)
                {
                    int sample = knotIndex >= 0
                        ? Mathf.Min(knotIndex * SplineCurve.SamplesPerSegment, m_SplineSamples.Count - 1)
                        : editedTransition != null ? NearestTransitionPreviewSample(coping) : m_SplineSamples.Count - 1;
                    Vector3 tangent = SplineCurve.GetAutoOrientation(m_SplineSamples, sample, m_SplineClosed) *
                        Vector3.forward;
                    tangent.y = 0f;
                    if (tangent.sqrMagnitude > 1e-6f)
                        heading = tangent.normalized;
                }
            }
            float deckScale = editedTransition?.m_BrushScale ??
                (spline ? 1f : brush?.POINTER_TO_LOCAL ?? canvasFromRoom.scale);
            DrawTransitionPreview(roomFromCanvas, coping, heading, inches, turns, options, parent, deckDepth,
                deckScale, camera.transform, App.Scene.FloorHeightCanvasUnits);
        }

        private int NearestTransitionPreviewSample(Vector3 position)
        {
            float closestDistance = float.PositiveInfinity;
            int closest = 0;
            for (int i = 0; i < m_SplineSamples.Count; i++)
            {
                float distance = (position - m_SplineSamples[i].translation).sqrMagnitude;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = i;
                }
            }
            return closest;
        }

        internal void DrawTransitionPreview(TrTransform roomFromCanvas, Vector3 coping, Vector3 heading,
            float inches, int quarterTurns, Transform parent = null,
            float deckDepthInches = TransitionBrush.DefaultDeckDepthInches, float deckScale = 1f,
            TransitionBrush.SweepStyle style = TransitionBrush.SweepStyle.PoolTransition, Transform viewer = null,
            float floorHeight = 0f) =>
            DrawTransitionPreview(roomFromCanvas, coping, heading, inches, quarterTurns,
                TransitionBrush.OptionsFor(style), parent, deckDepthInches, deckScale, viewer, floorHeight);

        internal void DrawTransitionPreview(TrTransform roomFromCanvas, Vector3 coping, Vector3 heading,
            float inches, int quarterTurns, TransitionBrush.SurfaceOptions options, Transform parent = null,
            float deckDepthInches = TransitionBrush.DefaultDeckDepthInches, float deckScale = 1f, Transform viewer = null,
            float floorHeight = 0f)
        {
            float height = coping.y - floorHeight;
            if (height <= 0f)
            {
                HideTransitionPreviewRenderers();
                m_TransitionPreviewStatus = "Tip at or below the sketch floor";
                return;
            }

            if (m_TransitionPreviewLine == null)
                CreateTransitionPreview(parent != null ? parent : transform);
            m_TransitionPreviewLine.gameObject.SetActive(true);
            float radius = inches * TransitionBrush.UnitsPerInch;
            float deckDepth = options.Deck ? deckDepthInches * TransitionBrush.UnitsPerInch * deckScale : 0f;
            bool poolCoping = options.Coping == TransitionBrush.CopingKind.Pool;
            Array.Resize(ref m_TransitionPreviewPoints, TransitionBrush.PreviewPointCount(options, deckDepth));
            Vector3 across = TransitionBrush.Across(heading, quarterTurns);
            var profile = new TransitionBrush.SurfaceProfile(height, radius, deckDepth, poolCoping);
            for (int row = 0; row < m_TransitionPreviewPoints.Length; row++)
            {
                Vector2 sample = TransitionBrush.PreviewPoint(options, profile, row, height, deckScale, deckDepth);
                m_TransitionPreviewPoints[row] = roomFromCanvas *
                    (new Vector3(coping.x, sample.y + floorHeight, coping.z) + across * sample.x);
            }
            m_TransitionPreviewLine.positionCount = m_TransitionPreviewPoints.Length;
            m_TransitionPreviewLine.SetPositions(m_TransitionPreviewPoints);
            m_TransitionPreviewLine.enabled = true;
            UpdateTransitionHeightLabel(roomFromCanvas, coping, height, viewer);

            m_TransitionPreviewStatus = "Full-size floor profile";
        }

        private void HideTransitionPreviewRenderers()
        {
            if (m_TransitionPreviewLine != null)
                m_TransitionPreviewLine.enabled = false;
            if (m_TransitionHeightLabel != null)
                m_TransitionHeightLabel.renderer.enabled = false;
        }

        private void UpdateTransitionHeightLabel(TrTransform roomFromCanvas, Vector3 coping, float height, Transform viewer)
        {
            if (viewer == null)
            {
                if (m_TransitionHeightLabel != null)
                    m_TransitionHeightLabel.renderer.enabled = false;
                return;
            }

            if (m_TransitionHeightLabel == null)
                CreateTransitionHeightLabel();
            int heightHalfInches = Mathf.RoundToInt(2f * height / TransitionBrush.UnitsPerInch);
            if (heightHalfInches != m_PreviewHeightHalfInches)
            {
                m_PreviewHeightHalfInches = heightHalfInches;
                m_TransitionHeightLabel.text = $"Height {TransitionBrush.FormatFeetAndInches(heightHalfInches * 0.5f)}";
            }

            const float offsetMeters = 0.08f;
            Vector3 position = roomFromCanvas * coping + Vector3.up * (offsetMeters * App.METERS_TO_UNITS);
            Vector3 awayFromViewer = position - viewer.position;
            Quaternion rotation = awayFromViewer.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(awayFromViewer, viewer.up) : viewer.rotation;
            Transform label = m_TransitionHeightLabel.transform;
            label.SetPositionAndRotation(position, rotation);
            label.localScale = Vector3.one / label.parent.lossyScale.x;
            m_TransitionHeightLabel.renderer.enabled = true;
        }

        internal string DescribeTransitionPreview()
        {
            Camera camera = App.VrSdk.GetVrCamera();
            var origin = camera.GetComponentInParent<XROrigin>();
            var canvas = App.ActiveCanvas;
            float floorY = canvas != null
                ? (canvas.Pose * (Vector3.up * App.Scene.FloorHeightCanvasUnits)).y : 0f;
            var snap = SelectionManager.m_Instance;
            var controls = SketchControlsScript.m_Instance;
            string floor = (origin != null
                ? $", tracking origin={origin.CurrentTrackingOriginMode}, " +
                    $"camera offset={origin.CameraFloorOffsetObject.transform.localPosition.y:F2}m"
                : "") + (canvas != null
                ? $", sketch floor={floorY * App.UNITS_TO_METERS:F2}m, sketch scale={canvas.Pose.scale:F2}"
                : "") + (snap != null
                ? $", grid snap={snap.SnappingGridSize / TransitionBrush.UnitsPerInch:F1}in, angle snap={snap.SnappingAngle:F0}°"
                : "") + (controls != null
                ? $", world lock={controls.IsWorldCalibrationLocked}, calibration={controls.GridCalibrationActive}"
                : "");
            if (m_TransitionPreviewLine == null || !m_TransitionPreviewLine.gameObject.activeInHierarchy)
                return m_TransitionPreviewStatus + floor;

            var line = m_TransitionPreviewLine;
            return $"{m_TransitionPreviewStatus}, camera layer={(camera.cullingMask & (1 << line.gameObject.layer)) != 0}, " +
                $"line={line.enabled}, " +
                $"heading={m_DisplayedTransitionOrientation.eulerAngles.y:F1}°, tracking={m_TransitionHeadingTracked}, " +
                $"locked={m_TransitionHeadingLocked}" + floor;
        }

        private void CreateTransitionPreview(Transform parent)
        {
            var root = new GameObject("Transition profile preview");
            root.layer = gameObject.layer;
            root.transform.SetParent(parent, false);
            var shader = Resources.Load<Shader>("Brushes/Basic/Transition/TransitionPreviewLine");
            m_TransitionPreviewMaterial = new Material(shader);
            m_TransitionPreviewLine = root.AddComponent<LineRenderer>();
            m_TransitionPreviewLine.useWorldSpace = true;
            m_TransitionPreviewLine.positionCount = m_TransitionPreviewPoints.Length;
            m_TransitionPreviewLine.startWidth = 1f;
            m_TransitionPreviewLine.endWidth = 1f;
            m_TransitionPreviewLine.widthMultiplier = 0.055f;
            // Tiled UVs are in world units. Repeat the dash pattern every 8 cm.
            const float dashSpacingMeters = 0.08f;
            m_TransitionPreviewLine.textureMode = LineTextureMode.Tile;
            m_TransitionPreviewLine.textureScale = new Vector2(1f / (dashSpacingMeters * App.METERS_TO_UNITS), 1f);
            m_TransitionPreviewLine.numCapVertices = 3;
            m_TransitionPreviewLine.sharedMaterial = m_TransitionPreviewMaterial;
            m_TransitionPreviewLine.shadowCastingMode = ShadowCastingMode.Off;
            m_TransitionPreviewLine.receiveShadows = false;
            m_TransitionPreviewLine.allowOcclusionWhenDynamic = false;
        }

        private void CreateTransitionHeightLabel()
        {
            var labelObject = new GameObject("Transition height");
            labelObject.layer = m_TransitionPreviewLine.gameObject.layer;
            labelObject.transform.SetParent(m_TransitionPreviewLine.transform, false);
            m_TransitionHeightLabel = labelObject.AddComponent<TextMeshPro>();
            m_TransitionHeightLabel.alignment = TextAlignmentOptions.Center;
            m_TransitionHeightLabel.color = Color.white;
            m_TransitionHeightLabel.fontSize = 4.2f;
            m_TransitionHeightLabel.textWrappingMode = TextWrappingModes.NoWrap;

            var overlay = Resources.Load<Material>("Brushes/Basic/Transition/TransitionOverlayText");
            // Keep the font's SDF atlas and metrics with an always-on-top shader.
            m_TransitionHeightLabelMaterial = new Material(m_TransitionHeightLabel.fontSharedMaterial)
                { shader = overlay.shader };
            m_TransitionHeightLabelMaterial.renderQueue = (int)RenderQueue.Overlay + 1;
            m_TransitionHeightLabelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
            m_TransitionHeightLabelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.12f);
            m_TransitionHeightLabel.fontSharedMaterial = m_TransitionHeightLabelMaterial;
            m_TransitionHeightLabel.renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_TransitionHeightLabel.renderer.receiveShadows = false;
            m_TransitionHeightLabel.renderer.allowOcclusionWhenDynamic = false;
        }
    }
}
