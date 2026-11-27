// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using StrokeFlags = TiltBrush.SketchMemoryScript.StrokeFlags;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        private const float kCopingEndSnapRadius = 0.10f * App.METERS_TO_UNITS;
        private const float kCopingEndReleaseRadius = 0.14f * App.METERS_TO_UNITS;
        private const float kCopingClosedJointTolerance = 0.002f * App.METERS_TO_UNITS;
        private const string kCopingQuarterTurnsPreference = "ModularCopingQuarterTurns";
        private const string kCopingLengthPreference = "ModularCopingLengthPercent";

        private readonly struct CopingEndpoint
        {
            public readonly Stroke Stroke;
            public readonly bool IsEnd;
            public readonly TrTransform Pose;

            public CopingEndpoint(Stroke stroke, bool isEnd, TrTransform pose)
            {
                Stroke = stroke;
                IsEnd = isEnd;
                Pose = pose;
            }

            public bool SameSocket(CopingEndpoint other) => Stroke == other.Stroke && IsEnd == other.IsEnd;
        }

        private readonly List<CopingEndpoint> m_CopingEndpoints = new();
        private CopingEndpoint? m_CopingSnap;
        private CopingEndpoint? m_CopingReleasedSocket;
        private bool m_CopingPlacementArmed;
        private bool m_CopingPanelOpened;
        private TrTransform? m_CopingPanelPlacement;
        private GameObject m_CopingPreview;
        private MeshFilter m_CopingPreviewFilter;
        private Mesh m_CopingPreviewMesh;
        private ModularCopingPiece m_PreviewCopingPiece;
        private StrokeFlags m_PreviewCopingFlags;
        private Color? m_PreviewCopingColor;

        internal bool UsesModularCoping => UsesSplinePreview && ModularCopingBrush.IsModular(SplineBrush);
        internal ModularCopingPiece CurrentModularCopingPiece { get; private set; }
        internal int ModularCopingLengthPercent => Mathf.Clamp(PlayerPrefs.GetInt(kCopingLengthPreference, 100), 0, 100);

        internal void StepModularCopingLength(int delta)
        {
            if (!UsesModularCoping)
                return;
            int previous = ModularCopingLengthPercent;
            int next = Mathf.Clamp(previous + delta, 0, 100);
            if (next == previous)
                return;
            PlayerPrefs.SetInt(kCopingLengthPreference, next);
            m_CopingPlacementArmed = false;
            if (next == 0)
                CancelModularCopingPlacement();
        }

        internal void SetModularCopingPiece(ModularCopingPiece piece)
        {
            if (!UsesModularCoping || piece == CurrentModularCopingPiece)
                return;
            CurrentModularCopingPiece = piece;
            CancelModularCopingPlacement();
        }

        private void UpdateModularCopingPlacement(bool canDraw)
        {
            if (!canDraw || App.ActiveCanvas == null)
            {
                CancelModularCopingPlacement();
                return;
            }

            if (m_SplineCanvas == null)
                BeginSplineDraft();
            if (!m_brushTrigger)
                OpenModularCopingPanel();
            int length = ModularCopingLengthPercent;
            if (length == 0)
            {
                CancelModularCopingPlacement();
                return;
            }

            int quarterTurns = PlayerPrefs.GetInt(kCopingQuarterTurnsPreference, 0) & 3;
            if (!m_brushTrigger && !m_wandTrigger && !m_CopingPlacementArmed &&
                InputManager.Brush.GetVrInputDown(VrInput.Thumbstick))
            {
                quarterTurns = (quarterTurns + 1) & 3;
                PlayerPrefs.SetInt(kCopingQuarterTurnsPreference, quarterTurns);
                if (m_CopingSnap.HasValue)
                    m_CopingReleasedSocket = m_CopingSnap;
                m_CopingSnap = null;
                InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.055f);
            }

            var piece = CurrentModularCopingPiece;
            TrTransform worldFromCanvas = m_SplineCanvas.Pose;
            (Vector3 worldPosition, Quaternion worldRotation) = GetPointerPosition();
            worldRotation = TransitionBrush.HorizontalOrientation(worldRotation);
            TrTransform cursor = worldFromCanvas.inverse * TrTransform.TR(worldPosition, worldRotation);
            TrTransform anchor;
            TrTransform placement;
            StrokeFlags flags;
            if (TryFindCopingEnd(cursor, worldFromCanvas.scale, out var endpoint))
            {
                if (!m_CopingSnap.HasValue || !endpoint.SameSocket(m_CopingSnap.Value))
                    InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.06f);
                m_CopingSnap = endpoint;
                anchor = endpoint.Pose;
                placement = JoinModularCopingEndpoint(piece, anchor, endpoint.IsEnd,
                    ModularCopingBrush.HasMortar(endpoint.Stroke, endpoint.IsEnd), out flags, length);
            }
            else
            {
                m_CopingSnap = null;
                anchor = RotateModularCopingAnchor(worldFromCanvas, SnapSplineKnot(cursor), quarterTurns);
                flags = ModularCopingBrush.FlagsFor(piece, length);
                placement = anchor * ModularCopingBrush.Midpoint(piece, flags).inverse;
            }

            TrTransform pointer = worldFromCanvas * anchor;
            PointerManager.m_Instance.SetPointerTransform(InputManager.ControllerName.Brush,
                pointer.translation, pointer.rotation);
            ShowModularCopingPreview(piece, placement, flags);
            if (m_brushTriggerDown && m_brushTrigger)
                m_CopingPlacementArmed = true;
            if (m_CopingPlacementArmed && !m_brushTrigger)
            {
                m_CopingPlacementArmed = false;
                CommitModularCoping(piece, placement, flags);
            }
        }

        internal static TrTransform RotateModularCopingAnchor(TrTransform worldFromCanvas, TrTransform anchor,
            int quarterTurns)
        {
            Quaternion heading = TransitionBrush.HorizontalOrientation(worldFromCanvas.rotation * anchor.rotation);
            Quaternion turn = Quaternion.AngleAxis((quarterTurns & 3) * 90f, Vector3.up);
            anchor.rotation = Quaternion.Inverse(worldFromCanvas.rotation) * turn * heading;
            return anchor;
        }

        internal static TrTransform GetModularCopingSocket(StrokeData stroke, bool end)
        {
            var first = stroke.m_ControlPoints[0];
            var pose = TrTransform.TRS(first.m_Pos, first.m_Orient, stroke.m_BrushScale);
            var face = pose * ModularCopingBrush.Connector(ModularCopingBrush.PieceFor(stroke), end, stroke.m_Flags);
            return ModularCopingBrush.HasMortar(stroke, end) ? OffsetCopingSocket(face, end) : face;
        }

        internal static TrTransform JoinModularCopingEndpoint(ModularCopingPiece piece, TrTransform socket,
            bool targetEnd, bool targetHasMortar, out StrokeFlags flags, int lengthPercent = 100)
        {
            flags = ModularCopingBrush.FlagsFor(piece, lengthPercent);
            bool attachEnd = !targetEnd;
            // The piece placed against a bare face supplies the mortar for this joint.
            if (!targetHasMortar)
            {
                socket = OffsetCopingSocket(socket, targetEnd);
                flags |= ModularCopingBrush.MortarFlags(attachEnd);
            }
            return socket * ModularCopingBrush.Connector(piece, attachEnd, flags).inverse;
        }

        private static TrTransform OffsetCopingSocket(TrTransform face, bool end) =>
            face * TrTransform.T(Vector3.forward * (end ? ModularCopingBrush.MortarThickness :
                -ModularCopingBrush.MortarThickness));

        private bool TryFindCopingEnd(TrTransform cursor, float canvasScale, out CopingEndpoint best)
        {
            m_CopingEndpoints.Clear();
            foreach (Stroke stroke in m_SplineMemory.GetMemoryList)
            {
                if (!stroke.IsGeometryEnabled || stroke.Canvas != m_SplineCanvas ||
                    stroke.m_ControlPoints == null || stroke.m_ControlPoints.Length == 0 ||
                    !ModularCopingBrush.IsModular(BrushCatalog.m_Instance.GetBrush(stroke.m_BrushGuid)))
                    continue;

                m_CopingEndpoints.Add(new CopingEndpoint(stroke, false, GetModularCopingSocket(stroke, false)));
                m_CopingEndpoints.Add(new CopingEndpoint(stroke, true, GetModularCopingSocket(stroke, true)));
            }

            float snapRadius = kCopingEndSnapRadius / canvasScale;
            float releaseRadius = kCopingEndReleaseRadius / canvasScale;
            float releaseRadiusSquared = releaseRadius * releaseRadius;
            float jointTolerance = kCopingClosedJointTolerance / canvasScale;
            float nearest = snapRadius * snapRadius;
            // A manual turn takes the current joint out of snapping range until the controller moves away.
            if (m_CopingReleasedSocket is CopingEndpoint released &&
                (released.Pose.translation - cursor.translation).sqrMagnitude > releaseRadiusSquared)
                m_CopingReleasedSocket = null;
            best = default;
            foreach (var endpoint in m_CopingEndpoints)
            {
                if (m_CopingReleasedSocket is CopingEndpoint ignored && endpoint.SameSocket(ignored))
                    continue;
                // A scaled copy may have a different lip width from a new piece.
                if (Mathf.Abs(endpoint.Pose.scale / cursor.scale - 1f) > 0.001f)
                    continue;
                float distance = (endpoint.Pose.translation - cursor.translation).sqrMagnitude;
                bool latched = m_CopingSnap is CopingEndpoint previous && endpoint.SameSocket(previous);
                if (distance > (latched ? releaseRadiusSquared : nearest) ||
                    !IsFreeCopingEnd(endpoint, jointTolerance * jointTolerance))
                    continue;
                best = endpoint;
                nearest = distance;
                if (latched)
                    return true;
            }
            return best.Stroke != null;
        }

        private bool IsFreeCopingEnd(CopingEndpoint endpoint, float toleranceSquared)
        {
            foreach (var other in m_CopingEndpoints)
            {
                if (other.Stroke != endpoint.Stroke && other.IsEnd != endpoint.IsEnd &&
                    (other.Pose.translation - endpoint.Pose.translation).sqrMagnitude <= toleranceSquared &&
                    Quaternion.Angle(other.Pose.rotation, endpoint.Pose.rotation) <= 1f)
                    return false;
            }
            return true;
        }

        private void CommitModularCoping(ModularCopingPiece piece, TrTransform placement, StrokeFlags flags)
        {
            var brush = SplineBrush;
            uint time = (uint)(App.Instance.CurrentSketchTime * 1000);
            var points = ModularCopingBrush.CreateControlPoints(piece, placement, time, flags);
            var stroke = new Stroke
            {
                m_Type = Stroke.Type.NotCreated,
                m_IntendedCanvas = m_SplineCanvas,
                m_BrushGuid = brush.m_Guid,
                m_BrushSize = brush.m_BrushSizeRange.x,
                m_BrushScale = placement.scale,
                m_Flags = flags,
                m_Color = App.BrushColor.CurrentColor,
                m_ControlPoints = points,
                m_ControlPointsToDrop = new bool[points.Length],
            };
            stroke.Recreate();
            if (stroke.m_Type == Stroke.Type.NotCreated)
                return;
            m_SplineMemory.MemoryListAdd(stroke);
            m_SplineMemory.PerformAndRecordCommand(new BrushStrokeCommand(stroke));
        }

        private void ShowModularCopingPreview(ModularCopingPiece piece, TrTransform placement, StrokeFlags flags)
        {
            if (m_CopingPreview == null)
            {
                m_CopingPreview = new GameObject("Modular coping preview") { layer = gameObject.layer };
                m_CopingPreviewFilter = m_CopingPreview.AddComponent<MeshFilter>();
                m_CopingPreviewFilter.sharedMesh = m_CopingPreviewMesh;
                var renderer = m_CopingPreview.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = SplineBrush.Material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            Color color = App.BrushColor.CurrentColor;
            if (m_CopingPreviewMesh == null || m_PreviewCopingPiece != piece ||
                m_PreviewCopingFlags != flags || m_PreviewCopingColor != color)
            {
                Destroy(m_CopingPreviewMesh);
                m_CopingPreviewMesh = ModularCopingBrush.CreatePreviewMesh(piece, flags, (Color32)color);
                m_CopingPreviewFilter.sharedMesh = m_CopingPreviewMesh;
                m_PreviewCopingPiece = piece;
                m_PreviewCopingFlags = flags;
                m_PreviewCopingColor = color;
            }
            var preview = m_CopingPreview.transform;
            if (preview.parent != m_SplineCanvas.transform)
                preview.SetParent(m_SplineCanvas.transform, false);
            preview.SetLocalPositionAndRotation(placement.translation, placement.rotation);
            preview.localScale = Vector3.one * placement.scale;
            m_CopingPreview.SetActive(true);
        }

        private void OpenModularCopingPanel()
        {
            if (m_CopingPanelOpened)
                return;
            m_CopingPanelOpened = true;
            const BasePanel.PanelType panelType = BasePanel.PanelType.ModularCoping;
            if (PanelManager.m_Instance.IsPanelOpen(panelType))
                return;
            if (m_CopingPanelPlacement is TrTransform placement)
                SketchControlsScript.m_Instance.OpenPanelOfType(panelType, placement);
            else
                OpenSplinePanel(panelType, -1.85f, distance: 4.6f, heightOffset: -0.55f);
        }

        private void CancelModularCopingPlacement()
        {
            m_CopingPlacementArmed = false;
            m_CopingSnap = null;
            m_CopingReleasedSocket = null;
            if (m_CopingPreview != null)
                m_CopingPreview.SetActive(false);
        }

        private void ExitModularCoping()
        {
            CancelModularCopingPlacement();
            if (!m_CopingPanelOpened)
                return;
            m_CopingPanelOpened = false;
            const BasePanel.PanelType panelType = BasePanel.PanelType.ModularCoping;
            var panels = PanelManager.m_Instance;
            if (panels.IsPanelOpen(panelType) && panels.GetPanelByType(panelType) is BasePanel panel)
            {
                m_CopingPanelPlacement = Coords.AsRoom[panel.transform];
                panels.HidePanel(panelType);
            }
        }

        private void DestroyModularCopingPreview()
        {
            Destroy(m_CopingPreview);
            Destroy(m_CopingPreviewMesh);
        }
    }
}
