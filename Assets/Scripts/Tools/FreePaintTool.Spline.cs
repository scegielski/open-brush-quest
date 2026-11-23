// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using OpenBrush.Multiplayer;
using UnityEngine;

namespace TiltBrush
{
    public partial class FreePaintTool
    {
        private enum SplineMode { Inactive, Place, Edit }
        private enum SplineAction { Move, Append, Insert, Delete }

        private const float kSplineHandleSize = 0.065f;
        private const float kSplinePickRadius = 0.095f;
        private const float kSplineMinimumSpacing = 0.011f;

        private readonly List<TrTransform> m_SplineKnots = new();
        private readonly List<TrTransform> m_SplineSamples = new();
        private readonly List<PointerManager.ControlPoint> m_SplineStrokePoints = new();
        private readonly List<Transform> m_SplineHandles = new();
        private readonly Stack<(TrTransform[] Knots, bool Closed)> m_SplineDraftUndo = new();
        private readonly Stack<(TrTransform[] Knots, bool Closed)> m_SplineDraftRedo = new();
        private SplineMode m_SplineMode;
        private CanvasScript m_SplineCanvas;
        private SketchMemoryScript m_SplineMemory;
        private GameObject m_SplinePreview;
        private bool m_SplinePreviewDirty;
        private BaseBrushScript m_DraftSplinePreviewBrush;
        private Material m_SplineMaterial;
        private Material m_SplineSelectedMaterial;
        private int m_DraggedSplineKnot = -1;
        private int m_HoveredSplineKnot = -1;
        private Vector3 m_SplineDragOffset;
        private (TrTransform[] Knots, bool Closed)? m_SplineDraftBeforeEdit;
        private bool m_SplineClosed;
        private Stroke m_EditableSpline;
        private PointerManager.ControlPoint[] m_LastAppliedSplinePoints;
        private BaseBrushScript m_SavedSplinePreviewBrush;
        private SplineAction m_SplineAction;
        private GameObject m_SplineActionMenu;
        private Mesh m_SplineActionMenuMesh;
        private readonly Vector2[] m_SplineMenuUvs = new Vector2[4];
        private Vector3 m_SplineGestureOrigin;
        private SplineAction m_SplineMenuSelection;
        private int m_SplineTopologyIndex = -1;
        private float m_SplineInsertionT;

        private bool IsSplineMode => m_SplineMode != SplineMode.Inactive;
        internal bool UsesSplinePreview => IsSplineMode && PanelManager.m_Instance.AdvancedModeActive() &&
            ApiManager.Instance.ForcePainting == ApiManager.ForcePaintingMode.None;
        internal bool OwnsSplineDraftHistory => PanelManager.m_Instance.AdvancedModeActive() &&
            m_EditableSpline == null && (m_SplineMode == SplineMode.Place ||
                (m_SplineMode == SplineMode.Edit &&
                 (m_SplineKnots.Count > 0 || m_SplineDraftRedo.Count > 0)));
        internal bool CanUndoSplineDraft => m_DraggedSplineKnot < 0 && m_SplineDraftUndo.Count > 0;
        internal bool CanRedoSplineDraft => m_DraggedSplineKnot < 0 && m_SplineDraftRedo.Count > 0;

        private bool UpdateSplineDrawingMode()
        {
            var sketchControls = SketchControlsScript.m_Instance;
            bool canDraw = m_AllowDrawing && !m_EatInput && !m_ToolHidden &&
                App.Instance.IsInStateThatAllowsPainting() && sketchControls.IsFreepaintToolReady() &&
                !MultiplayerManager.m_Instance.IsViewOnly;

            // Temporarily suspend the draft while another input mode owns the pointer.
            if (!PanelManager.m_Instance.AdvancedModeActive() ||
                ApiManager.Instance.ForcePainting != ApiManager.ForcePaintingMode.None)
            {
                SetSplinePreviewVisible(false);
                return false;
            }

            if (IsSplineMode && m_SplineCanvas != App.ActiveCanvas)
            {
                if (m_EditableSpline != null)
                    ReleaseSplineKnot();
                ClearSplineDraft();
                ShowSplineHint("Spline canceled: drawing layer changed");
                return true;
            }

            RefreshEditableSpline();
            if (IsSplineMode && !m_brushTrigger)
                ReleaseSplineKnot();

            if (canDraw && !m_BimanualTape && !m_brushTrigger && !m_wandTrigger &&
                !PointerManager.m_Instance.IsMainPointerProcessingLine())
            {
                if (m_brushUndoButtonTapped)
                {
                    AdvanceDrawingMode();
                }
                else if (IsSplineMode && m_brushUndoButtonHeld)
                {
                    ClearSplineDraft();
                    ShowSplineHint("Spline canceled");
                }
            }

            if (!IsSplineMode)
                return false;

            m_PaintingActive = false;
            PointerManager.m_Instance.EnableLine(false);
            if (!canDraw)
            {
                PauseSplineInput();
                return true;
            }

            SetSplinePreviewVisible(true);
            if (UpdateSplineActionMenu())
                return true;

            (Vector3 worldPosition, Quaternion worldRotation) = GetPointerPosition();
            PointerManager.m_Instance.SetPointerTransform(
                InputManager.ControllerName.Brush, worldPosition, worldRotation);

            var worldFromCanvas = m_SplineCanvas.Pose;
            var cursor = worldFromCanvas.inverse * TrTransform.TR(worldPosition, worldRotation);
            float pickRadius = kSplinePickRadius / worldFromCanvas.scale;
            int highlighted;
            if (m_SplineMode == SplineMode.Place)
            {
                if (m_brushTriggerDown)
                    PlaceSplineKnot(cursor);
                highlighted = m_DraggedSplineKnot;
            }
            else
            {
                highlighted = EditSplineAtCursor(cursor, pickRadius);
            }
            MoveSplineKnot(cursor);
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

        private void AdvanceDrawingMode()
        {
            switch (m_SplineMode)
            {
                case SplineMode.Place:
                    m_SplineMode = SplineMode.Edit;
                    m_SplineAction = SplineAction.Move;
                    return;
                case SplineMode.Edit:
                    if (m_EditableSpline == null && m_SplineKnots.Count >= 2 && !CommitSpline())
                    {
                        ShowSplineHint("Separate overlapping points before finishing");
                        return;
                    }
                    ClearSplineDraft();
                    return;
            }

            if (!m_LazyInputActive || !InputManager.m_Instance.AllowVrControllers)
            {
                m_LazyInputActive = !m_LazyInputActive;
                return;
            }

            m_LazyInputActive = false;
            EndLazyInputVisuals();
            m_SplineMode = SplineMode.Place;
            m_SplineCanvas = App.ActiveCanvas;
            m_SplineMemory = SketchMemoryScript.m_Instance;
            m_SplineMemory.SketchClearing += OnSplineSketchClearing;
        }

        private void PlaceSplineKnot(TrTransform cursor)
        {
            if (m_SplineKnots.Count == SplineCurve.MaxKnots)
            {
                ShowSplineHint("Point limit reached. Tap the mode button to edit.");
                return;
            }
            BeginSplineKnotEdit();
            m_DraggedSplineKnot = m_SplineKnots.Count;
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
                (m_SplineKnots[m_HoveredSplineKnot].translation - position).sqrMagnitude < exitRadius * exitRadius)
                nearest = m_HoveredSplineKnot;

            if (nearest != m_HoveredSplineKnot && nearest >= 0 &&
                InputManager.Brush is UnityXRControllerInfo brushController)
                brushController.SendHapticImpulse(0.55f, 0.06f);
            m_HoveredSplineKnot = nearest;

            return nearest;
        }

        private int EditSplineAtCursor(TrTransform cursor, float radius)
        {
            int nearest = m_SplineAction switch
            {
                SplineAction.Move or SplineAction.Delete => FindHoveredSplineKnot(cursor.translation, radius),
                SplineAction.Append when !m_SplineClosed => FindHoveredSplineKnot(cursor.translation, radius, endpointsOnly: true),
                _ => m_DraggedSplineKnot
            };
            if (!m_brushTriggerDown)
                return nearest;

            if (m_SplineKnots.Count == 0 ||
                (m_SplineAction == SplineAction.Move && nearest < 0 && m_EditableSpline != null))
            {
                var hit = FindSavedSpline(cursor.translation, radius);
                if (hit != null && hit != m_EditableSpline)
                    OpenSavedSpline(hit);
                return -1;
            }

            switch (m_SplineAction)
            {
                case SplineAction.Move when nearest >= 0:
                    BeginSplineKnotEdit();
                    m_DraggedSplineKnot = nearest;
                    m_SplineDragOffset = m_SplineKnots[nearest].translation - cursor.translation;
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
                    int minimumKnots = m_SplineClosed ? 3 : 2;
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
                    return -1;
            }
            return nearest;
        }

        private void BeginSplineKnotEdit()
        {
            if (m_EditableSpline == null)
                m_SplineDraftBeforeEdit = (m_SplineKnots.ToArray(), m_SplineClosed);
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
            AddSplineKnot(cursor, index);
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

        private void MoveSplineKnot(TrTransform cursor)
        {
            if (m_DraggedSplineKnot < 0)
                return;

            var knot = m_SplineKnots[m_DraggedSplineKnot];
            Vector3 moved = cursor.translation + m_SplineDragOffset;
            Quaternion rotation = m_SplineMode == SplineMode.Place || m_SplineTopologyIndex >= 0
                ? cursor.rotation : knot.rotation;
            if (knot.translation == moved && knot.rotation == rotation)
                return;

            if (m_EditableSpline != null)
            {
                int index = m_DraggedSplineKnot;
                const float minimumDistanceSquared = 0.0000000001f;
                if (IsSplineKnotTooClose(index, moved, minimumDistanceSquared))
                    return;
            }

            m_SplineKnots[m_DraggedSplineKnot] = TrTransform.TR(moved, rotation);
            m_SplineHandles[m_DraggedSplineKnot].localPosition = moved;
            SampleSpline();
            if (m_EditableSpline != null)
                UpdateSavedSplinePreview();
        }

        private void ReleaseSplineKnot()
        {
            int released = m_DraggedSplineKnot;
            m_DraggedSplineKnot = -1;
            if (released < 0)
                return;
            float spacing = kSplineMinimumSpacing / m_SplineCanvas.Pose.scale;
            if ((m_SplineMode == SplineMode.Place || m_SplineTopologyIndex >= 0) &&
                IsSplineKnotTooClose(released, m_SplineKnots[released].translation, spacing * spacing))
            {
                RemoveSplineKnot(released);
                m_SplineTopologyIndex = -1;
                SampleSpline();
                if (m_EditableSpline != null)
                {
                    RestoreSavedSplineGeometry();
                    m_SplinePreviewDirty = false;
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
            if (edit.Value.Closed == m_SplineClosed && before.Length == m_SplineKnots.Count)
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
            opposite.Push((m_SplineKnots.ToArray(), m_SplineClosed));
            ClearSplineHandles();
            var snapshot = history.Pop();
            m_SplineClosed = snapshot.Closed;
            foreach (var knot in snapshot.Knots)
                AddSplineKnot(knot);
            SampleSpline();
        }

        private void SampleSpline()
        {
            SplineCurve.Sample(m_SplineKnots, m_SplineSamples, m_SplineClosed);
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

        private void UpdateSplineBrushPreview()
        {
            if (m_EditableSpline != null)
                return;
            if (m_SplineStrokePoints.Count < 2)
            {
                ClearSplineBrushPreview();
                return;
            }

            var pointer = PointerManager.m_Instance.MainPointer;
            var descriptor = pointer.CurrentBrush;
            var color = App.BrushColor.CurrentColor;
            float size = pointer.BrushSizeAbsolute;
            var brush = m_DraftSplinePreviewBrush;
            bool styleChanged = brush == null || brush.Descriptor != descriptor ||
                brush.CurrentColor != color || brush.BaseSize_PS != size;
            if (!m_SplinePreviewDirty && !styleChanged)
                return;
            m_SplinePreviewDirty = false;
            if (descriptor == null || descriptor.m_BrushPrefab == null)
            {
                ClearSplineBrushPreview();
                return;
            }

            var first = m_SplineStrokePoints[0];
            var start = TrTransform.TR(first.m_Pos, first.m_Orient);
            if (styleChanged || !brush.AlwaysRebuildPreviewBrush())
            {
                ClearSplineBrushPreview();
                brush = BaseBrushScript.Create(m_SplineCanvas.transform, start, descriptor, color, size);
                brush.gameObject.name = "Draft spline brush";
                brush.RandomSeed = 0;
                m_DraftSplinePreviewBrush = brush;
            }
            else
            {
                brush.ResetBrushForPreview(start);
            }
            for (int i = 0; i < m_SplineStrokePoints.Count && !brush.IsOutOfVerts(); i++)
            {
                var point = m_SplineStrokePoints[i];
                brush.UpdatePosition_LS(TrTransform.TR(point.m_Pos, point.m_Orient), point.m_Pressure);
            }
            brush.ApplyChangesToVisuals();
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
                if (!stroke.IsGeometryEnabled || stroke.Canvas != m_SplineCanvas ||
                    !SplineCurve.IsEditable(stroke))
                    continue;

                float radius = pickRadius + stroke.SizeInLocalSpace * 0.5f;
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

        private void OpenSavedSpline(Stroke stroke)
        {
            ClearSplineBrushPreview();
            m_EditableSpline = stroke;
            m_SplineAction = SplineAction.Move;
            LoadSavedSplineKnots();
            InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.035f);
        }

        private void RefreshEditableSpline()
        {
            if (m_EditableSpline == null)
                return;

            if (!m_EditableSpline.IsGeometryEnabled || m_EditableSpline.Canvas != m_SplineCanvas ||
                !SplineCurve.IsEditable(m_EditableSpline))
            {
                m_EditableSpline = null;
                LoadSavedSplineKnots();
            }
            else if (m_LastAppliedSplinePoints != m_EditableSpline.m_ControlPoints)
            {
                // Undo, redo and other stroke tools can replace the control points.
                LoadSavedSplineKnots();
            }
        }

        private void LoadSavedSplineKnots()
        {
            m_DraggedSplineKnot = -1;
            m_SplineTopologyIndex = -1;
            m_SavedSplinePreviewBrush = null;
            m_SplineDraftBeforeEdit = null;
            m_SplineDraftUndo.Clear();
            m_SplineDraftRedo.Clear();
            ClearSplineHandles();
            m_SplineSamples.Clear();
            m_SplineStrokePoints.Clear();

            m_LastAppliedSplinePoints = m_EditableSpline?.m_ControlPoints;
            m_SplineClosed = m_EditableSpline != null && SplineCurve.IsClosed(m_EditableSpline);
            if (m_EditableSpline != null)
            {
                for (int i = 0; i < m_EditableSpline.m_SplineKnotCount; i++)
                {
                    var point = m_LastAppliedSplinePoints[i * SplineCurve.SamplesPerSegment];
                    AddSplineKnot(TrTransform.TR(point.m_Pos, point.m_Orient));
                }
            }
            SampleSpline();
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
            var start = TrTransform.TRS(first.m_Pos, first.m_Orient, stroke.m_BrushScale);
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
                brush.SetStrokeData(stroke);
            }
            else
            {
                brush.ResetBrushForPreview(start);
            }

            for (int i = 0; i < m_SplineStrokePoints.Count && !brush.IsOutOfVerts(); i++)
            {
                var point = m_SplineStrokePoints[i];
                int sourceIndex = GetSavedSplineSampleIndex(i);
                brush.UpdatePosition_LS(TrTransform.TRS(point.m_Pos, point.m_Orient, stroke.m_BrushScale),
                    m_LastAppliedSplinePoints[sourceIndex].m_Pressure, stroke.GetColor(sourceIndex));
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
            var originalColors = m_EditableSpline.m_OverrideColors;
            bool resize = points.Length != m_LastAppliedSplinePoints.Length;
            var colors = resize && originalColors != null ? new List<Color32?>(points.Length) : originalColors;
            for (int i = 0; i < points.Length; i++)
            {
                int sourceIndex = GetSavedSplineSampleIndex(i);
                points[i] = m_LastAppliedSplinePoints[sourceIndex];
                points[i].m_Pos = m_SplineStrokePoints[i].m_Pos;
                points[i].m_Orient = m_SplineStrokePoints[i].m_Orient;
                if (resize && colors != null)
                    colors.Add(sourceIndex < originalColors.Count ? originalColors[sourceIndex] : null);
            }
            ReleaseSavedSplinePreview();
            if (m_EditableSpline.m_Object != null)
                m_EditableSpline.m_Object.SetActive(false);
            var command = new ModifyStrokePointsCommand(m_EditableSpline, points, m_SplineKnots.Count, colors);
            m_SplineMemory.PerformAndRecordCommand(command);
            m_LastAppliedSplinePoints = points;
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
                points[i].m_TimestampMs = time;

            var pointer = PointerManager.m_Instance.MainPointer;
            var stroke = new Stroke
            {
                m_Type = Stroke.Type.NotCreated,
                m_IntendedCanvas = m_SplineCanvas,
                m_BrushGuid = pointer.CurrentBrush.m_Guid,
                m_BrushSize = pointer.BrushSizeAbsolute,
                m_BrushScale = 1,
                m_Color = App.BrushColor.CurrentColor,
                m_ControlPoints = points,
                m_SplineKnotCount = m_SplineKnots.Count,
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
                }
            }
            if (m_SplinePreview != null)
                m_SplinePreview.SetActive(visible);
            if (!visible)
            {
                PauseSplineInput();
                ClearSplineBrushPreview();
            }
        }

        private void PauseSplineInput()
        {
            ReleaseSplineKnot();
            m_HoveredSplineKnot = -1;
            if (m_SplineActionMenu != null)
                m_SplineActionMenu.SetActive(false);
        }

        private void ClearSplineDraft()
        {
            ClearSplineBrushPreview();
            RestoreSavedSplineGeometry();
            m_EditableSpline = null;
            m_LastAppliedSplinePoints = null;
            if (m_SplineMemory != null)
                m_SplineMemory.SketchClearing -= OnSplineSketchClearing;
            m_SplineMemory = null;
            m_SplineMode = SplineMode.Inactive;
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
            if (m_SplineActionMenu != null)
                m_SplineActionMenu.SetActive(false);
            if (m_SplinePreview != null)
            {
                m_SplinePreview.SetActive(false);
                Destroy(m_SplinePreview);
                m_SplinePreview = null;
            }
        }

        private void OnSplineSketchClearing()
        {
            // Clearing the sketch removes the preview stroke along with the other geometry.
            ReleaseSavedSplinePreview();
            ClearSplineDraft();
        }

        private bool UpdateSplineActionMenu()
        {
            if (m_SplineMode != SplineMode.Edit)
                return false;

            bool open = m_SplineActionMenu != null && m_SplineActionMenu.activeSelf;
            var brush = InputManager.Brush;
            const InputManager.SketchCommands button = InputManager.SketchCommands.ShowPinCushion;
            if (!open)
            {
                if (!brush.GetCommandDown(button) || m_DraggedSplineKnot >= 0 || m_brushTrigger)
                    return false;
                var spawn = InputManager.m_Instance.GetPinCushionSpawn();
                var pointer = InputManager.m_Instance.GetBrushControllerAttachPoint();
                if (pointer == null)
                    return false;
                OpenSplineActionMenu(spawn != null ? spawn.position : pointer.position, pointer.position);
                m_HoveredSplineKnot = -1;
                return true;
            }

            if (!brush.GetCommand(button))
            {
                m_SplineAction = m_SplineMenuSelection;
                m_SplineActionMenu.SetActive(false);
                return true;
            }
            if (m_brushTriggerDown)
            {
                if (ToggleSplineLoop())
                    m_SplineActionMenu.SetActive(false);
                return true;
            }

            var attachPoint = InputManager.m_Instance.GetBrushControllerAttachPoint();
            if (attachPoint == null)
            {
                m_SplineActionMenu.SetActive(false);
                return true;
            }
            Vector3 offset = m_SplineActionMenu.transform.InverseTransformDirection(
                attachPoint.position - m_SplineGestureOrigin);
            const float gestureDistance = 0.05f;
            SplineAction selection = m_SplineAction;
            if (new Vector2(offset.x, offset.y).sqrMagnitude >= gestureDistance * gestureDistance)
            {
                selection = Mathf.Abs(offset.x) > Mathf.Abs(offset.y)
                    ? (offset.x > 0 ? SplineAction.Append : SplineAction.Insert)
                    : (offset.y > 0 ? SplineAction.Move : SplineAction.Delete);
            }
            if (selection != m_SplineMenuSelection)
            {
                m_SplineMenuSelection = selection;
                UpdateSplineActionMenuTexture();
                if (brush is UnityXRControllerInfo xrController)
                    xrController.SendHapticImpulse(0.30f, 0.035f);
            }
            return true;
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
                m_SplineAction = SplineAction.Move;
            m_HoveredSplineKnot = -1;
            SampleSpline();
            if (m_EditableSpline != null)
                ApplySavedSplineEdit();
            else
                RecordSplineDraftEdit();
            return true;
        }

        private void OpenSplineActionMenu(Vector3 spawn, Vector3 gestureOrigin)
        {
            if (m_SplineActionMenu == null)
                CreateSplineActionMenu();

            var head = ViewpointScript.Head;
            Vector3 toSpawn = spawn - head.position;
            Vector3 direction = toSpawn.magnitude > 0.1f && Vector3.Dot(toSpawn, head.forward) > 0
                ? toSpawn.normalized : head.forward;
            // Put the menu in the controller's direction, just beyond the hand.
            float distance = Mathf.Max(0.65f, toSpawn.magnitude + 0.12f);
            var menuTransform = m_SplineActionMenu.transform;
            // Keep text legible when the controller is held farther away.
            menuTransform.localScale = Vector3.one * (1.6f * distance / 0.65f);
            menuTransform.SetPositionAndRotation(head.position + direction * distance + head.up * 0.055f,
                Quaternion.LookRotation(direction, head.up));

            m_SplineGestureOrigin = gestureOrigin;
            m_SplineMenuSelection = m_SplineAction;
            UpdateSplineActionMenuTexture();
            m_SplineActionMenu.SetActive(true);
            InputManager.m_Instance.TriggerHaptics(InputManager.ControllerName.Brush, 0.035f);
        }

        private void CreateSplineActionMenu()
        {
            m_SplineActionMenu = new GameObject("Spline actions", typeof(MeshFilter), typeof(MeshRenderer));
            m_SplineActionMenu.layer = LayerMask.NameToLayer("Panels");
            m_SplineActionMenuMesh = new Mesh
            {
                name = "Spline action menu plane",
                vertices = new[]
                {
                    new Vector3(-0.20f, -0.15f), new Vector3(-0.20f, 0.15f),
                    new Vector3(0.20f, 0.15f), new Vector3(0.20f, -0.15f)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            m_SplineActionMenu.GetComponent<MeshFilter>().sharedMesh = m_SplineActionMenuMesh;
            var renderer = m_SplineActionMenu.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = Resources.Load<Material>("SplineActionMenu");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private void UpdateSplineActionMenuTexture()
        {
            // The four atlas tiles are ordered like SplineAction, starting at the upper left.
            int tile = (int)m_SplineMenuSelection;
            float left = 0.5f * (tile % 2);
            float bottom = tile < 2 ? 0.5f : 0f;
            const float pad = 0.001f;
            m_SplineMenuUvs[0] = new Vector2(left + pad, bottom + pad);
            m_SplineMenuUvs[1] = new Vector2(left + pad, bottom + 0.5f - pad);
            m_SplineMenuUvs[2] = new Vector2(left + 0.5f - pad, bottom + 0.5f - pad);
            m_SplineMenuUvs[3] = new Vector2(left + 0.5f - pad, bottom + pad);
            m_SplineActionMenuMesh.uv = m_SplineMenuUvs;
        }

        public override void HideTool(bool hide)
        {
            base.HideTool(hide);
            if (hide)
                PauseSplineInput();
        }

        protected override void OnDestroy()
        {
            ClearSplineDraft();
            Destroy(m_SplineActionMenu);
            Destroy(m_SplineActionMenuMesh);
            Destroy(m_SplineMaterial);
            Destroy(m_SplineSelectedMaterial);
            base.OnDestroy();
        }

        private static void ShowSplineHint(string message)
        {
            OutputWindowScript.m_Instance?.CreateInfoCardAtController(
                InputManager.ControllerName.Brush, message, alsoConsole: false);
        }
    }
}
