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
        private readonly Vector3[] m_SteelPreviewPoints = new Vector3[TransitionBrush.TubePoints];
        private LineRenderer m_TransitionPreviewLine;
        private LineRenderer m_SteelPreviewLine;
        private Material m_TransitionPreviewMaterial;
        private TextMeshPro m_BrushHeightLabel;
        private Material m_BrushHeightLabelMaterial;
        private int? m_PreviewHeightHalfInches;
        private Quaternion m_DisplayedTransitionOrientation;
        private bool m_TransitionHeadingLocked;
        private bool m_TransitionHeadingTracked;
        private string m_TransitionPreviewStatus;

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
            // The rendered preview can be a previous tracking frame or an existing knot.
            // Use the current controller pose for both the preview and new control points.
            Transform attachment = InputManager.m_Instance?.GetBrushControllerAttachPoint();
            Quaternion raw = attachment != null
                ? Quaternion.Inverse(canvas.Pose.rotation) * attachment.rotation * sm_OrientationAdjust
                : fallback;
            Quaternion heading = TransitionBrush.HorizontalOrientation(raw);
            return snap ? SnapTransitionAngle(heading, canvas) : heading;
        }

        internal void UpdateTransitionPreview(PointerScript pointer, Transform parent)
        {
            var controls = SketchControlsScript.m_Instance;
            bool painting = App.Instance != null && App.Instance.IsInStateThatAllowsPainting() &&
                SketchSurfacePanel.m_Instance?.ActiveTool == this && controls?.GridCalibrationActive != true;
            Camera camera = painting && App.VrSdk != null ? App.VrSdk.GetVrCamera() : null;
            var canvas = painting ? App.ActiveCanvas : null;
            if (canvas == null || camera == null || pointer?.CurrentBrush == null)
            {
                HideTransitionPreviewRenderers();
                m_TransitionPreviewStatus = "Waiting for a drawing tool, sketch, or view";
                return;
            }

            bool spline = UsesSplinePreview && m_SplineCanvas == canvas;
            if (spline)
                UpdateDraftTransitionDimensions();

            TrTransform roomFromCanvas = canvas.Pose;
            var canvasFromRoom = roomFromCanvas.inverse;
            float floorHeight = App.Scene.FloorHeightCanvasUnits;
            int knotIndex = m_DraggedSplineKnot >= 0 ? m_DraggedSplineKnot :
                (m_SplineAction is SplineAction.MoveAndRotate or SplineAction.DeckDepth or SplineAction.Append
                    or SplineAction.Delete ? m_HoveredSplineKnot : -1);
            bool useKnot = spline && knotIndex >= 0 && knotIndex < m_SplineKnots.Count;
            Vector3 drawingPoint = useKnot ? m_SplineKnots[knotIndex].translation :
                canvasFromRoom * pointer.transform.position;
            bool hidePreview = m_ToolHidden || controls?.IsBrushPointingAtPanel() == true ||
                (spline && m_SplineAction == SplineAction.SelectCurve);
            Transform heightViewer = SnapSettingsPanel.ShowHeight && !hidePreview ? camera.transform : null;
            var descriptor = spline ? SplineBrush : pointer.CurrentBrush;
            if (!TransitionBrush.IsTransition(descriptor))
            {
                if (m_TransitionPreviewLine != null)
                    m_TransitionPreviewLine.gameObject.SetActive(false);
                DrawBrushHeightLabel(roomFromCanvas, drawingPoint, floorHeight, heightViewer, parent);
                m_TransitionPreviewStatus = "Transition brush is inactive";
                return;
            }

            var editedTransition = spline ? SplineEditData : null;
            // The controller pose exists independently of drawing input. During startup or panel hover,
            // the sketch pointer can still be at its previous position even though the controller has moved.
            Transform attachment = InputManager.m_Instance?.GetBrushControllerAttachPoint();
            var controllerPose = attachment != null
                ? TrTransform.TR(attachment.position, attachment.rotation * sm_OrientationAdjust)
                : Coords.AsRoom[pointer.transform];
            var controller = canvasFromRoom * controllerPose;
            var brush = pointer.CurrentBrushScript as TransitionBrush;
            Vector3 coping = spline || brush != null ? drawingPoint : controller.translation;
            Quaternion? lockedOrientation = spline
                ? TransitionBrush.GetSavedOrientation(editedTransition) : brush?.StrokeOrientation;
            Quaternion? knotOrientation = null;
            var options = spline ? SplineSurfaceOptions : TransitionBrush.OptionsFor(descriptor);
            float deckDepth = editedTransition != null
                ? SelectedSplineDeckDepthInches
                : spline ? DraftSplineDeckDepth : brush?.NewStrokeDeckDepthInches ?? TransitionBrush.DeckDepthInches;
            if (useKnot)
            {
                TrTransform knot = m_SplineKnots[knotIndex];
                deckDepth = KnotDeckDepthInches(knotIndex);
                if (SplineTracksTransitionHeading)
                    knotOrientation = knot.rotation;
                else if (SplineUsesHeadingOffsets)
                    knotOrientation = knot.rotation *
                        (GetSplineTransitionTangent(knotIndex * SplineCurve.SamplesPerSegment) ?? Quaternion.identity);
            }
            m_TransitionHeadingTracked = !spline && brush != null && brush.TrackControllerForNewStroke;
            m_DisplayedTransitionOrientation = m_TransitionHeadingTracked ? brush.CurrentControllerOrientation :
                knotOrientation ?? lockedOrientation ??
                SnapTransitionAngle(TransitionBrush.HorizontalOrientation(controller.rotation), canvas);
            m_TransitionHeadingTracked |= knotOrientation.HasValue && m_DraggedSplineKnot >= 0;
            m_TransitionHeadingLocked = lockedOrientation.HasValue;

            // Keep the draft and controller heading up to date while the panel has the laser.
            if (hidePreview)
            {
                HideTransitionPreviewRenderers();
                m_TransitionPreviewStatus = "Drawing preview hidden";
                return;
            }

            float inches = editedTransition == null ?
                (spline ? DraftSplineRadiusInches : TransitionBrush.RadiusInches) :
                Mathf.Max(TransitionBrush.MinRadiusInches,
                    editedTransition.m_BrushSize * editedTransition.m_BrushScale / TransitionBrush.UnitsPerInch);
            int turns = editedTransition != null ? TransitionBrush.QuarterTurnsFromFlags(editedTransition.m_Flags) :
                spline ? DraftSplineQuarterTurns : brush?.QuarterTurns ?? TransitionBrush.PreferredQuarterTurns;
            if (spline && SplineAutoOrientEnabled)
            {
                turns &= 2;
                int sample = useKnot ? knotIndex * SplineCurve.SamplesPerSegment :
                    editedTransition != null ? NearestTransitionPreviewSample(coping) : m_SplineSamples.Count - 1;
                m_DisplayedTransitionOrientation = GetSplineTransitionTangent(sample) ?? m_DisplayedTransitionOrientation;
            }
            float deckScale = editedTransition?.m_BrushScale ??
                (spline ? 1f : brush?.POINTER_TO_LOCAL ?? canvasFromRoom.scale);
            float steelDiameterInches = editedTransition != null
                ? TransitionBrush.SteelDiameterInchesFor(editedTransition)
                : spline || brush == null ? TransitionBrush.SteelDiameterInches : brush.NewStrokeSteelDiameterHalfInches * 0.5f;
            DrawTransitionPreview(roomFromCanvas, coping, m_DisplayedTransitionOrientation * Vector3.forward, inches,
                turns, options, parent, deckDepth, deckScale, heightViewer, floorHeight, steelDiameterInches);
        }

        private Quaternion? GetSplineTransitionTangent(int sample)
        {
            if (m_SplineSamples.Count < 2)
                return null;
            int index = Mathf.Clamp(sample, 0, m_SplineSamples.Count - 1);
            Vector3 heading = SplineCurve.GetAutoOrientation(m_SplineSamples, index, m_SplineClosed) * Vector3.forward;
            heading.y = 0f;
            return heading.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(heading, Vector3.up) : null;
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
            float floorHeight = 0f, float steelDiameterInches = TransitionBrush.DefaultSteelDiameterInches) =>
            DrawTransitionPreview(roomFromCanvas, coping, heading, inches, quarterTurns,
                TransitionBrush.OptionsFor(style), parent, deckDepthInches, deckScale, viewer, floorHeight,
                steelDiameterInches);

        internal void DrawTransitionPreview(TrTransform roomFromCanvas, Vector3 coping, Vector3 heading,
            float inches, int quarterTurns, TransitionBrush.SurfaceOptions options, Transform parent = null,
            float deckDepthInches = TransitionBrush.DefaultDeckDepthInches, float deckScale = 1f, Transform viewer = null,
            float floorHeight = 0f, float steelDiameterInches = TransitionBrush.DefaultSteelDiameterInches)
        {
            float height = coping.y - floorHeight;
            if (height <= 0f)
            {
                HideTransitionPreviewRenderers();
                m_TransitionPreviewStatus = "Tip at or below the sketch floor";
                return;
            }

            if (m_TransitionPreviewLine == null)
                m_TransitionPreviewLine = CreateTransitionPreviewLine("Transition profile preview",
                    parent != null ? parent : transform);
            m_TransitionPreviewLine.gameObject.SetActive(true);
            float radius = inches * TransitionBrush.UnitsPerInch;
            float deckDepth = options.Deck ? deckDepthInches * TransitionBrush.UnitsPerInch * deckScale : 0f;
            bool poolCoping = options.Coping == TransitionBrush.CopingKind.Pool;
            Array.Resize(ref m_TransitionPreviewPoints, TransitionBrush.PreviewPointCount(options, deckDepth));
            Vector3 across = TransitionBrush.Across(heading, quarterTurns);
            var profile = new TransitionBrush.SurfaceProfile(height, radius, deckDepth, poolCoping);
            Vector3 ToRoom(Vector2 sample) => roomFromCanvas *
                (new Vector3(coping.x, sample.y + floorHeight, coping.z) + across * sample.x);
            for (int row = 0; row < m_TransitionPreviewPoints.Length; row++)
            {
                Vector2 sample = TransitionBrush.PreviewPoint(options, profile, row, height, deckScale, deckDepth,
                    steelDiameterInches);
                m_TransitionPreviewPoints[row] = ToRoom(sample);
            }
            m_TransitionPreviewLine.positionCount = m_TransitionPreviewPoints.Length;
            m_TransitionPreviewLine.SetPositions(m_TransitionPreviewPoints);
            m_TransitionPreviewLine.enabled = true;

            if (options.Coping == TransitionBrush.CopingKind.Steel)
            {
                if (m_SteelPreviewLine == null)
                    m_SteelPreviewLine = CreateTransitionPreviewLine("Steel coping profile", m_TransitionPreviewLine.transform);
                float tubeRadius = steelDiameterInches * 0.5f * TransitionBrush.UnitsPerInch * deckScale;
                Vector2 tubeCenter = Vector2.up * (height - tubeRadius);
                for (int row = 0; row < m_SteelPreviewPoints.Length; row++)
                    m_SteelPreviewPoints[row] = ToRoom(tubeCenter + TransitionBrush.SteelProfilePoint(row, tubeRadius));
                m_SteelPreviewLine.positionCount = m_SteelPreviewPoints.Length;
                m_SteelPreviewLine.SetPositions(m_SteelPreviewPoints);
                m_SteelPreviewLine.enabled = true;
            }
            else if (m_SteelPreviewLine != null)
            {
                m_SteelPreviewLine.enabled = false;
            }
            DrawBrushHeightLabel(roomFromCanvas, coping, floorHeight, viewer, parent);

            m_TransitionPreviewStatus = "Full-size solid cross-section";
        }

        private void HideTransitionPreviewRenderers()
        {
            if (m_TransitionPreviewLine != null)
                m_TransitionPreviewLine.enabled = false;
            if (m_SteelPreviewLine != null)
                m_SteelPreviewLine.enabled = false;
            if (m_BrushHeightLabel != null)
                m_BrushHeightLabel.renderer.enabled = false;
        }

        internal void DrawBrushHeightLabel(TrTransform roomFromCanvas, Vector3 drawingPoint, float floorHeight,
            Transform viewer, Transform parent = null)
        {
            if (viewer == null)
            {
                if (m_BrushHeightLabel != null)
                    m_BrushHeightLabel.renderer.enabled = false;
                return;
            }

            if (m_BrushHeightLabel == null)
                CreateBrushHeightLabel(parent != null ? parent : transform);
            float height = drawingPoint.y - floorHeight;
            int heightHalfInches = Mathf.RoundToInt(2f * height / TransitionBrush.UnitsPerInch);
            if (heightHalfInches != m_PreviewHeightHalfInches)
            {
                m_PreviewHeightHalfInches = heightHalfInches;
                m_BrushHeightLabel.text = $"Height {TransitionBrush.FormatFeetAndInches(heightHalfInches * 0.5f)}";
            }

            const float offsetMeters = 0.08f;
            Vector3 position = roomFromCanvas * drawingPoint + Vector3.up * (offsetMeters * App.METERS_TO_UNITS);
            Vector3 awayFromViewer = position - viewer.position;
            Quaternion rotation = awayFromViewer.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(awayFromViewer, viewer.up) : viewer.rotation;
            Transform label = m_BrushHeightLabel.transform;
            label.SetPositionAndRotation(position, rotation);
            label.localScale = Vector3.one / label.parent.lossyScale.x;
            m_BrushHeightLabel.renderer.enabled = true;
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

        private LineRenderer CreateTransitionPreviewLine(string name, Transform parent)
        {
            var root = new GameObject(name) { layer = gameObject.layer };
            root.transform.SetParent(parent, false);
            if (m_TransitionPreviewMaterial == null)
                m_TransitionPreviewMaterial = new Material(
                    Resources.Load<Shader>("Brushes/Basic/Transition/TransitionPreviewLine"));
            var line = root.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.startWidth = 1f;
            line.endWidth = 1f;
            line.widthMultiplier = 0.055f;
            // Tiled UVs are in world units. Repeat the dash pattern every 8 cm.
            const float dashSpacingMeters = 0.08f;
            line.textureMode = LineTextureMode.Tile;
            line.textureScale = new Vector2(1f / (dashSpacingMeters * App.METERS_TO_UNITS), 1f);
            line.numCapVertices = 3;
            line.sharedMaterial = m_TransitionPreviewMaterial;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.allowOcclusionWhenDynamic = false;
            return line;
        }

        private void CreateBrushHeightLabel(Transform parent)
        {
            var labelObject = new GameObject("Brush height") { layer = gameObject.layer };
            labelObject.transform.SetParent(parent, false);
            m_BrushHeightLabel = labelObject.AddComponent<TextMeshPro>();
            m_BrushHeightLabel.alignment = TextAlignmentOptions.Center;
            m_BrushHeightLabel.color = Color.white;
            m_BrushHeightLabel.fontSize = 4.2f;
            m_BrushHeightLabel.textWrappingMode = TextWrappingModes.NoWrap;

            var overlay = Resources.Load<Material>("Brushes/Basic/Transition/TransitionOverlayText");
            // Keep the font's SDF atlas and metrics with an always-on-top shader.
            m_BrushHeightLabelMaterial = new Material(m_BrushHeightLabel.fontSharedMaterial)
                { shader = overlay.shader };
            m_BrushHeightLabelMaterial.renderQueue = (int)RenderQueue.Overlay + 1;
            m_BrushHeightLabelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
            m_BrushHeightLabelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.12f);
            m_BrushHeightLabel.fontSharedMaterial = m_BrushHeightLabelMaterial;
            m_BrushHeightLabel.renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_BrushHeightLabel.renderer.receiveShadows = false;
            m_BrushHeightLabel.renderer.allowOcclusionWhenDynamic = false;
        }
    }
}
