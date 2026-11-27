// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TiltBrush
{
    /// Sparse markers on a world-space sphere. Each marker represents an actual coverage sample.
    internal sealed class EnvironmentCaptureGuide : IDisposable
    {
        private const int kMarkerCount = 144;
        private const float kRadius = 2f * App.METERS_TO_UNITS;
        private const float kMarkerHalfWidth = 0.055f * App.METERS_TO_UNITS;

        private static readonly int[] kSamples = SelectCoverageSamples();

        private readonly GameObject m_Root;
        private readonly Material m_Material;
        private readonly Mesh m_Mesh;
        private readonly Color32[] m_VertexStates = new Color32[kMarkerCount * 4];
        private bool m_Disposed;

        internal EnvironmentCaptureGuide(Shader shader, Vector3 center, int layer)
        {
            m_Root = new GameObject("Room capture coverage guide")
            {
                layer = layer,
                hideFlags = HideFlags.HideAndDontSave
            };
            m_Root.transform.position = center;
            m_Mesh = new Mesh { name = "Room capture direction markers", hideFlags = HideFlags.HideAndDontSave };
            m_Mesh.MarkDynamic();
            CreateMesh();
            m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_Root.AddComponent<MeshFilter>().sharedMesh = m_Mesh;

            var renderer = m_Root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        internal void SetCenter(Vector3 center) => m_Root.transform.position = center;

        internal void UpdateCoverage(EnvironmentCaptureMaps maps)
        {
            bool changed = false;
            for (int i = 0; i < kSamples.Length; i++)
            {
                int vertex = 4 * i;
                if (m_VertexStates[vertex].r != 0 || !maps.IsDirectionCovered(kSamples[i]))
                    continue;

                var covered = new Color32(255, 0, 0, 255);
                for (int corner = 0; corner < 4; corner++)
                    m_VertexStates[vertex + corner] = covered;
                changed = true;
            }
            if (changed)
                m_Mesh.colors32 = m_VertexStates;
        }

        private void CreateMesh()
        {
            var vertices = new Vector3[kMarkerCount * 4];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[kMarkerCount * 6];

            for (int i = 0; i < kSamples.Length; i++)
            {
                Vector3 direction = EnvironmentCaptureMaps.GetCoverageDirection(kSamples[i]);
                Vector3 center = direction * kRadius;
                Vector3 reference = Mathf.Abs(direction.y) < 0.95f ? Vector3.up : Vector3.forward;
                Vector3 right = Vector3.Cross(reference, direction).normalized * kMarkerHalfWidth;
                Vector3 up = Vector3.Cross(direction, right);
                int v = i * 4;
                vertices[v] = center - right - up;
                vertices[v + 1] = center - right + up;
                vertices[v + 2] = center + right + up;
                vertices[v + 3] = center + right - up;

                uv[v] = new Vector2(-1f, -1f);
                uv[v + 1] = new Vector2(-1f, 1f);
                uv[v + 2] = new Vector2(1f, 1f);
                uv[v + 3] = new Vector2(1f, -1f);

                int t = i * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            m_Mesh.vertices = vertices;
            m_Mesh.uv = uv;
            m_Mesh.triangles = triangles;
            m_Mesh.colors32 = m_VertexStates;
        }

        private static int[] SelectCoverageSamples()
        {
            Vector3[] sparse = EnvironmentCaptureMaps.CreateCoverageDirections(kMarkerCount);
            var selected = new int[sparse.Length];
            for (int i = 0; i < sparse.Length; i++)
            {
                float closest = float.NegativeInfinity;
                for (int j = 0; j < EnvironmentCaptureMaps.CoverageSamples; j++)
                {
                    float dot = Vector3.Dot(sparse[i], EnvironmentCaptureMaps.GetCoverageDirection(j));
                    if (dot <= closest)
                        continue;
                    closest = dot;
                    selected[i] = j;
                }
            }
            return selected;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_Root.SetActive(false);
            UnityEngine.Object.Destroy(m_Mesh);
            UnityEngine.Object.Destroy(m_Material);
            UnityEngine.Object.Destroy(m_Root);
        }
    }
}
