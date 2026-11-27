// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TiltBrush
{
    internal sealed class RoomCaptureHud : IDisposable
    {
        private TextMeshPro m_Label;
        private Material m_Material;

        internal RoomCaptureHud(Transform head, int layer)
        {
            if (head == null)
                return;

            var labelObject = new GameObject("Room Capture HUD") { layer = layer };
            m_Label = labelObject.AddComponent<TextMeshPro>();
            var rect = m_Label.rectTransform;
            rect.SetParent(head, false);

            // Cancel the inherited XR rig scale so the label has a consistent depth and size.
            float inverseHeadScale = 1f / head.lossyScale.x;
            rect.localPosition = new Vector3(0f, -0.18f, 0.9f) * (App.METERS_TO_UNITS * inverseHeadScale);
            rect.localScale = Vector3.one * inverseHeadScale;
            rect.sizeDelta = new Vector2(5f, 1f);

            m_Label.alignment = TextAlignmentOptions.Center;
            m_Label.color = Color.white;
            m_Label.richText = false;
            m_Label.fontSize = 3f;
            m_Label.enableAutoSizing = true;
            m_Label.fontSizeMin = 2f;
            m_Label.fontSizeMax = 3f;
            m_Label.textWrappingMode = TextWrappingModes.NoWrap;
            m_Label.text = "";

            var overlay = Resources.Load<Material>("Brushes/Basic/Transition/TransitionOverlayText");
            if (overlay != null && m_Label.fontSharedMaterial != null)
            {
                m_Material = new Material(m_Label.fontSharedMaterial) { shader = overlay.shader };
                m_Material.renderQueue = (int)RenderQueue.Overlay + 2;
                m_Material.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                m_Material.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.14f);
                m_Label.fontSharedMaterial = m_Material;
            }
            m_Label.renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Label.renderer.receiveShadows = false;
            m_Label.renderer.allowOcclusionWhenDynamic = false;
        }

        internal void UpdateStatus(string text)
        {
            if (m_Label != null && m_Label.text != text)
                m_Label.text = text;
        }

        public void Dispose()
        {
            if (m_Label != null)
            {
                m_Label.gameObject.SetActive(false);
                Object.Destroy(m_Label.gameObject);
                m_Label = null;
            }
            if (m_Material != null)
            {
                Object.Destroy(m_Material);
                m_Material = null;
            }
        }
    }
}
