// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        private readonly Vector3[] m_TransitionPreviewPoints = new Vector3[TransitionBrush.ProfilePoints];
        private LineRenderer m_TransitionPreviewLine;
        private Material m_TransitionPreviewMaterial;
        private TextMeshPro m_TransitionReadout;
        private Material m_TransitionLabelMaterial;
        private int m_PreviewRadiusInches;
        private int m_PreviewHeightInches;
        private int m_PreviewQuarterTurns;
        private int m_PreviewDeckDepthInches;
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
            return TransitionBrush.HorizontalOrientation(fallback);
        }

        internal Quaternion SampleTransitionOrientation(CanvasScript canvas, Quaternion fallback)
        {
            bool sameCanvas = m_TransitionSampleCanvas == canvas;
            int frame = Time.frameCount;
            if (sameCanvas && m_TransitionSampleFrame == frame)
                return Quaternion.Euler(0f, m_TransitionSampleYaw, 0f);

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
            return Quaternion.Euler(0f, m_TransitionSampleYaw, 0f);
        }

        internal void UpdateTransitionPreview(PointerScript pointer, Transform parent)
        {
            bool painting = App.Instance != null && App.Instance.IsInStateThatAllowsPainting();
            Camera camera = painting && App.VrSdk != null ? App.VrSdk.GetVrCamera() : null;
            var canvas = painting ? App.ActiveCanvas : null;
            bool spline = canvas != null && UsesSplinePreview && m_SplineCanvas == canvas;
            var editedTransition = spline && m_EditingTransitionSpline ? SplineEditData : null;
            bool transition = editedTransition != null || TransitionBrush.IsTransition(pointer?.CurrentBrush);
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
            int deckDepth = TransitionBrush.DefaultDeckDepthInches;
            if (spline && knotIndex >= 0 && knotIndex < m_SplineKnots.Count)
            {
                TrTransform knot = m_SplineKnots[knotIndex];
                coping = knot.translation;
                deckDepth = Mathf.RoundToInt(KnotDeckDepthInches(knotIndex));
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
            Vector3 heading = m_DisplayedTransitionOrientation * Vector3.forward;
            int inches = editedTransition == null ? TransitionBrush.RadiusInches :
                Mathf.Max(1, Mathf.RoundToInt(
                    editedTransition.m_BrushSize * editedTransition.m_BrushScale / TransitionBrush.UnitsPerInch));
            int turns = editedTransition != null ? TransitionBrush.QuarterTurnsFromFlags(editedTransition.m_Flags) :
                brush != null ? brush.QuarterTurns : TransitionBrush.PreferredQuarterTurns;
            float deckScale = editedTransition?.m_BrushScale ??
                (spline ? 1f : brush?.POINTER_TO_LOCAL ?? canvasFromRoom.scale);
            DrawTransitionPreview(roomFromCanvas, coping, heading, camera, inches, turns, parent, deckDepth, deckScale);
        }

        internal void DrawTransitionPreview(TrTransform roomFromCanvas, Vector3 coping, Vector3 heading,
            Camera camera, int inches, int quarterTurns, Transform parent = null,
            int deckDepthInches = TransitionBrush.DefaultDeckDepthInches, float deckScale = 1f)
        {
            if (m_TransitionPreviewLine == null)
                CreateTransitionPreview(parent != null ? parent : transform);
            m_TransitionPreviewLine.gameObject.SetActive(true);
            float radius = inches * TransitionBrush.UnitsPerInch;
            float deckDepth = deckDepthInches * TransitionBrush.UnitsPerInch * deckScale;
            bool belowFloor = coping.y <= 0f;
            Vector3 tip = roomFromCanvas * coping;
            Transform head = camera.transform;
            Vector3 labelViewport = camera.WorldToViewportPoint(tip + head.up * 1.45f);
            bool labelInView = InPreviewView(labelViewport, camera.nearClipPlane, 0.28f);
            bool profileInView = false;
            if (!belowFloor)
            {
                Vector3 across = TransitionBrush.Across(heading, quarterTurns);
                var profile = new TransitionBrush.SurfaceProfile(coping.y, radius, deckDepth);
                for (int row = 0; row < m_TransitionPreviewPoints.Length; row++)
                {
                    m_TransitionPreviewPoints[row] = roomFromCanvas *
                        profile.Vertex(coping, across, row, out _);
                    if (!profileInView)
                        profileInView = InPreviewView(camera.WorldToViewportPoint(m_TransitionPreviewPoints[row]),
                            camera.nearClipPlane, 0.06f);
                }
            }

            // Keep the readout reachable when the drawing controller starts offscreen or on the floor.
            // The smaller cross section in that case uses the same profile, scaled to fit beside the readout.
            if (!labelInView || !profileInView)
            {
                if (labelViewport.z <= camera.nearClipPlane)
                    labelViewport = new Vector3(0.5f, 0.44f, 6f);
                labelViewport.x = Mathf.Clamp(labelViewport.x, 0.28f, 0.72f);
                labelViewport.y = Mathf.Clamp(labelViewport.y, profileInView ? 0.24f : 0.44f, 0.81f);
            }
            Vector3 labelPosition = camera.ViewportToWorldPoint(labelViewport);
            float readoutScale = Mathf.Clamp(Vector3.Distance(labelPosition, head.position) / 5f, 0.7f, 2.5f);
            m_TransitionPreviewLine.widthMultiplier = profileInView ? 0.11f : 0.08f * readoutScale;
            if (!profileInView)
            {
                float profileHeight = belowFloor ? radius : coping.y;
                float scale = 1.5f * readoutScale / Mathf.Max(radius + deckDepth, profileHeight);
                Vector3 right = head.right * (quarterTurns >= 2 ? -1f : 1f);
                Vector3 origin = labelPosition - head.up * (2.8f * readoutScale);
                var profile = new TransitionBrush.SurfaceProfile(profileHeight, radius, deckDepth);
                float center = (profile.Sample(0, out _).x +
                    profile.Sample(TransitionBrush.ProfilePoints - 1, out _).x) * 0.5f;
                for (int row = 0; row < m_TransitionPreviewPoints.Length; row++)
                {
                    Vector2 sample = profile.Sample(row, out _);
                    m_TransitionPreviewPoints[row] = origin + scale * ((sample.x - center) * right + sample.y * head.up);
                }
            }
            m_TransitionPreviewLine.SetPositions(m_TransitionPreviewPoints);

            int heightInches = Mathf.RoundToInt(coping.y / TransitionBrush.UnitsPerInch);
            if (m_PreviewRadiusInches != inches || m_PreviewHeightInches != heightInches ||
                m_PreviewQuarterTurns != quarterTurns || m_PreviewDeckDepthInches != deckDepthInches)
            {
                m_PreviewRadiusInches = inches;
                m_PreviewHeightInches = heightInches;
                m_PreviewQuarterTurns = quarterTurns;
                m_PreviewDeckDepthInches = deckDepthInches;
                m_TransitionReadout.text =
                    $"Radius {TransitionBrush.FormatFeetAndInches(inches)}\n" +
                    $"Height {TransitionBrush.FormatFeetAndInches(heightInches)}\n" +
                    $"Orientation {quarterTurns * 90}°\n" +
                    $"Deck Depth {TransitionBrush.FormatFeetAndInches(deckDepthInches)}";
            }
            Transform label = m_TransitionReadout.transform;
            label.SetPositionAndRotation(labelPosition, Quaternion.LookRotation(labelPosition - head.position, head.up));
            label.localScale = Vector3.one * (readoutScale / label.parent.lossyScale.x);
            m_TransitionPreviewStatus = profileInView ? "Full-size floor profile" :
                (belowFloor ? "Compact profile, tip below sketch floor" : "Compact profile, floor profile outside view");
        }

        private static bool InPreviewView(Vector3 point, float nearClip, float margin) =>
            point.z > nearClip && point.x > margin && point.x < 1f - margin &&
            point.y > margin && point.y < 1f - margin;

        internal string DescribeTransitionPreview()
        {
            if (m_TransitionPreviewLine == null || !m_TransitionPreviewLine.gameObject.activeInHierarchy)
                return m_TransitionPreviewStatus;

            Camera camera = App.VrSdk.GetVrCamera();
            var line = m_TransitionPreviewLine;
            var label = m_TransitionReadout;
            return $"{m_TransitionPreviewStatus}, camera layer={(camera.cullingMask & (1 << line.gameObject.layer)) != 0}, " +
                $"line={line.enabled}, label={label.renderer.enabled}, text={label.text.Replace("\n", " / ")}, " +
                $"heading={m_DisplayedTransitionOrientation.eulerAngles.y:F1}°, tracking={m_TransitionHeadingTracked}, " +
                $"locked={m_TransitionHeadingLocked}, " +
                $"readout viewport={camera.WorldToViewportPoint(label.transform.position):F3}";
        }

        private void CreateTransitionPreview(Transform parent)
        {
            var root = new GameObject("Transition profile preview");
            root.layer = gameObject.layer;
            root.transform.SetParent(parent, false);
            m_TransitionPreviewMaterial = new Material(m_BimanualGuideLine.GetComponent<Renderer>().sharedMaterial);
            m_TransitionPreviewMaterial.SetColor("_Color", new Color(0.16f, 0.72f, 0.86f));
            m_TransitionPreviewMaterial.SetFloat("_Cull", (float)CullMode.Off);
            m_TransitionPreviewMaterial.SetFloat("_ZTest", (float)CompareFunction.Always);
            m_TransitionPreviewMaterial.SetFloat("_ZWrite", 0f);
            m_TransitionPreviewMaterial.renderQueue = (int)RenderQueue.Overlay;
            m_TransitionPreviewLine = root.AddComponent<LineRenderer>();
            m_TransitionPreviewLine.useWorldSpace = true;
            m_TransitionPreviewLine.positionCount = m_TransitionPreviewPoints.Length;
            m_TransitionPreviewLine.startWidth = 1f;
            m_TransitionPreviewLine.endWidth = 1f;
            m_TransitionPreviewLine.numCapVertices = 3;
            m_TransitionPreviewLine.sharedMaterial = m_TransitionPreviewMaterial;
            m_TransitionPreviewLine.shadowCastingMode = ShadowCastingMode.Off;
            m_TransitionPreviewLine.receiveShadows = false;
            m_TransitionPreviewLine.allowOcclusionWhenDynamic = false;

            var label = new GameObject("Transition measurements");
            label.layer = root.layer;
            label.transform.SetParent(root.transform, false);
            m_TransitionReadout = label.AddComponent<TextMeshPro>();
            m_TransitionReadout.alignment = TextAlignmentOptions.Center;
            m_TransitionReadout.color = Color.white;
            m_TransitionReadout.fontSize = 3.6f;
            m_TransitionReadout.textWrappingMode = TextWrappingModes.NoWrap;
            m_TransitionLabelMaterial = CreateTransitionOverlayTextMaterial(m_TransitionReadout);
        }

        private static Material CreateTransitionOverlayTextMaterial(TextMeshPro label)
        {
            var reference = Resources.Load<Material>("Brushes/Basic/Transition/TransitionOverlayText");
            // Keep the configured font atlas while switching its renderer to the always-visible shader.
            var material = new Material(label.fontSharedMaterial) { shader = reference.shader };
            material.renderQueue = (int)RenderQueue.Overlay + 1;
            material.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
            material.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.12f);
            label.fontSharedMaterial = material;
            label.renderer.shadowCastingMode = ShadowCastingMode.Off;
            label.renderer.receiveShadows = false;
            label.renderer.allowOcclusionWhenDynamic = false;
            return material;
        }
    }
}
