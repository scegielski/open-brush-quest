// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TiltBrush
{
    /// Supplies room lighting to materials that use Unity's default probes.
    internal sealed class EnvironmentCaptureLighting : IDisposable
    {
        private readonly EnvironmentCaptureMaps m_Maps;
        private readonly Color[] m_RoomSamples;
        private Texture m_PreviousReflection;
        private DefaultReflectionMode m_PreviousMode;
        private SphericalHarmonicsL2 m_PreviousAmbient;
        private SphericalHarmonicsL2 m_AppliedAmbient;
        private bool m_HasAmbientOverride;

        public EnvironmentCaptureLighting(EnvironmentCaptureMaps maps)
        {
            m_Maps = maps;
            m_RoomSamples = maps.ReadAmbientSamples();
        }

        public void Update()
        {
            // SceneSettings may replace the environment independently of capture.
            var reflection = RenderSettings.customReflectionTexture;
            if (m_Maps.SceneReflection == null || reflection != m_Maps.SceneReflection)
            {
                m_PreviousReflection = reflection;
                m_PreviousMode = RenderSettings.defaultReflectionMode;
                Texture fallback = m_PreviousMode == DefaultReflectionMode.Custom ? reflection : ReflectionProbe.defaultTexture;
                if (m_Maps.ComposeSceneReflection(fallback, ReflectionProbe.defaultTextureHDRDecodeValues))
                {
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                    RenderSettings.customReflectionTexture = m_Maps.SceneReflection;
                }
            }

            var ambient = RenderSettings.ambientProbe;
            if (!m_HasAmbientOverride || ambient != m_AppliedAmbient)
            {
                m_PreviousAmbient = ambient;
                RenderSettings.ambientProbe = BlendAmbient(ambient, m_RoomSamples);
                m_AppliedAmbient = RenderSettings.ambientProbe;
                m_HasAmbientOverride = true;
            }
        }

        public void Dispose()
        {
            if (m_Maps.SceneReflection != null && RenderSettings.customReflectionTexture == m_Maps.SceneReflection)
            {
                RenderSettings.customReflectionTexture = m_PreviousReflection;
                RenderSettings.defaultReflectionMode = m_PreviousMode;
            }
            if (m_HasAmbientOverride && RenderSettings.ambientProbe == m_AppliedAmbient)
                RenderSettings.ambientProbe = m_PreviousAmbient;
        }

        internal static SphericalHarmonicsL2 BlendAmbient(SphericalHarmonicsL2 original, Color[] samples)
        {
            const float roomWeight = 0.35f;
            float coverage = 0f;
            var roomProbe = new SphericalHarmonicsL2();
            for (int face = 0; face < samples.Length; face++)
            {
                Color sample = samples[face];
                coverage += Mathf.Clamp01(sample.a) / samples.Length;

                // RGB is already premultiplied by captured coverage.
                roomProbe.AddDirectionalLight(EnvironmentCaptureMaps.GetFaceDirection(face),
                    sample, 2f / samples.Length);
            }
            return original * (1f - coverage * roomWeight) + roomProbe * roomWeight;
        }
    }
}
