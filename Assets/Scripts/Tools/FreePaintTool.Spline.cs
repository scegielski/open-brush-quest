// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using OpenBrush.Multiplayer;
using Unity.Profiling;
using UnityEngine;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        internal enum SplineAction
        {
            MoveAndRotate, Append, Insert, Delete, DeckDepth, ToggleLoop, NewSpline, ToggleAutoOrient, SelectCurve, Draw
        }

        internal struct TransitionSettings
        {
            internal int RadiusHalfInches;
            internal int DeckDepthHalfInches;
            internal int SteelDiameterHalfInches;
            internal bool MixedDeckDepth;
            internal bool IsSelection;
            internal bool IsStandaloneSteel;
            internal TransitionBrush.SurfaceOptions Options;
        }

        private const float kSplineHandleSize = 0.065f;
        private const float kSplinePickRadius = 0.095f;
        private const float kSplineHandleGrabRadius = 0.16f;
        private const float kSplineHullWidth = 0.009f;
        private const float kSplineMinimumSpacing = 0.011f;
        private const float kSplineSelectReach = 12f * App.METERS_TO_UNITS;
        private const float kSplineSelectRayWidth = 0.0045f * App.METERS_TO_UNITS;
        private static readonly Color kSplineSelectRayColor = new(0.08f, 0.86f, 1f);
        private static readonly Color kSplineSelectHitColor = new(1f, 0.8f, 0.15f);
        private static readonly ProfilerMarker kRebuildDraftSplinePreview =
            new("FreePaintTool.RebuildDraftSplinePreview");

        // For Transition, scale holds the deck depth relative to the default depth (1).
        private readonly List<TrTransform> m_SplineKnots = new();
        private readonly List<TrTransform> m_SplineSamples = new();
        private readonly List<PointerManager.ControlPoint> m_SplineStrokePoints = new();
        private readonly List<Transform> m_SplineHandles = new();
        private readonly SplineStrokePicker m_SplineStrokePicker = new();
        private readonly Stack<(TrTransform[] Knots, bool Closed, bool AutoOrient)> m_SplineDraftUndo = new();
        private readonly Stack<(TrTransform[] Knots, bool Closed, bool AutoOrient)> m_SplineDraftRedo = new();
        private CanvasScript m_SplineCanvas;
        private SketchMemoryScript m_SplineMemory;
        private GameObject m_SplinePreview;
        private LineRenderer m_SplineHull;
        private LineRenderer m_SplineSelectRay;
        private Material m_SplineSelectRayMaterial;
        private Material m_SplineHullMaterial;
        private bool m_SplinePreviewDirty;
        private BaseBrushScript m_DraftSplinePreviewBrush;
        private BrushDescriptor m_DraftSplineDescriptor;
        private float m_DraftSplineSize;
        private float m_DraftSplineDeckDepth;
        private float m_DraftSplineFloorHeight;
        private float m_DraftSplineStartingDeckDepth;
        private TransitionBrush.SurfaceOptions? m_DraftSplineSurfaceOptions;
        private bool m_TransitionSettingsOpenedForPlacement;
        private TrTransform? m_TransitionSettingsPlacement;
        private Color m_DraftSplineColor;
        private int m_DraftSplineQuarterTurns;
        private Material m_SplineMaterial;
        private Material m_SplineSelectedMaterial;
        private int m_DraggedSplineKnot = -1;
        private int m_HoveredSplineKnot = -1;
        private Vector3 m_SplineDragOffset;
        private Vector3 m_SplineMoveAtGrab;
        private bool m_SplineMoveStarted;
        private Quaternion m_SplineControllerAtGrab;
        private Quaternion m_SplineKnotAtGrab;
        private float m_DeckDepthAtGrab;
        private Vector3 m_DeckControllerAtGrab;
        private Vector3 m_DeckDragUp;
        private (TrTransform[] Knots, bool Closed, bool AutoOrient)? m_SplineDraftBeforeEdit;
        private bool m_SplineClosed;
        private bool m_DraftSplineAutoOrient;
        private Stroke m_EditableSpline;
        private StrokeData m_SplineHeadingEditData;
        private BrushDescriptor m_SplineEditBrush;
        private bool m_EditingTransitionSpline;
        private PointerManager.ControlPoint[] m_LastAppliedSplinePoints;
        private TrTransform[] m_LastAppliedSplineControls;
        private BaseBrushScript m_SavedSplinePreviewBrush;
        private SplineAction m_SplineAction;
        private int m_SplineTopologyIndex = -1;
        private float m_SplineInsertionT;

        internal bool UsesSplinePreview => InputManager.m_Instance.AllowVrControllers &&
            App.VrSdk.GetControllerDof() == VrSdk.DoF.Six &&
            ApiManager.Instance.ForcePainting == ApiManager.ForcePaintingMode.None;
        internal bool OwnsSplineDraftHistory => UsesSplinePreview && m_SplineCanvas == App.ActiveCanvas &&
            m_EditableSpline == null &&
            (m_SplineKnots.Count > 0 || m_SplineDraftUndo.Count > 0 || m_SplineDraftRedo.Count > 0);
        internal bool CanUndoSplineDraft => m_DraggedSplineKnot < 0 && m_SplineDraftUndo.Count > 0;
        internal bool CanRedoSplineDraft => m_DraggedSplineKnot < 0 && m_SplineDraftRedo.Count > 0;
        private StrokeData SplineEditData => m_SplineHeadingEditData ?? m_EditableSpline;
        private BrushDescriptor SplineBrush => m_EditableSpline != null ? m_SplineEditBrush :
            m_DraftSplineDescriptor ?? PointerManager.m_Instance.MainPointer.CurrentBrush;
        private int DraftSplineQuarterTurns => m_DraftSplineDescriptor != null ?
            m_DraftSplineQuarterTurns : TransitionBrush.PreferredQuarterTurns;
        private float DraftSplineRadiusInches => m_DraftSplineDescriptor != null ?
            m_DraftSplineSize / TransitionBrush.UnitsPerInch : TransitionBrush.RadiusInches;
        private float DraftSplineDeckDepth => m_DraftSplineDescriptor != null ?
            m_DraftSplineDeckDepth : TransitionBrush.DeckDepthInches;
        private bool SplineTracksTransitionHeading => m_EditableSpline != null
            ? m_EditingTransitionSpline && TransitionBrush.HasControllerHeading(SplineEditData)
            : TransitionBrush.IsTransition(SplineBrush);
        private bool SplineUsesHeadingOffsets => m_EditingTransitionSpline &&
            TransitionBrush.HasHeadingOffsets(SplineEditData);
        private TransitionBrush.SurfaceOptions SplineSurfaceOptions => m_EditableSpline != null
            ? TransitionBrush.OptionsFor(SplineBrush, SplineEditData)
            : m_DraftSplineSurfaceOptions ?? TransitionBrush.OptionsFor(SplineBrush);
        private bool SplineHasDeck => SplineSurfaceOptions.Deck;
        internal bool SplineAutoOrientEnabled => m_EditableSpline == null ? m_DraftSplineAutoOrient :
            SplineCurve.AutoOrients(SplineEditData);
        private bool CanEditSelectedSplineKnots => m_EditableSpline == null || SplineCurve.IsEditable(m_EditableSpline);
        private bool IsDeckDepthAction => m_SplineAction == SplineAction.DeckDepth;

        private bool UpdateSplineDrawingMode()
        {
            var sketchControls = SketchControlsScript.m_Instance;
            bool canDraw = m_AllowDrawing && !m_EatInput && !m_ToolHidden &&
                App.Instance.IsInStateThatAllowsPainting() && sketchControls.IsFreepaintToolReady() &&
                !MultiplayerManager.m_Instance.IsViewOnly;

            // Temporarily suspend the draft while another input mode owns the pointer.
            if (!UsesSplinePreview)
            {
                ExitModularCoping();
                SetSplinePreviewVisible(false);
                HideTransitionSettingsPanel();
                if (ModularCopingBrush.IsModular(SplineBrush))
                {
                    m_PaintingActive = false;
                    PointerManager.m_Instance.EnableLine(false);
                    return true;
                }
                return false;
            }

            ClearSplineOnCanvasChange();

            m_PaintingActive = false;
            PointerManager.m_Instance.EnableLine(false);
            if (m_BimanualTape)
                EndBimanualTape();
            if (UsesModularCoping)
            {
                SetSplinePreviewVisible(false);
                HideTransitionSettingsPanel();
                UpdateModularCopingPlacement(canDraw);
                return true;
            }
            ExitModularCoping();
            if (!canDraw || App.ActiveCanvas == null)
            {
                PauseSplineInput();
                return true;
            }

            if (m_SplineCanvas == null)
                BeginSplineDraft();
            RefreshEditableSpline();
            if (!m_brushTrigger)
                ReleaseSplineKnot();
            UpdateTransitionSettingsPanel(!m_brushTrigger);

            SetSplinePreviewVisible(true);
            if (UpdateSplinePanelShortcut())
                return true;

            var worldFromCanvas = m_SplineCanvas.Pose;
            if (m_SplineAction == SplineAction.SelectCurve)
            {
                UpdateSplineSelection();
                return true;
            }
            if (m_SplineSelectRay != null)
                m_SplineSelectRay.enabled = false;
            (Vector3 worldPosition, Quaternion worldRotation) = GetPointerPosition();
            var cursor = worldFromCanvas.inverse * TrTransform.TR(worldPosition, worldRotation);
            Quaternion controllerRotation = cursor.rotation;
            float pickRadius = kSplinePickRadius / worldFromCanvas.scale;
            int highlighted = UpdateSplineActionAtCursor(cursor, pickRadius, kSplineHandleGrabRadius / worldFromCanvas.scale);
            MoveSplineKnot(cursor, controllerRotation);
            var pointerPose = worldFromCanvas *
                (m_DraggedSplineKnot >= 0 ? m_SplineKnots[m_DraggedSplineKnot] : SnapSplineKnot(cursor));
            PointerManager.m_Instance.SetPointerTransform(InputManager.ControllerName.Brush,
                pointerPose.translation, pointerPose.rotation);
            UpdateSplineBrushPreview();

            for (int i = 0; i < m_SplineHandles.Count; i++)
            {
                bool selected = i == highlighted;
                m_SplineHandles[i].localScale = Vector3.one *
                    (kSplineHandleSize * (selected ? 1.5f : 1f) / worldFromCanvas.scale);
                m_SplineHandles[i].GetComponent<Renderer>().sharedMaterial =
                    selected ? m_SplineSelectedMaterial : m_SplineMaterial;
            }
            return true;
        }

        private void BeginSplineDraft()
        {
            m_SplineCanvas = App.ActiveCanvas;
            m_SplineMemory = SketchMemoryScript.m_Instance;
            m_SplineMemory.SketchClearing += OnSplineSketchClearing;
        }

        private void ClearSplineOnCanvasChange()
        {
            if (m_SplineCanvas == null || m_SplineCanvas == App.ActiveCanvas)
                return;

            CancelModularCopingPlacement();
            bool hadDraft = m_EditableSpline == null && m_SplineKnots.Count > 0;
            if (m_EditableSpline != null)
                ReleaseSplineKnot();
            ClearSplineDraft();
            if (hadDraft)
                ShowSplineHint("Spline canceled: drawing layer changed");
        }

        internal void StartDrawing()
        {
            EatInput();
            if (!UsesSplinePreview || App.ActiveCanvas == null)
                return;

            ClearSplineOnCanvasChange();
            if (m_SplineCanvas == null && !UsesModularCoping)
                BeginSplineDraft();
            ChooseSplineAction(SplineAction.Draw);
        }

        internal bool TryFinishSplineForBrushChange(BrushDescriptor paletteBrush = null)
        {
            if (App.Instance == null || !App.Instance.IsInStateThatAllowsPainting() || !UsesSplinePreview)
                return true;

            bool fromPalette = paletteBrush != null;
            bool drawNext = m_SplineAction == SplineAction.Draw &&
                (!fromPalette || !ModularCopingBrush.IsModular(paletteBrush));
            var nextAction = drawNext ? SplineAction.Draw : SplineAction.MoveAndRotate;
            if (m_SplineCanvas != null && m_SplineCanvas == App.ActiveCanvas)
            {
                if (m_EditableSpline != null || OwnsSplineDraftHistory)
                    return TryFinishSplineAndStartAnother(keepTransitionSettings: false, nextAction: nextAction);
                m_DraftSplineSurfaceOptions = null;
                if (fromPalette)
                    PauseSplineInput();
            }

            if (fromPalette)
            {
                EatInput();
                m_SplineAction = nextAction;
            }
            return true;
        }

        internal bool TryFinishSplineForStrokeTool()
        {
            if (m_SplineCanvas == null || m_SplineCanvas != App.ActiveCanvas || !UsesSplinePreview ||
                !App.Instance.IsInStateThatAllowsPainting())
                return true;

            return TryFinishSplineAndStartAnother(keepTransitionSettings: true,
                nextAction: m_SplineAction == SplineAction.Draw ? SplineAction.Draw : SplineAction.MoveAndRotate,
                completeDraftOnly: true);
        }

        private bool TryFinishSplineAndStartAnother(bool keepTransitionSettings, SplineAction nextAction,
            bool completeDraftOnly = false)
        {
            RefreshEditableSpline();
            PauseSplineInput();
            EatInput();
            bool hasCompleteDraft = m_EditableSpline == null && m_SplineKnots.Count >= 2;
            if (hasCompleteDraft && !CommitSpline())
            {
                ShowSplineHint("Separate overlapping points before finishing");
                return false;
            }
            if (completeDraftOnly && !hasCompleteDraft)
            {
                m_SplineAction = nextAction;
                return true;
            }

            var options = keepTransitionSettings ? m_DraftSplineSurfaceOptions : null;
            ClearSplineDraft();
            BeginSplineDraft();
            m_DraftSplineSurfaceOptions = options;
            m_SplineAction = nextAction;
            return true;
        }

        private void PlaceSplineKnot(TrTransform cursor)
        {
            if (m_SplineKnots.Count == SplineCurve.MaxKnots)
            {
                ShowSplineHint("Spline point limit reached");
                return;
            }
            if (m_DraftSplineDescriptor == null)
            {
                var pointer = PointerManager.m_Instance.MainPointer;
                m_DraftSplineDescriptor = pointer.CurrentBrush;
                m_DraftSplineSize = TransitionBrush.IsTransition(m_DraftSplineDescriptor)
                    ? TransitionBrush.RadiusInches * TransitionBrush.UnitsPerInch : pointer.BrushSizeAbsolute;
                m_DraftSplineDeckDepth = TransitionBrush.DeckDepthInches;
                m_DraftSplineStartingDeckDepth = m_DraftSplineDeckDepth;
                UpdateDraftSplineAppearance(pointer);
            }
            Quaternion controllerRotation = cursor.rotation;
            cursor = SnapSplineKnot(cursor);
            if (TransitionBrush.IsTransition(SplineBrush))
                cursor.scale = m_DraftSplineStartingDeckDepth / TransitionBrush.DefaultDeckDepthInches;
            if (SplineTracksTransitionHeading)
                cursor.rotation = SnapTransitionAngle(TransitionBrush.HorizontalOrientation(controllerRotation), m_SplineCanvas);

            BeginSplineKnotEdit();
            m_DraggedSplineKnot = m_SplineKnots.Count;
            m_SplineTopologyIndex = m_DraggedSplineKnot;
            m_SplineDragOffset = Vector3.zero;
            AddSplineKnot(cursor);
            SampleSpline();
            InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.035f);
        }

        private void AddSplineKnot(TrTransform cursor, int index = -1)
        {
            if (index < 0)
                index = m_SplineKnots.Count;
            m_SplineKnots.Insert(index, cursor);
            var handle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            handle.name = "Spline control point";
            var collider = handle.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            handle.transform.SetParent(m_SplinePreview.transform, false);
            handle.transform.localPosition = cursor.translation;
            handle.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_SplineHandles.Insert(index, handle.transform);
        }

        private int FindHoveredSplineKnot(Vector3 position, float radius, bool endpointsOnly = false)
        {
            if (m_DraggedSplineKnot >= 0)
                return m_DraggedSplineKnot;

            int nearest = -1;
            float closest = radius * radius;
            for (int i = 0; i < m_SplineKnots.Count; i++)
            {
                if (endpointsOnly && i != 0 && i != m_SplineKnots.Count - 1)
                    continue;
                float distance = (m_SplineKnots[i].translation - position).sqrMagnitude;
                if (distance < closest)
                {
                    nearest = i;
                    closest = distance;
                }
            }

            // Give a previously hovered handle a slightly larger exit radius.
            const float exitRadiusFactor = 1.15f;
            float exitRadius = radius * exitRadiusFactor;
            if (nearest < 0 && m_HoveredSplineKnot >= 0 &&
                (!endpointsOnly || m_HoveredSplineKnot == 0 || m_HoveredSplineKnot == m_SplineKnots.Count - 1) &&
                (m_SplineKnots[m_HoveredSplineKnot].translation - position).sqrMagnitude < exitRadius * exitRadius)
                nearest = m_HoveredSplineKnot;

            if (nearest != m_HoveredSplineKnot && nearest >= 0 &&
                InputManager.Brush is UnityXRControllerInfo brushController)
                brushController.SendHapticImpulse(0.55f, 0.06f);
            m_HoveredSplineKnot = nearest;

            return nearest;
        }

        private int UpdateSplineActionAtCursor(TrTransform cursor, float radius, float handleRadius)
        {
            int nearest = m_SplineAction switch
            {
                SplineAction.MoveAndRotate or SplineAction.Delete or SplineAction.DeckDepth =>
                    FindHoveredSplineKnot(cursor.translation, handleRadius),
                SplineAction.Append when !m_SplineClosed => FindHoveredSplineKnot(cursor.translation, handleRadius, endpointsOnly: true),
                _ => m_DraggedSplineKnot
            };
            if (!m_brushTriggerDown)
                return nearest;

            switch (m_SplineAction)
            {
                case SplineAction.MoveAndRotate when nearest >= 0:
                    BeginSplineKnotEdit();
                    m_DraggedSplineKnot = nearest;
                    m_SplineDragOffset = m_SplineKnots[nearest].translation - cursor.translation;
                    m_SplineMoveAtGrab = cursor.translation;
                    m_SplineMoveStarted = false;
                    StartSplineRotation(cursor.rotation, m_SplineKnots[nearest].rotation);
                    break;
                case SplineAction.MoveAndRotate:
                    if (!OwnsSplineDraftHistory)
                    {
                        var hit = FindSavedSpline(cursor.translation, radius);
                        if (hit != null && hit != m_EditableSpline)
                            OpenSavedSpline(hit);
                    }
                    return -1;
                case SplineAction.Draw when m_EditableSpline == null && !m_SplineClosed:
                    PlaceSplineKnot(cursor);
                    return m_DraggedSplineKnot;
                case SplineAction.DeckDepth when nearest >= 0 && SplineHasDeck:
                    BeginSplineKnotEdit();
                    m_DraggedSplineKnot = nearest;
                    m_DeckDepthAtGrab = KnotDeckDepthInches(nearest);
                    m_DeckControllerAtGrab = cursor.translation;
                    m_DeckDragUp = Quaternion.Inverse(m_SplineCanvas.Pose.rotation) * Vector3.up;
                    break;
                case SplineAction.Append:
                    if (m_SplineClosed)
                        ShowSplineHint("Open the loop before appending");
                    else if (nearest >= 0)
                        InsertSplineKnot(cursor, nearest == 0 ? 0 : m_SplineKnots.Count);
                    else
                        ShowSplineHint("Grab either end and drag to append");
                    return m_DraggedSplineKnot;
                case SplineAction.Insert:
                    int insertAt = FindSplineInsertion(cursor.translation, radius, out m_SplineInsertionT);
                    if (insertAt >= 0)
                        InsertSplineKnot(cursor, insertAt);
                    return m_DraggedSplineKnot;
                case SplineAction.Delete when nearest >= 0:
                    int minimumKnots = MinimumSplineKnots;
                    if (m_SplineKnots.Count <= minimumKnots)
                    {
                        ShowSplineHint(m_SplineClosed ? "A loop needs at least three points" : "A spline needs at least two points");
                        break;
                    }
                    if (TryGetAdjacentSplineKnot(nearest, -1, out var before) &&
                        TryGetAdjacentSplineKnot(nearest, 1, out var after) &&
                        (before - after).sqrMagnitude < 0.0000000001f)
                    {
                        ShowSplineHint("Separate the neighboring points before deleting");
                        break;
                    }
                    BeginSplineKnotEdit();
                    m_SplineTopologyIndex = nearest;
                    RemoveSplineKnot(nearest);
                    SampleSpline();
                    if (m_EditableSpline != null)
                        ApplySavedSplineEdit();
                    else
                        RecordSplineDraftEdit();
                    m_SplineTopologyIndex = -1;
                    if (m_SplineKnots.Count == 0)
                        m_SplineAction = SplineAction.MoveAndRotate;
                    return -1;
            }
            return nearest;
        }

        private int MinimumSplineKnots => m_SplineClosed ? 3 : m_EditableSpline == null ? 0 : 2;

        private void BeginSplineKnotEdit()
        {
            if (m_EditableSpline == null)
                m_SplineDraftBeforeEdit = (m_SplineKnots.ToArray(), m_SplineClosed, m_DraftSplineAutoOrient);
        }

        private void InsertSplineKnot(TrTransform cursor, int index)
        {
            if (m_SplineKnots.Count >= SplineCurve.MaxKnots)
            {
                ShowSplineHint("Spline point limit reached");
                return;
            }
            BeginSplineKnotEdit();
            m_SplineTopologyIndex = index;
            m_DraggedSplineKnot = index;
            m_SplineDragOffset = Vector3.zero;
            TrTransform snapped = SnapSplineKnot(cursor);
            if (SplineTracksTransitionHeading)
                snapped.rotation = SnapTransitionAngle(TransitionBrush.HorizontalOrientation(cursor.rotation), m_SplineCanvas);
            else if (SplineUsesHeadingOffsets)
            {
                snapped.rotation = Quaternion.identity;
                StartSplineRotation(cursor.rotation, snapped.rotation);
            }
            int before = index > 0 ? index - 1 : (m_SplineClosed ? m_SplineKnots.Count - 1 : 0);
            int after = index < m_SplineKnots.Count ? index : m_SplineKnots.Count - 1;
            snapped.scale = Mathf.Lerp(m_SplineKnots[before].scale, m_SplineKnots[after].scale, m_SplineInsertionT);
            AddSplineKnot(snapped, index);
            m_HoveredSplineKnot = index;
            SampleSpline();
            if (m_EditableSpline != null)
                UpdateSavedSplinePreview();
            InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.035f);
        }

        private void RemoveSplineKnot(int index)
        {
            m_SplineHandles[index].gameObject.SetActive(false);
            Destroy(m_SplineHandles[index].gameObject);
            m_SplineHandles.RemoveAt(index);
            m_SplineKnots.RemoveAt(index);
            m_HoveredSplineKnot = -1;
        }

        private int FindSplineInsertion(Vector3 position, float radius, out float segmentT)
        {
            segmentT = 0f;
            float closest = radius * radius;
            int index = -1;
            int stride = SplineCurve.SamplesPerSegment;
            if (m_SplineKnots.Count < 2 ||
                m_SplineSamples.Count != SplineCurve.GetSampleCount(m_SplineKnots.Count, m_SplineClosed))
                return index;

            int hullSegments = m_SplineClosed ? m_SplineKnots.Count : m_SplineKnots.Count - 1;
            for (int i = 0; i < hullSegments; i++)
            {
                float distance = DistanceToSplineSegmentSquared(position, m_SplineKnots[i].translation,
                    m_SplineKnots[(i + 1) % m_SplineKnots.Count].translation, out float t);
                if (distance < closest)
                {
                    closest = distance;
                    index = i + 1;
                    segmentT = t;
                }
            }

            for (int i = 1; i < m_SplineSamples.Count; i++)
            {
                Vector3 start = m_SplineSamples[i - 1].translation;
                float distance = DistanceToSplineSegmentSquared(position, start,
                    m_SplineSamples[i].translation, out float t);
                if (distance >= closest)
                    continue;
                int sampleIndex = i - 1;
                closest = distance;
                index = sampleIndex / stride + 1;
                segmentT = (sampleIndex % stride + t) / stride;
            }
            return index;
        }

        private static float DistanceToSplineSegmentSquared(Vector3 position,
            Vector3 start, Vector3 end, out float t)
        {
            Vector3 direction = end - start;
            float lengthSquared = direction.sqrMagnitude;
            t = lengthSquared > 0f
                ? Mathf.Clamp01(Vector3.Dot(position - start, direction) / lengthSquared)
                : 0f;
            return (position - (start + direction * t)).sqrMagnitude;
        }

        private void MoveSplineKnot(TrTransform cursor, Quaternion controllerRotation)
        {
            if (m_DraggedSplineKnot < 0 || m_brushTriggerDown)
                return;

            int index = m_DraggedSplineKnot;
            var knot = m_SplineKnots[index];
            if (IsDeckDepthAction)
            {
                float travel = Vector3.Dot(cursor.translation - m_DeckControllerAtGrab, m_DeckDragUp);
                float unitsPerInch = TransitionBrush.UnitsPerInch * (m_EditableSpline?.m_BrushScale ?? 1f);
                float depth = Mathf.Clamp(Mathf.Round((m_DeckDepthAtGrab + travel / unitsPerInch) * 2f) * 0.5f,
                    0f, TransitionBrush.MaxDeckDepthInches);
                float scale = (depth - DraftDeckOffset) / TransitionBrush.DefaultDeckDepthInches;
                if (Mathf.Approximately(knot.scale, scale))
                    return;
                knot.scale = scale;
                m_SplineKnots[index] = knot;
                SampleSpline();
                if (m_EditableSpline != null)
                    UpdateSavedSplinePreview();
                return;
            }

            bool placing = m_SplineTopologyIndex >= 0;
            bool stabilizeMove = !placing && (m_EditingTransitionSpline || SplineTracksTransitionHeading);
            float moveThreshold = 0.001f * App.METERS_TO_UNITS / m_SplineCanvas.Pose.scale;
            float moveThresholdSquared = moveThreshold * moveThreshold;
            if (stabilizeMove && !m_SplineMoveStarted &&
                (cursor.translation - m_SplineMoveAtGrab).sqrMagnitude > moveThresholdSquared)
                m_SplineMoveStarted = true;
            Vector3 moved = knot.translation;
            if (!stabilizeMove || m_SplineMoveStarted)
            {
                cursor = SnapSplineKnot(TrTransform.TR(cursor.translation + m_SplineDragOffset, cursor.rotation),
                    stabilizeMove ? knot.translation : null, moveThreshold);
                moved = cursor.translation;
                if (stabilizeMove && (moved - knot.translation).sqrMagnitude < moveThresholdSquared)
                    moved = knot.translation;
            }
            const float minimumDistanceSquared = 0.0000000001f;
            if (m_EditableSpline != null && IsSplineKnotTooClose(index, moved, minimumDistanceSquared))
                moved = m_SplineKnots[index].translation;

            var headingBefore = m_SplineHeadingEditData;
            Quaternion rotation = knot.rotation;
            if (!placing || SplineUsesHeadingOffsets)
            {
                if (!SplineAutoOrientEnabled || placing)
                    rotation = UpdateSplineGrabRotation(controllerRotation, knot.rotation);
            }
            else
            {
                if (!SplineTracksTransitionHeading)
                    rotation = cursor.rotation;
                else
                    rotation = SnapTransitionAngle(TransitionBrush.HorizontalOrientation(controllerRotation), m_SplineCanvas);
            }
            if (knot.translation == moved && knot.rotation == rotation && headingBefore == m_SplineHeadingEditData)
                return;

            knot.translation = moved;
            knot.rotation = rotation;
            m_SplineKnots[index] = knot;
            m_SplineHandles[index].localPosition = moved;
            SampleSpline();
            if (m_EditableSpline != null)
                UpdateSavedSplinePreview();
        }

        private float DraftDeckOffset => m_EditableSpline == null
            ? m_DraftSplineDeckDepth - m_DraftSplineStartingDeckDepth : 0f;

        private float DeckDepthFromKnotScale(float scale) => Mathf.Clamp(
            scale * TransitionBrush.DefaultDeckDepthInches + DraftDeckOffset, 0f, TransitionBrush.MaxDeckDepthInches);

        private float KnotDeckDepthInches(int index) => DeckDepthFromKnotScale(m_SplineKnots[index].scale);

        private float SplineDeckPressure(int sample) =>
            TransitionBrush.EncodeDeckDepth(DeckDepthFromKnotScale(
                SplineCurve.SampleDeckScale(m_SplineKnots, sample, m_SplineClosed)));

        private void StartSplineRotation(Quaternion controller, Quaternion knot)
        {
            m_SplineControllerAtGrab = controller;
            m_SplineKnotAtGrab = knot;
        }

        private Quaternion UpdateSplineGrabRotation(Quaternion controller, Quaternion previous)
        {
            if (!m_EditingTransitionSpline && !SplineTracksTransitionHeading)
            {
                if (Quaternion.Angle(m_SplineControllerAtGrab, controller) < 0.5f)
                    return m_SplineKnotAtGrab;
                Quaternion rotated = SplineCurve.RotateGrabbedKnot(m_SplineKnotAtGrab, m_SplineControllerAtGrab, controller);
                return SnapBrushAngle(rotated, m_SplineCanvas.Pose.rotation);
            }

            Quaternion desired = SplineCurve.RotateGrabbedKnotAroundUp(m_SplineKnotAtGrab,
                m_SplineControllerAtGrab, controller, previous);
            if (m_EditingTransitionSpline && !SplineTracksTransitionHeading && !SplineUsesHeadingOffsets)
            {
                if (Quaternion.Angle(desired, previous) < 0.2f)
                    return previous;
                EnableRotationForOlderTransition();
                previous = m_SplineKnotAtGrab;
                desired = SplineCurve.RotateGrabbedKnotAroundUp(m_SplineKnotAtGrab,
                    m_SplineControllerAtGrab, controller, previous);
            }

            var selection = SelectionManager.m_Instance;
            bool angleSnapping = selection != null && selection.CurrentSnapAngleIndex != 0 &&
                selection.m_EnableSnapRotationY;
            Quaternion quantized = desired;
            if (angleSnapping)
            {
                Quaternion basis = SplineUsesHeadingOffsets
                    ? GetSplineTransitionTangent(m_DraggedSplineKnot * SplineCurve.SamplesPerSegment) ?? Quaternion.identity
                    : Quaternion.identity;
                quantized = SnapTransitionAngle(desired * basis, m_SplineCanvas) * Quaternion.Inverse(basis);
            }
            return SplineCurve.StabilizeGrabbedHeading(previous, desired, quantized, angleSnapping);
        }

        private void EnableRotationForOlderTransition()
        {
            Quaternion? fixedOrientation = TransitionBrush.GetSavedOrientation(m_EditableSpline);
            m_SplineKnotAtGrab = fixedOrientation ?? Quaternion.identity;
            for (int i = 0; i < m_SplineKnots.Count; i++)
            {
                var knot = m_SplineKnots[i];
                knot.rotation = m_SplineKnotAtGrab;
                m_SplineKnots[i] = knot;
            }
            var flags = m_EditableSpline.m_Flags & ~SketchMemoryScript.StrokeFlags.TransitionFixedHeading;
            flags |= fixedOrientation.HasValue ? SketchMemoryScript.StrokeFlags.TransitionControllerHeading :
                SketchMemoryScript.StrokeFlags.TransitionHeadingOffsets;
            m_SplineHeadingEditData = new StrokeData(m_EditableSpline) { m_Flags = flags };
        }

        private TrTransform SnapSplineKnot(TrTransform knot, Vector3? previousPosition = null, float snapMargin = 0f)
        {
            TrTransform worldFromCanvas = m_SplineCanvas.Pose;
            TrTransform pose = worldFromCanvas * knot;
            var widgets = WidgetManager.m_Instance;
            bool onGuide = widgets.MagnetizeToStencils(ref pose.translation, ref pose.rotation);
            if (SelectionManager.m_Instance.CurrentSnapGridIndex != 0)
            {
                Vector3 unsnapped = pose.translation;
                pose.translation = SnapToGrid(pose.translation, allowAxisSlide: false);
                // Project onto the same guide so snapping a grid axis cannot lift a point off its surface.
                if (onGuide)
                    widgets.ProjectOntoActiveStencil(ref pose.translation, ref pose.rotation);
                else if (previousPosition is Vector3 previous)
                {
                    var canvasFromWorld = worldFromCanvas.inverse;
                    Vector3 raw = canvasFromWorld * unsnapped;
                    Vector3 snapped = canvasFromWorld * pose.translation;
                    var selection = SelectionManager.m_Instance;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        bool enabled = axis == 0 ? selection.m_EnableSnapTranslationX :
                            axis == 1 ? selection.m_EnableSnapTranslationY : selection.m_EnableSnapTranslationZ;
                        if (enabled &&
                            Mathf.Abs(raw[axis] - previous[axis]) <= Mathf.Abs(raw[axis] - snapped[axis]) + snapMargin)
                            snapped[axis] = previous[axis];
                    }
                    pose.translation = worldFromCanvas * snapped;
                }
            }
            pose.rotation = SnapBrushAngle(pose.rotation, Quaternion.identity);
            return worldFromCanvas.inverse * TrTransform.TR(pose.translation, pose.rotation);
        }

        private void ReleaseSplineKnot()
        {
            int released = m_DraggedSplineKnot;
            m_DraggedSplineKnot = -1;
            if (released < 0)
                return;
            float spacing = kSplineMinimumSpacing / m_SplineCanvas.Pose.scale;
            if (m_SplineTopologyIndex >= 0 &&
                IsSplineKnotTooClose(released, m_SplineKnots[released].translation, spacing * spacing))
            {
                RemoveSplineKnot(released);
                m_SplineTopologyIndex = -1;
                SampleSpline();
                if (m_EditableSpline != null)
                {
                    RestoreSavedSplineGeometry();
                    LoadSavedSplineKnots();
                    return;
                }
            }
            if (m_EditableSpline != null)
                ApplySavedSplineEdit();
            else
                RecordSplineDraftEdit();
            m_SplineTopologyIndex = -1;
        }

        private bool IsSplineKnotTooClose(int index, Vector3 position, float spacingSquared)
        {
            return (TryGetAdjacentSplineKnot(index, -1, out var before) &&
                    (before - position).sqrMagnitude < spacingSquared) ||
                (TryGetAdjacentSplineKnot(index, 1, out var after) &&
                 (after - position).sqrMagnitude < spacingSquared);
        }

        private bool TryGetAdjacentSplineKnot(int index, int offset, out Vector3 position)
        {
            int count = m_SplineKnots.Count;
            int neighbor = index + offset;
            if (m_SplineClosed)
                neighbor = (neighbor + count) % count;
            if (neighbor < 0 || neighbor >= count)
            {
                position = default;
                return false;
            }
            position = m_SplineKnots[neighbor].translation;
            return true;
        }

        private void RecordSplineDraftEdit()
        {
            var edit = m_SplineDraftBeforeEdit;
            m_SplineDraftBeforeEdit = null;
            m_SplineTopologyIndex = -1;
            if (!edit.HasValue)
                return;
            var before = edit.Value.Knots;
            if (edit.Value.Closed == m_SplineClosed && edit.Value.AutoOrient == m_DraftSplineAutoOrient &&
                before.Length == m_SplineKnots.Count)
            {
                int i = 0;
                while (i < before.Length && before[i].Equals(m_SplineKnots[i]))
                    i++;
                if (i == before.Length)
                    return;
            }
            m_SplineDraftUndo.Push(edit.Value);
            m_SplineDraftRedo.Clear();
        }

        internal void StepSplineDraft(bool redo)
        {
            if (redo ? !CanRedoSplineDraft : !CanUndoSplineDraft)
                return;
            var history = redo ? m_SplineDraftRedo : m_SplineDraftUndo;
            var opposite = redo ? m_SplineDraftUndo : m_SplineDraftRedo;
            opposite.Push((m_SplineKnots.ToArray(), m_SplineClosed, m_DraftSplineAutoOrient));
            ClearSplineHandles();
            var snapshot = history.Pop();
            m_SplineClosed = snapshot.Closed;
            m_DraftSplineAutoOrient = snapshot.AutoOrient;
            foreach (var knot in snapshot.Knots)
                AddSplineKnot(knot);
            SampleSpline();
            UpdateSplineBrushPreview();
            if ((m_SplineAction == SplineAction.SelectCurve && m_SplineKnots.Count > 0) ||
                !IsSplineActionAvailable(m_SplineAction))
                m_SplineAction = SplineAction.MoveAndRotate;
        }

        private void SampleSpline()
        {
            SplineCurve.Sample(m_SplineKnots, m_SplineSamples, m_SplineClosed);
            UpdateSplineHull();
            uint time = (uint)(App.Instance.CurrentSketchTime * 1000);
            m_SplineStrokePoints.Clear();
            foreach (var sample in m_SplineSamples)
            {
                m_SplineStrokePoints.Add(new PointerManager.ControlPoint
                {
                    m_Pos = sample.translation,
                    m_Orient = sample.rotation,
                    m_Pressure = 1,
                    m_TimestampMs = time
                });
            }
            m_SplinePreviewDirty = true;
        }

        private Quaternion SplineSampleOrientation(int sample) => SplineAutoOrientEnabled &&
            !TransitionBrush.IsTransition(SplineBrush)
                ? SplineCurve.GetAutoOrientation(m_SplineSamples, sample, m_SplineClosed)
                : m_SplineStrokePoints[sample].m_Orient;

        private void UpdateSplineBrushPreview()
        {
            if (m_EditableSpline != null)
                return;
            UpdateDraftSplineAppearance(PointerManager.m_Instance.MainPointer);
            if (m_SplineStrokePoints.Count < 2)
            {
                ClearSplineBrushPreview();
                return;
            }

            var descriptor = SplineBrush;
            var color = m_DraftSplineColor;
            float size = m_DraftSplineSize;
            var brush = m_DraftSplinePreviewBrush;
            bool isTransition = TransitionBrush.IsTransition(descriptor);
            bool usesSteel = TransitionBrush.IsStandaloneSteelCoping(descriptor) ||
                (isTransition && SplineSurfaceOptions.Coping == TransitionBrush.CopingKind.Steel);
            int steelDiameter = usesSteel ? TransitionBrush.SteelDiameterHalfInches : 0;
            int turns = isTransition ? DraftSplineQuarterTurns : 0;
            bool styleChanged = brush == null || brush.Descriptor != descriptor ||
                !brush.MatchesColor(color) || brush.BaseSize_PS != size ||
                (brush is TransitionBrush transition && (transition.QuarterTurns != turns ||
                    (usesSteel && transition.NewStrokeSteelDiameterHalfInches != steelDiameter)));
            if (!m_SplinePreviewDirty && !styleChanged)
                return;
            m_SplinePreviewDirty = false;
            if (descriptor == null || descriptor.m_BrushPrefab == null)
            {
                ClearSplineBrushPreview();
                return;
            }

            using var sample = kRebuildDraftSplinePreview.Auto();
            var first = m_SplineStrokePoints[0];
            var start = TrTransform.TR(first.m_Pos, SplineSampleOrientation(0));
            if (styleChanged || !brush.AlwaysRebuildPreviewBrush())
            {
                ClearSplineBrushPreview();
                brush = BaseBrushScript.Create(m_SplineCanvas.transform, start, descriptor, color, size);
                if (brush is TransitionBrush newTransition)
                {
                    newTransition.NewStrokeQuarterTurns = turns;
                    newTransition.TrackControllerForNewStroke = isTransition;
                }
                brush.gameObject.name = "Draft spline brush";
                brush.RandomSeed = 0;
                m_DraftSplinePreviewBrush = brush;
            }
            else
            {
                brush.ResetBrushForPreview(start);
            }
            if (brush is TransitionBrush previewTransition)
            {
                if (usesSteel)
                    previewTransition.NewStrokeSteelDiameterHalfInches = steelDiameter;
                if (isTransition)
                {
                    previewTransition.NewStrokeSurfaceOptions = SplineSurfaceOptions;
                    previewTransition.NewStrokeAutoOrient = SplineAutoOrientEnabled;
                    previewTransition.PreviewClosedSweep = m_SplineClosed;
                    previewTransition.PreviewStartDeckPressure = SplineDeckPressure(0);
                }
            }
            for (int i = 0; i < m_SplineStrokePoints.Count && !brush.IsOutOfVerts(); i++)
            {
                var point = m_SplineStrokePoints[i];
                brush.UpdatePosition_LS(TrTransform.TR(point.m_Pos, SplineSampleOrientation(i)),
                    isTransition ? SplineDeckPressure(i) : point.m_Pressure);
            }
            brush.ApplyChangesToVisuals();
        }

        private void UpdateDraftTransitionDimensions()
        {
            if (m_EditableSpline != null || !TransitionBrush.IsTransition(m_DraftSplineDescriptor))
                return;

            float radius = TransitionBrush.RadiusInches * TransitionBrush.UnitsPerInch;
            float deckDepth = TransitionBrush.DeckDepthInches;
            float floorHeight = App.Scene.FloorHeightCanvasUnits;
            if (Mathf.Approximately(m_DraftSplineSize, radius) && m_DraftSplineDeckDepth == deckDepth &&
                m_DraftSplineFloorHeight == floorHeight)
                return;

            m_DraftSplineSize = radius;
            m_DraftSplineDeckDepth = deckDepth;
            m_DraftSplineFloorHeight = floorHeight;
            // The draft must rebuild even while the controller is pointing at the settings panel.
            m_SplinePreviewDirty = true;
            UpdateSplineBrushPreview();
        }

        internal bool TryGetTransitionSettings(out TransitionSettings settings)
        {
            settings = default;
            if (m_SplineCanvas == null || m_SplineCanvas != App.ActiveCanvas || !UsesSplinePreview ||
                !App.Instance.IsInStateThatAllowsPainting())
                return false;

            var brush = SplineBrush;
            settings.IsStandaloneSteel = TransitionBrush.IsStandaloneSteelCoping(brush);
            if (!settings.IsStandaloneSteel && !TransitionBrush.IsTransition(brush))
                return false;
            settings.Options = SplineSurfaceOptions;
            settings.IsSelection = m_EditableSpline != null;
            settings.SteelDiameterHalfInches = settings.IsSelection
                ? TransitionBrush.SteelDiameterHalfInchesFor(m_EditableSpline) : TransitionBrush.SteelDiameterHalfInches;
            if (settings.IsStandaloneSteel)
                return true;
            if (!settings.IsSelection)
            {
                settings.RadiusHalfInches = TransitionBrush.RadiusHalfInches;
                settings.DeckDepthHalfInches = TransitionBrush.DeckDepthHalfInches;
                return true;
            }

            float scale = m_EditableSpline.m_BrushScale;
            settings.RadiusHalfInches = Mathf.RoundToInt(m_EditableSpline.SizeInLocalSpace * 2f /
                TransitionBrush.UnitsPerInch);
            float deckDepth = SelectedSplineDeckDepthInches;
            settings.DeckDepthHalfInches = Mathf.RoundToInt(deckDepth * scale * 2f);
            if (!TransitionBrush.HasVariableDeck(m_EditableSpline))
                return true;
            var samples = m_EditableSpline.m_ControlPoints;
            int stride = CanEditSelectedSplineKnots ? SplineCurve.SamplesPerSegment : 1;
            for (int i = stride; i < samples.Length; i += stride)
            {
                if (Mathf.Abs(SelectedTransitionDeckDepthAtSample(i) - deckDepth) > 0.0001f)
                {
                    settings.MixedDeckDepth = true;
                    break;
                }
            }
            return true;
        }

        internal void SetTransitionSettings(TransitionSettings settings)
        {
            if (!TryGetTransitionSettings(out var current) || settings.IsSelection != current.IsSelection ||
                settings.IsStandaloneSteel != current.IsStandaloneSteel)
                return;

            var options = settings.Options;
            int steelDiameter = TransitionBrush.ClampSteelDiameter(settings.SteelDiameterHalfInches);
            bool steelChanged = (current.IsStandaloneSteel || options.Coping == TransitionBrush.CopingKind.Steel) &&
                steelDiameter != current.SteelDiameterHalfInches;
            if (current.IsStandaloneSteel)
            {
                if (!steelChanged)
                    return;
                if (current.IsSelection)
                    ChangeSelectedStandaloneSteelDiameter(steelDiameter);
                else
                    SetDraftSteelDiameter(steelDiameter);
                return;
            }

            bool featuresChanged = options.Coping != current.Options.Coping || options.Tile != current.Options.Tile ||
                options.Deck != current.Options.Deck;
            int radiusHalfInches = Mathf.Clamp(settings.RadiusHalfInches,
                TransitionBrush.MinRadiusInches * 2, TransitionBrush.MaxRadiusInches * 2);
            bool radiusChanged = settings.RadiusHalfInches != current.RadiusHalfInches &&
                radiusHalfInches != current.RadiusHalfInches;
            if (!featuresChanged && !radiusChanged && !steelChanged)
                return;

            if (settings.IsSelection)
            {
                ChangeSelectedTransitionSettings(featuresChanged ? options : null,
                    radiusChanged ? radiusHalfInches : null, deckDepthSteps: 0,
                    steelDiameterHalfInches: steelChanged ? steelDiameter : null);
                return;
            }

            if (radiusChanged)
            {
                PointerManager.m_Instance.SetTransitionRadiusHalfInches(radiusHalfInches);
                PlayerPrefs.Save();
                UpdateDraftTransitionDimensions();
            }
            if (featuresChanged)
            {
                m_DraftSplineSurfaceOptions = options;
                if (!SplineHasDeck && IsDeckDepthAction)
                    m_SplineAction = SplineAction.MoveAndRotate;
                m_SplinePreviewDirty = true;
                UpdateSplineBrushPreview();
            }
            if (steelChanged)
                SetDraftSteelDiameter(steelDiameter);
        }

        private void SetDraftSteelDiameter(int halfInches)
        {
            PointerManager.m_Instance.SetSteelDiameterHalfInches(halfInches);
            PlayerPrefs.Save();
            UpdateSplineBrushPreview();
        }

        private void StepStandaloneSteelDiameter(int steps)
        {
            if (TryGetTransitionSettings(out var current) && current.IsStandaloneSteel && current.IsSelection)
            {
                int diameter = TransitionBrush.ClampSteelDiameter(current.SteelDiameterHalfInches + steps);
                if (diameter != current.SteelDiameterHalfInches)
                    ChangeSelectedStandaloneSteelDiameter(diameter);
            }
            else
            {
                PointerManager.m_Instance.SetSteelDiameterHalfInches(TransitionBrush.SteelDiameterHalfInches + steps);
            }
        }

        private void ChangeSelectedStandaloneSteelDiameter(int halfInches)
        {
            RefreshEditableSpline();
            if (m_EditableSpline == null || !TransitionBrush.IsStandaloneSteelCoping(m_SplineEditBrush))
                return;
            PauseSplineInput();
            var stroke = m_EditableSpline;
            var flags = TransitionBrush.FlagsWithSteelDiameter(stroke.m_Flags, halfInches);
            var points = (PointerManager.ControlPoint[])stroke.m_ControlPoints.Clone();
            float size = halfInches * 0.5f * TransitionBrush.UnitsPerInch;
            m_SplineMemory.PerformAndRecordCommand(
                new ModifyStrokePointsCommand(stroke, points, flags, size, stroke.m_SplineControlPoints));
            LoadSavedSplineKnots();
            EatInput();
        }

        internal void StepTransitionDeckDepth(int direction)
        {
            if (direction == 0 || !TryGetTransitionSettings(out var settings) || settings.IsStandaloneSteel)
                return;
            if (settings.IsSelection)
            {
                ChangeSelectedTransitionSettings(null, null, direction);
                return;
            }

            int previous = TransitionBrush.DeckDepthHalfInches;
            TransitionBrush.DeckDepthHalfInches = previous + direction;
            if (TransitionBrush.DeckDepthHalfInches != previous)
            {
                PlayerPrefs.Save();
                UpdateDraftTransitionDimensions();
            }
        }

        private float SelectedSplineDeckDepthInches => SelectedTransitionDeckDepthAtSample(0);

        private float SelectedTransitionDeckDepthAtSample(int index)
        {
            if (TransitionBrush.HasVariableDeck(m_EditableSpline))
                return TransitionBrush.DecodeDeckDepth(m_EditableSpline.m_ControlPoints[index].m_Pressure);

            var preset = TransitionBrush.StyleFor(m_SplineEditBrush);
            return TransitionBrush.HasDeck(preset) ? TransitionBrush.LegacyDeckDepth(preset) :
                TransitionBrush.DefaultDeckDepthInches;
        }

        private void ChangeSelectedTransitionSettings(TransitionBrush.SurfaceOptions? options,
            int? radiusHalfInches, int deckDepthSteps, int? steelDiameterHalfInches = null)
        {
            RefreshEditableSpline();
            if (!m_EditingTransitionSpline || m_EditableSpline == null)
                return;

            PauseSplineInput();
            var stroke = m_EditableSpline;
            var flags = stroke.m_Flags;
            bool preserveDepth = !TransitionBrush.HasVariableDeck(stroke);
            if (options.HasValue)
                flags = TransitionBrush.FlagsWithSurfaceOptions(flags, options.Value);
            if (steelDiameterHalfInches.HasValue)
                flags = TransitionBrush.FlagsWithSteelDiameter(flags, steelDiameterHalfInches.Value);
            var points = (PointerManager.ControlPoint[])stroke.m_ControlPoints.Clone();
            var authoredPoints = stroke.m_SplineControlPoints;
            bool depthChanged = false;
            if (deckDepthSteps != 0 || preserveDepth)
            {
                float inches = deckDepthSteps * 0.5f / stroke.m_BrushScale;
                if (CanEditSelectedSplineKnots)
                {
                    var knots = m_SplineKnots.ToArray();
                    for (int i = 0; i < knots.Length; i++)
                    {
                        float before = SelectedTransitionDeckDepthAtSample(i * SplineCurve.SamplesPerSegment);
                        float after = Mathf.Clamp(before + inches, 0f, TransitionBrush.MaxDeckDepthInches);
                        depthChanged |= !Mathf.Approximately(before, after);
                        knots[i].scale = after / TransitionBrush.DefaultDeckDepthInches;
                    }
                    for (int i = 0; i < points.Length; i++)
                    {
                        float depth = SplineCurve.SampleDeckScale(knots, i, m_SplineClosed) *
                            TransitionBrush.DefaultDeckDepthInches;
                        points[i].m_Pressure = TransitionBrush.EncodeDeckDepth(depth);
                    }
                    if (authoredPoints != null)
                        authoredPoints = knots;
                }
                else
                {
                    for (int i = 0; i < points.Length; i++)
                    {
                        float before = SelectedTransitionDeckDepthAtSample(i);
                        float after = Mathf.Clamp(before + inches, 0f, TransitionBrush.MaxDeckDepthInches);
                        depthChanged |= !Mathf.Approximately(before, after);
                        points[i].m_Pressure = TransitionBrush.EncodeDeckDepth(after);
                    }
                }
            }

            float size = radiusHalfInches.HasValue
                ? radiusHalfInches.Value * 0.5f * TransitionBrush.UnitsPerInch / stroke.m_BrushScale
                : stroke.m_BrushSize;
            if (!depthChanged && Mathf.Approximately(size, stroke.m_BrushSize) && flags == stroke.m_Flags)
                return;
            flags |= SketchMemoryScript.StrokeFlags.TransitionVariableDeck;
            m_SplineMemory.PerformAndRecordCommand(
                new ModifyStrokePointsCommand(stroke, points, flags, size, authoredPoints));
            LoadSavedSplineKnots();
            if (!IsSplineActionAvailable(m_SplineAction))
                m_SplineAction = SplineAction.MoveAndRotate;
            EatInput();
        }

        private void ClearSplineBrushPreview()
        {
            if (m_DraftSplinePreviewBrush == null)
                return;
            m_DraftSplinePreviewBrush.gameObject.SetActive(false);
            m_DraftSplinePreviewBrush.DestroyMesh();
            Destroy(m_DraftSplinePreviewBrush.gameObject);
            m_DraftSplinePreviewBrush = null;
        }

        private Stroke FindSavedSpline(Vector3 position, float pickRadius)
        {
            Stroke nearest = null;
            float nearestDistance = float.PositiveInfinity;
            foreach (var stroke in m_SplineMemory.GetMemoryList)
            {
                if (!CanSelectSplineStroke(stroke))
                    continue;

                var descriptor = BrushCatalog.m_Instance.GetBrush(stroke.m_BrushGuid);
                float radius = SplinePickRadius(stroke, descriptor, pickRadius);
                var points = stroke.m_ControlPoints;
                for (int i = 1; i < points.Length; i++)
                {
                    float distance = DistanceToSplineSegmentSquared(position,
                        points[i - 1].m_Pos, points[i].m_Pos, out _);
                    if (distance < radius * radius && distance < nearestDistance)
                    {
                        nearest = stroke;
                        nearestDistance = distance;
                    }
                }
            }
            return nearest;
        }

        private void UpdateSplineSelection()
        {
            if (SketchControlsScript.m_Instance.IsBrushPointingAtPanel())
            {
                PauseSplineInput();
                return;
            }

            Transform controller = InputManager.m_Instance.GetBrushControllerAttachPoint();
            var ray = new Ray(controller.position, controller.forward);
            float depth = kSplineSelectReach;
            Stroke hit = FindSavedSpline(ray, ref depth);
            UpdateSplineSelectionRay(ray, depth, hit != null);
            if (!m_brushTriggerDown)
                return;

            if (hit == null)
            {
                ShowSplineHint("Aim the laser at a saved curve");
                return;
            }
            if (m_EditableSpline == null && m_SplineKnots.Count > 0)
            {
                ShowSplineHint("Finish the current curve before selecting another");
                m_SplineAction = SplineAction.MoveAndRotate;
                m_SplineSelectRay.enabled = false;
                return;
            }

            m_SplineSelectRay.enabled = false;
            OpenSavedSpline(hit);
        }

        private Stroke FindSavedSpline(Ray ray, ref float depth)
        {
            Stroke hit = null;
            m_SplineStrokePicker.BeginQuery();
            foreach (var stroke in m_SplineMemory.GetMemoryList)
            {
                if (!CanSelectSplineStroke(stroke))
                    continue;
                var preview = stroke == m_EditableSpline && m_SavedSplinePreviewBrush != null
                    ? m_SavedSplinePreviewBrush.gameObject : null;
                if (m_SplineStrokePicker.TryHitStroke(stroke, preview, ray, ref depth))
                    hit = stroke;
            }
            return hit;
        }

        private static float SplinePickRadius(Stroke stroke, BrushDescriptor brush, float tolerance)
        {
            if (TransitionBrush.IsStandaloneSteelCoping(brush))
                return tolerance + TransitionBrush.SteelDiameterInchesFor(stroke) * 0.5f *
                    TransitionBrush.UnitsPerInch * stroke.m_BrushScale;
            // The transition's main size is the surface radius, far wider than its coping.
            return TransitionBrush.IsTransition(brush) ? tolerance : tolerance + stroke.SizeInLocalSpace * 0.5f;
        }

        private void UpdateSplineSelectionRay(Ray ray, float depth, bool hit)
        {
            if (m_SplineSelectRay == null)
            {
                if (m_SplineSelectRayMaterial == null)
                {
                    m_SplineSelectRayMaterial = new Material(
                        Resources.Load<Shader>("Brushes/Basic/Transition/TransitionPreviewLine"));
                    m_SplineSelectRayMaterial.SetFloat("_Dashed", 0f);
                }
                var beam = new GameObject("Select spline ray") { layer = gameObject.layer };
                beam.transform.SetParent(m_SplinePreview.transform, false);
                m_SplineSelectRay = beam.AddComponent<LineRenderer>();
                m_SplineSelectRay.useWorldSpace = true;
                m_SplineSelectRay.positionCount = 2;
                m_SplineSelectRay.widthMultiplier = kSplineSelectRayWidth;
                m_SplineSelectRay.numCapVertices = 4;
                m_SplineSelectRay.sharedMaterial = m_SplineSelectRayMaterial;
                m_SplineSelectRay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_SplineSelectRay.receiveShadows = false;
                m_SplineSelectRay.allowOcclusionWhenDynamic = false;
            }
            m_SplineSelectRayMaterial.color = hit ? kSplineSelectHitColor : kSplineSelectRayColor;
            m_SplineSelectRay.SetPosition(0, ray.origin);
            m_SplineSelectRay.SetPosition(1, ray.GetPoint(depth));
            m_SplineSelectRay.enabled = true;
        }

        private bool CanSelectSplineStroke(Stroke stroke)
        {
            bool previewVisible = stroke == m_EditableSpline && m_SavedSplinePreviewBrush != null &&
                m_SavedSplinePreviewBrush.gameObject.activeInHierarchy;
            if ((!stroke.IsGeometryEnabled && !previewVisible) || stroke.Canvas != m_SplineCanvas)
                return false;
            var brush = BrushCatalog.m_Instance.GetBrush(stroke.m_BrushGuid);
            return !ModularCopingBrush.IsModular(brush) && (SplineCurve.IsEditable(stroke) ||
                (stroke.m_ControlPoints?.Length >= 2 && (TransitionBrush.IsTransition(brush) ||
                    TransitionBrush.IsStandaloneSteelCoping(brush))));
        }

        private void OpenSavedSpline(Stroke stroke)
        {
            ClearSplineBrushPreview();
            m_DraftSplineDescriptor = null;
            m_EditableSpline = stroke;
            m_SplineAction = SplineAction.MoveAndRotate;
            LoadSavedSplineKnots();
            if (m_EditingTransitionSpline || TransitionBrush.IsStandaloneSteelCoping(m_SplineEditBrush))
                m_TransitionSettingsOpenedForPlacement = false;
            if (BrushController.m_Instance.SetActiveBrushFromSpline(m_SplineEditBrush))
                m_DraftSplineSurfaceOptions = null;
            InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.035f);
        }

        private void RefreshEditableSpline()
        {
            if (m_EditableSpline == null)
                return;

            if (!CanSelectSplineStroke(m_EditableSpline))
            {
                m_EditableSpline = null;
                m_SplineAction = SplineAction.MoveAndRotate;
                LoadSavedSplineKnots();
            }
            else if (m_LastAppliedSplinePoints != m_EditableSpline.m_ControlPoints ||
                     m_LastAppliedSplineControls != m_EditableSpline.m_SplineControlPoints ||
                     m_SplineEditBrush == null || m_SplineEditBrush.m_Guid != m_EditableSpline.m_BrushGuid)
            {
                // Undo, redo and other stroke tools can replace the control points.
                LoadSavedSplineKnots();
                if (!IsSplineActionAvailable(m_SplineAction))
                    m_SplineAction = SplineAction.MoveAndRotate;
            }
        }

        private void LoadSavedSplineKnots()
        {
            m_DraggedSplineKnot = -1;
            m_SplineTopologyIndex = -1;
            m_SavedSplinePreviewBrush = null;
            m_SplineHeadingEditData = null;
            m_SplineEditBrush = m_EditableSpline == null ? null :
                BrushCatalog.m_Instance.GetBrush(m_EditableSpline.m_BrushGuid);
            m_EditingTransitionSpline = TransitionBrush.IsTransition(m_SplineEditBrush);
            m_SplineDraftBeforeEdit = null;
            m_SplineDraftUndo.Clear();
            m_SplineDraftRedo.Clear();
            ClearSplineHandles();
            m_SplineSamples.Clear();
            m_SplineStrokePoints.Clear();

            m_LastAppliedSplinePoints = m_EditableSpline?.m_ControlPoints;
            m_LastAppliedSplineControls = m_EditableSpline?.m_SplineControlPoints;
            m_SplineClosed = m_EditableSpline != null && CanEditSelectedSplineKnots && SplineCurve.IsClosed(m_EditableSpline);
            if (m_EditableSpline != null && CanEditSelectedSplineKnots)
            {
                for (int i = 0; i < m_EditableSpline.m_SplineKnotCount; i++)
                {
                    var knot = SplineCurve.GetAuthoredPoint(m_EditableSpline, i);
                    if (m_EditingTransitionSpline)
                        knot.scale = SelectedTransitionDeckDepthAtSample(i * SplineCurve.SamplesPerSegment) /
                            TransitionBrush.DefaultDeckDepthInches;
                    else
                        knot.scale = 1f;
                    AddSplineKnot(knot);
                }

                foreach (var sample in m_LastAppliedSplinePoints)
                    m_SplineSamples.Add(TrTransform.TR(sample.m_Pos, sample.m_Orient));
                m_SplineStrokePoints.AddRange(m_LastAppliedSplinePoints);
            }
            UpdateSplineHull();
            m_SplinePreviewDirty = false;
        }

        private void ClearSplineHandles()
        {
            m_HoveredSplineKnot = -1;
            foreach (var handle in m_SplineHandles)
            {
                handle.gameObject.SetActive(false);
                Destroy(handle.gameObject);
            }
            m_SplineHandles.Clear();
            m_SplineKnots.Clear();
        }

        private void UpdateSavedSplinePreview()
        {
            if (!HasValidSplineSamples)
                return;

            var stroke = m_EditableSpline;
            var first = m_SplineStrokePoints[0];
            var start = TrTransform.TRS(first.m_Pos, SplineSampleOrientation(0), stroke.m_BrushScale);
            var brush = m_SavedSplinePreviewBrush;
            bool replace = brush == null || !brush.AlwaysRebuildPreviewBrush();
            if (replace)
            {
                var descriptor = BrushCatalog.m_Instance.GetBrush(stroke.m_BrushGuid);
                if (descriptor == null || descriptor.m_BrushPrefab == null)
                    return;
                brush = BaseBrushScript.Create(m_SplineCanvas.transform, start, descriptor,
                    stroke.m_Color, stroke.m_BrushSize);
                brush.SetIsLoading();
                brush.RandomSeed = stroke.m_Seed;
            }
            else
            {
                brush.ResetBrushForPreview(start);
            }
            brush.SetStrokeData(SplineEditData);
            if (m_EditingTransitionSpline && brush is TransitionBrush transition)
            {
                transition.PreviewStartDeckPressure = SplineDeckPressure(0);
                transition.PreviewClosedSweep = m_SplineClosed;
            }

            for (int i = 0; i < m_SplineStrokePoints.Count && !brush.IsOutOfVerts(); i++)
            {
                var point = m_SplineStrokePoints[i];
                int sourceIndex = GetSavedSplineSampleIndex(i);
                brush.UpdatePosition_LS(TrTransform.TRS(point.m_Pos, SplineSampleOrientation(i), stroke.m_BrushScale),
                    m_EditingTransitionSpline ? SplineDeckPressure(i) : m_LastAppliedSplinePoints[sourceIndex].m_Pressure,
                    stroke.GetColor(sourceIndex));
            }
            brush.ApplyChangesToVisuals();

            if (replace)
            {
                // Use solitary geometry during the drag. The committed stroke data stays usable
                // for saving and undo, and only the trigger release re-batches the stroke.
                ReleaseSavedSplinePreview();
                if (stroke.m_Object != null)
                    stroke.m_Object.SetActive(false);
                stroke.Uncreate();
                stroke.m_Type = Stroke.Type.BrushStroke;
                stroke.m_IntendedCanvas = null;
                stroke.m_Object = brush.gameObject;
                brush.Stroke = stroke;
                m_SavedSplinePreviewBrush = brush;
            }
        }

        private void ReleaseSavedSplinePreview()
        {
            if (m_SavedSplinePreviewBrush != null)
            {
                m_SavedSplinePreviewBrush.DestroyMesh();
                m_SavedSplinePreviewBrush = null;
            }
        }

        private void RestoreSavedSplineGeometry()
        {
            if (m_SavedSplinePreviewBrush == null)
                return;
            m_SavedSplinePreviewBrush.gameObject.SetActive(false);
            ReleaseSavedSplinePreview();
            m_EditableSpline.Uncreate();
            m_EditableSpline.Recreate();
        }

        private bool HasValidSplineSamples => m_SplineKnots.Count >= 2 &&
            m_SplineStrokePoints.Count == SplineCurve.GetSampleCount(m_SplineKnots.Count, m_SplineClosed);

        private int GetSavedSplineSampleIndex(int index) =>
            SplineCurve.GetSourceSampleIndex(index, m_EditableSpline.m_SplineKnotCount,
                m_SplineKnots.Count, m_SplineTopologyIndex, m_SplineInsertionT, SplineCurve.IsClosed(m_EditableSpline));

        private void ApplySavedSplineEdit()
        {
            if (!m_SplinePreviewDirty || m_LastAppliedSplinePoints == null)
                return;
            if (!HasValidSplineSamples)
            {
                RestoreSavedSplineGeometry();
                LoadSavedSplineKnots();
                return;
            }
            var points = new PointerManager.ControlPoint[m_SplineStrokePoints.Count];
            Quaternion? transitionOrientation = TransitionBrush.GetSavedOrientation(SplineEditData);
            var originalColors = m_EditableSpline.m_OverrideColors;
            bool resize = points.Length != m_LastAppliedSplinePoints.Length;
            var colors = resize && originalColors != null ? new List<Color32?>(points.Length) : originalColors;
            for (int i = 0; i < points.Length; i++)
            {
                int sourceIndex = GetSavedSplineSampleIndex(i);
                points[i] = m_LastAppliedSplinePoints[sourceIndex];
                points[i].m_Pos = m_SplineStrokePoints[i].m_Pos;
                points[i].m_Orient = transitionOrientation ?? SplineSampleOrientation(i);
                if (m_EditingTransitionSpline)
                    points[i].m_Pressure = SplineDeckPressure(i);
                if (resize && colors != null)
                    colors.Add(sourceIndex < originalColors.Count ? originalColors[sourceIndex] : null);
            }
            ReleaseSavedSplinePreview();
            if (m_EditableSpline.m_Object != null)
                m_EditableSpline.m_Object.SetActive(false);
            var flags = SplineEditData.m_Flags;
            if (m_EditingTransitionSpline)
                flags |= SketchMemoryScript.StrokeFlags.TransitionVariableDeck;
            var command = new ModifyStrokePointsCommand(m_EditableSpline, points, m_SplineKnots.ToArray(), colors, flags);
            m_SplineMemory.PerformAndRecordCommand(command);
            m_SplineHeadingEditData = null;
            m_LastAppliedSplinePoints = points;
            m_LastAppliedSplineControls = m_EditableSpline.m_SplineControlPoints;
            m_SplinePreviewDirty = false;
        }

        private bool CommitSpline()
        {
            if (!HasValidSplineSamples)
                return false;

            // Stamp the completed curve at commit time so sketch playback stays ordered.
            uint time = (uint)(App.Instance.CurrentSketchTime * 1000);
            var points = m_SplineStrokePoints.ToArray();
            for (int i = 0; i < points.Length; i++)
            {
                points[i].m_TimestampMs = time;
                points[i].m_Orient = SplineSampleOrientation(i);
            }

            UpdateDraftSplineAppearance(PointerManager.m_Instance.MainPointer);
            var descriptor = SplineBrush;
            var flags = SketchMemoryScript.StrokeFlags.None;
            var controls = m_SplineKnots.ToArray();
            if (TransitionBrush.IsTransition(descriptor))
            {
                flags = TransitionBrush.FlagsForQuarterTurns(DraftSplineQuarterTurns) |
                    SketchMemoryScript.StrokeFlags.TransitionControllerHeading |
                    SketchMemoryScript.StrokeFlags.TransitionVariableDeck;
                if (m_DraftSplineSurfaceOptions is TransitionBrush.SurfaceOptions options)
                    flags = TransitionBrush.FlagsWithSurfaceOptions(flags, options);
                for (int i = 0; i < points.Length; i++)
                    points[i].m_Pressure = SplineDeckPressure(i);
                for (int i = 0; i < controls.Length; i++)
                    controls[i].scale = KnotDeckDepthInches(i) / TransitionBrush.DefaultDeckDepthInches;
            }
            if (SplineAutoOrientEnabled)
                flags |= SketchMemoryScript.StrokeFlags.SplineAutoOrient;
            if (TransitionBrush.IsStandaloneSteelCoping(descriptor) ||
                (TransitionBrush.IsTransition(descriptor) && SplineSurfaceOptions.Coping == TransitionBrush.CopingKind.Steel))
                flags = TransitionBrush.FlagsWithSteelDiameter(flags, TransitionBrush.SteelDiameterHalfInches);
            var stroke = new Stroke
            {
                m_Type = Stroke.Type.NotCreated,
                m_IntendedCanvas = m_SplineCanvas,
                m_BrushGuid = descriptor.m_Guid,
                m_BrushSize = m_DraftSplineSize,
                m_Flags = flags,
                m_BrushScale = 1,
                m_Color = m_DraftSplineColor,
                m_ControlPoints = points,
                m_SplineKnotCount = m_SplineKnots.Count,
                m_SplineControlPoints = controls,
                m_ControlPointsToDrop = new bool[points.Length]
            };
            if (!SplineCurve.IsEditable(stroke))
                return false;
            stroke.Recreate();
            if (stroke.m_Type == Stroke.Type.NotCreated)
                return false;

            m_SplineMemory.MemoryListAdd(stroke);
            m_SplineMemory.PerformAndRecordCommand(new BrushStrokeCommand(stroke));
            return true;
        }

        private void UpdateDraftSplineAppearance(PointerScript pointer)
        {
            // A palette selection is for the next spline once this one has a point.
            if (m_DraftSplineDescriptor == null || pointer.CurrentBrush != m_DraftSplineDescriptor)
                return;
            if (TransitionBrush.IsStandaloneSteelCoping(m_DraftSplineDescriptor))
                m_DraftSplineSize = TransitionBrush.SteelDiameterInches * TransitionBrush.UnitsPerInch;
            else if (!TransitionBrush.IsTransition(m_DraftSplineDescriptor))
                m_DraftSplineSize = pointer.BrushSizeAbsolute;
            m_DraftSplineColor = App.BrushColor.CurrentColor;
            m_DraftSplineQuarterTurns = TransitionBrush.PreferredQuarterTurns;
        }

        private void SetSplinePreviewVisible(bool visible)
        {
            if (visible && m_SplinePreview == null)
            {
                m_SplinePreview = new GameObject("Spline preview");
                m_SplinePreview.transform.SetParent(m_SplineCanvas.transform, false);
                if (m_SplineMaterial == null)
                {
                    m_SplineMaterial = new Material(m_BimanualGuideLineRenderer.sharedMaterial);
                    m_SplineMaterial.SetColor("_Color", new Color(0.35f, 0.85f, 1f));
                    m_SplineMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                    m_SplineMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
                    m_SplineSelectedMaterial = new Material(m_SplineMaterial);
                    m_SplineSelectedMaterial.SetColor("_Color", new Color(1f, 0.78f, 0.25f));
                    m_SplineHullMaterial = new Material(m_SplineMaterial);
                    m_SplineHullMaterial.SetColor("_Color", new Color(0.06f, 0.42f, 0.54f));
                    m_SplineHullMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    m_SplineHullMaterial.SetFloat("_ZWrite", 0);
                    m_SplineHullMaterial.renderQueue = m_SplineMaterial.renderQueue - 1;
                }
                m_SplineHull = m_SplinePreview.AddComponent<LineRenderer>();
                m_SplineHull.sharedMaterial = m_SplineHullMaterial;
                m_SplineHull.useWorldSpace = false;
                m_SplineHull.positionCount = 0;
                m_SplineHull.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_SplineHull.receiveShadows = false;
                UpdateSplineHull();
            }
            if (m_SplinePreview != null)
            {
                m_SplinePreview.SetActive(visible);
                if (visible)
                    m_SplineHull.widthMultiplier = kSplineHullWidth / m_SplineCanvas.Pose.scale;
            }
            if (!visible)
            {
                PauseSplineInput();
                ClearSplineBrushPreview();
            }
        }

        private void UpdateSplineHull()
        {
            if (m_SplineHull == null)
                return;

            int count = m_SplineKnots.Count;
            m_SplineHull.loop = m_SplineClosed;
            m_SplineHull.positionCount = count >= 2 ? count : 0;
            for (int i = 0; i < m_SplineHull.positionCount; i++)
                m_SplineHull.SetPosition(i, m_SplineKnots[i].translation);
        }

        private void PauseSplineInput()
        {
            ReleaseSplineKnot();
            m_HoveredSplineKnot = -1;
            m_SplineStrokePicker.BeginQuery();
            if (m_SplineSelectRay != null)
                m_SplineSelectRay.enabled = false;
        }

        private void ClearSplineDraft()
        {
            ClearSplineBrushPreview();
            m_DraftSplineDescriptor = null;
            m_DraftSplineSurfaceOptions = null;
            m_DraftSplineAutoOrient = false;
            RestoreSavedSplineGeometry();
            m_EditableSpline = null;
            m_SplineEditBrush = null;
            m_SplineHeadingEditData = null;
            m_EditingTransitionSpline = false;
            m_LastAppliedSplinePoints = null;
            m_LastAppliedSplineControls = null;
            if (m_SplineMemory != null)
                m_SplineMemory.SketchClearing -= OnSplineSketchClearing;
            m_SplineMemory = null;
            m_SplineAction = SplineAction.MoveAndRotate;
            m_SplineClosed = false;
            m_SplineCanvas = null;
            m_SplinePreviewDirty = false;
            m_DraggedSplineKnot = -1;
            m_HoveredSplineKnot = -1;
            m_SplineDraftBeforeEdit = null;
            m_SplineTopologyIndex = -1;
            m_SplineDraftUndo.Clear();
            m_SplineDraftRedo.Clear();
            m_SplineKnots.Clear();
            m_SplineSamples.Clear();
            m_SplineStrokePoints.Clear();
            m_SplineHandles.Clear();
            if (m_SplinePreview != null)
            {
                m_SplinePreview.SetActive(false);
                Destroy(m_SplinePreview);
                m_SplinePreview = null;
                m_SplineHull = null;
                m_SplineSelectRay = null;
            }
        }

        private void OnSplineSketchClearing()
        {
            // Clearing the sketch removes the preview stroke along with the other geometry.
            CancelModularCopingPlacement();
            ReleaseSavedSplinePreview();
            ClearSplineDraft();
        }

        private bool UpdateSplinePanelShortcut()
        {
            if (m_DraggedSplineKnot >= 0 || m_brushTrigger ||
                !m_brushUndoButtonDown)
                return false;

            if (!PanelManager.m_Instance.TryFocusWandPanel(BasePanel.PanelType.Spline))
                return false;

            PauseSplineInput();
            return true;
        }

        private void UpdateTransitionSettingsPanel(bool canOpen)
        {
            if (!TransitionBrush.IsTransition(SplineBrush) && !TransitionBrush.IsStandaloneSteelCoping(SplineBrush))
            {
                HideTransitionSettingsPanel();
                return;
            }

            if (!canOpen || m_TransitionSettingsOpenedForPlacement)
                return;

            var panels = PanelManager.m_Instance;
            if (!panels.StandardActive() || !panels.GazePanelsAreVisible())
                return;

            const BasePanel.PanelType panelType = BasePanel.PanelType.TransitionSettings;
            if (!panels.IsPanelOpen(panelType))
            {
                var panel = panels.GetPanelByType(panelType);
                if (panel == null || !panels.IsPanelAvailable(panel) || panel.WidgetSibling == null)
                    return;

                if (m_TransitionSettingsPlacement is TrTransform placement)
                    SketchControlsScript.m_Instance.OpenPanelOfType(panelType, placement);
                else
                    OpenSplinePanel(panelType, -1.85f, distance: 4.6f, heightOffset: -0.55f);
            }

            m_TransitionSettingsOpenedForPlacement = panels.IsPanelOpen(panelType);
        }

        private void HideTransitionSettingsPanel()
        {
            if (!m_TransitionSettingsOpenedForPlacement)
                return;
            m_TransitionSettingsOpenedForPlacement = false;
            const BasePanel.PanelType panelType = BasePanel.PanelType.TransitionSettings;
            var panels = PanelManager.m_Instance;
            if (panels.IsPanelOpen(panelType) && panels.GetPanelByType(panelType) is BasePanel panel)
            {
                var transform = panel.transform;
                m_TransitionSettingsPlacement = TrTransform.TR(transform.position, transform.rotation);
                panels.HidePanel(panelType);
            }
        }

        private static void OpenSplinePanel(BasePanel.PanelType panelType, float sideOffset,
            float distance = 6.5f, float heightOffset = 0f)
        {
            var head = ViewpointScript.Head;
            Vector3 position = head.position + head.forward * distance + head.right * sideOffset + head.up * heightOffset;
            Quaternion rotation = Quaternion.LookRotation(position - head.position, Vector3.up);
            SketchControlsScript.m_Instance.OpenPanelOfType(panelType, TrTransform.TR(position, rotation));
        }

        internal bool SplineLoopIsClosed => m_SplineClosed;

        internal bool IsSplineActionAvailable(SplineAction action)
        {
            if (!UsesSplinePreview || UsesModularCoping || !App.Instance.IsInStateThatAllowsPainting() ||
                m_SplineCanvas != App.ActiveCanvas || MultiplayerManager.m_Instance.IsViewOnly)
                return false;

            return action switch
            {
                SplineAction.MoveAndRotate or SplineAction.Draw => true,
                SplineAction.Append => m_SplineKnots.Count > 0 && !m_SplineClosed,
                SplineAction.Insert => m_SplineKnots.Count >= 2,
                SplineAction.Delete => m_SplineKnots.Count > MinimumSplineKnots,
                SplineAction.DeckDepth => m_SplineKnots.Count > 0 && SplineHasDeck,
                SplineAction.ToggleLoop => m_SplineClosed || m_SplineKnots.Count >= 3,
                SplineAction.NewSpline => true,
                SplineAction.ToggleAutoOrient => CanEditSelectedSplineKnots,
                SplineAction.SelectCurve => true,
                _ => false,
            };
        }

        internal bool IsSplineActionSelected(SplineAction action) => action switch
        {
            SplineAction.ToggleLoop or SplineAction.NewSpline or SplineAction.ToggleAutoOrient => false,
            _ => action == m_SplineAction,
        };

        internal void ChooseSplineAction(SplineAction action)
        {
            if (!IsSplineActionAvailable(action))
                return;
            if (action == SplineAction.Draw && (m_EditableSpline != null || m_SplineClosed))
            {
                TryFinishSplineAndStartAnother(keepTransitionSettings: true, nextAction: SplineAction.Draw);
                return;
            }
            if (action is SplineAction.NewSpline or SplineAction.SelectCurve)
            {
                TryFinishSplineAndStartAnother(keepTransitionSettings: true,
                    nextAction: action == SplineAction.NewSpline ? SplineAction.Draw : SplineAction.SelectCurve,
                    completeDraftOnly: action == SplineAction.SelectCurve);
                return;
            }

            RefreshEditableSpline();
            PauseSplineInput();
            EatInput();
            if (action == SplineAction.ToggleLoop)
            {
                if (ToggleSplineLoop())
                    m_SplineAction = SplineAction.MoveAndRotate;
            }
            else if (action == SplineAction.ToggleAutoOrient)
            {
                ToggleSplineAutoOrient();
            }
            else
            {
                m_SplineAction = action;
            }
        }

        private void ToggleSplineAutoOrient()
        {
            if (m_EditableSpline == null)
            {
                BeginSplineKnotEdit();
                m_DraftSplineAutoOrient = !m_DraftSplineAutoOrient;
                SampleSpline();
                RecordSplineDraftEdit();
                return;
            }

            var flags = SplineEditData.m_Flags ^ SketchMemoryScript.StrokeFlags.SplineAutoOrient;
            m_SplineHeadingEditData = new StrokeData(m_EditableSpline) { m_Flags = flags };
            SampleSpline();
            if (m_SplineStrokePoints.Count == m_LastAppliedSplinePoints.Length)
            {
                // A property change should keep the existing path, including older Catmull-Rom curves.
                for (int i = 0; i < m_SplineStrokePoints.Count; i++)
                {
                    var baked = m_LastAppliedSplinePoints[i];
                    var point = m_SplineStrokePoints[i];
                    var sample = m_SplineSamples[i];
                    point.m_Pos = baked.m_Pos;
                    sample.translation = baked.m_Pos;
                    if (m_EditingTransitionSpline)
                        point.m_Orient = baked.m_Orient;
                    m_SplineStrokePoints[i] = point;
                    m_SplineSamples[i] = sample;
                }
            }
            ApplySavedSplineEdit();
        }

        private bool ToggleSplineLoop()
        {
            if (!m_SplineClosed)
            {
                if (m_SplineKnots.Count < 3)
                {
                    ShowSplineHint("Place at least three points to close the loop");
                    return false;
                }
                const float minimumDistanceSquared = 0.0000000001f;
                if (!HasValidSplineSamples ||
                    (m_SplineKnots[0].translation - m_SplineKnots[m_SplineKnots.Count - 1].translation)
                    .sqrMagnitude < minimumDistanceSquared)
                {
                    ShowSplineHint("Separate overlapping points before closing the loop");
                    return false;
                }
            }
            BeginSplineKnotEdit();
            m_SplineClosed = !m_SplineClosed;
            if (m_SplineClosed && m_SplineAction == SplineAction.Append)
                m_SplineAction = SplineAction.MoveAndRotate;
            m_HoveredSplineKnot = -1;
            SampleSpline();
            if (m_EditableSpline != null)
                ApplySavedSplineEdit();
            else
            {
                RecordSplineDraftEdit();
                UpdateSplineBrushPreview();
            }
            return true;
        }

        public override void HideTool(bool hide)
        {
            base.HideTool(hide);
            if (hide)
            {
                CancelModularCopingPlacement();
                PauseSplineInput();
            }
        }

        protected override void OnDestroy()
        {
            DestroyModularCopingPreview();
            ClearSplineDraft();
            Destroy(m_SplineMaterial);
            Destroy(m_SplineSelectedMaterial);
            Destroy(m_SplineHullMaterial);
            Destroy(m_SplineSelectRayMaterial);
            if (m_TransitionPreviewLine != null)
                Destroy(m_TransitionPreviewLine.gameObject);
            Destroy(m_TransitionPreviewMaterial);
            if (m_BrushHeightLabel != null)
                Destroy(m_BrushHeightLabel.gameObject);
            Destroy(m_BrushHeightLabelMaterial);
            base.OnDestroy();
        }

        private static void ShowSplineHint(string message)
        {
            OutputWindowScript.m_Instance?.CreateInfoCardAtController(
                InputManager.ControllerName.Brush, message, alsoConsole: false);
        }
    }
}
