// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    internal sealed class SplineStrokePicker
    {
        private readonly List<MeshFilter> m_Filters = new();
        private readonly List<Vector3> m_Vertices = new();
        private readonly List<int> m_Indices = new();
        private Mesh m_VertexMesh;
        private Mesh m_IndexMesh;
        private int m_IndexSubmesh;

        internal void BeginQuery()
        {
            // Meshes can change without getting a new instance after a drag or a batch edit.
            m_VertexMesh = null;
            m_IndexMesh = null;
        }

        internal bool TryHitStroke(Stroke stroke, GameObject editPreview, Ray roomRay, ref float roomDepth)
        {
            if (editPreview != null && editPreview.activeInHierarchy)
                return TryHitObject(editPreview, roomRay, ref roomDepth);

            if (stroke.m_Type == Stroke.Type.BatchedBrushStroke)
                return TryHitSubset(stroke.m_BatchSubset, roomRay, ref roomDepth);

            return TryHitObject(stroke.m_Object, roomRay, ref roomDepth);
        }

        private bool TryHitObject(GameObject obj, Ray roomRay, ref float roomDepth)
        {
            if (obj == null || !obj.activeInHierarchy)
                return false;

            bool hit = false;
            obj.GetComponentsInChildren(false, m_Filters);
            foreach (var filter in m_Filters)
            {
                if (filter.TryGetComponent<Renderer>(out var renderer) && renderer.enabled)
                    hit |= TryHitMesh(filter, roomRay, ref roomDepth);
            }
            return hit;
        }

        private bool TryHitSubset(BatchSubset subset, Ray roomRay, ref float roomDepth)
        {
            var batch = subset?.m_ParentBatch;
            if (batch == null || !subset.m_Active || !batch.gameObject.activeInHierarchy ||
                !TryEnterBounds(batch.transform, subset.m_Bounds, roomRay, roomDepth,
                    out var ray, out float depth, out float distanceScale))
                return false;

            Vector3[] vertices;
            int[] indices;
            int indexCount;
            var geometry = batch.Geometry;
            if (geometry.IsGeometryResident)
            {
                batch.GetTriangles(out vertices, out _, out indices, out indexCount);
            }
            else
            {
                // Avoid repopulating normals, colors and UVs in a memory-optimized batch.
                var mesh = geometry.GetBackingMesh();
                if (mesh == null || !mesh.isReadable)
                    return false;
                vertices = ReadVertices(mesh);
                if (geometry.m_Tris != null)
                {
                    indices = geometry.m_Tris.GetBackingArray();
                    indexCount = geometry.m_Tris.Count;
                }
                else
                {
                    indices = ReadIndices(mesh, 0);
                    indexCount = m_Indices.Count;
                }
            }

            int end = Mathf.Min(subset.m_iTriIndex + subset.m_nTriIndex, indexCount);
            if (!TryHitTriangles(ray, vertices, indices, subset.m_iTriIndex, end, ref depth))
                return false;
            roomDepth = depth / distanceScale;
            return true;
        }

        private bool TryHitMesh(MeshFilter filter, Ray roomRay, ref float roomDepth)
        {
            var mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable ||
                !TryEnterBounds(filter.transform, mesh.bounds, roomRay, roomDepth,
                    out var ray, out float depth, out float distanceScale))
                return false;

            var vertices = ReadVertices(mesh);
            bool hit = false;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    continue;
                var indices = ReadIndices(mesh, submesh);
                hit |= TryHitTriangles(ray, vertices, indices, 0, m_Indices.Count, ref depth);
            }
            if (hit)
                roomDepth = depth / distanceScale;
            return hit;
        }

        private static bool TryEnterBounds(Transform mesh, Bounds bounds, Ray roomRay, float roomDepth,
            out Ray ray, out float depth, out float distanceScale)
        {
            var inverse = mesh.worldToLocalMatrix;
            Vector3 direction = inverse.MultiplyVector(roomRay.direction);
            distanceScale = direction.magnitude;
            depth = roomDepth * distanceScale;
            ray = default;
            if (distanceScale <= 0f)
                return false;
            ray = new Ray(inverse.MultiplyPoint3x4(roomRay.origin), direction / distanceScale);
            return bounds.IntersectRay(ray, out float entry) && entry < depth;
        }

        private Vector3[] ReadVertices(Mesh mesh)
        {
            if (m_VertexMesh != mesh)
            {
                mesh.GetVertices(m_Vertices);
                m_VertexMesh = mesh;
            }
            return m_Vertices.GetBackingArray();
        }

        private int[] ReadIndices(Mesh mesh, int submesh)
        {
            if (m_IndexMesh != mesh || m_IndexSubmesh != submesh)
            {
                mesh.GetTriangles(m_Indices, submesh);
                m_IndexMesh = mesh;
                m_IndexSubmesh = submesh;
            }
            return m_Indices.GetBackingArray();
        }

        private static bool TryHitTriangles(Ray ray, Vector3[] vertices, int[] indices, int start, int end,
            ref float depth)
        {
            const float edgeTolerance = 1e-5f;
            bool hit = false;
            for (int index = start; index + 2 < end; index += 3)
            {
                Vector3 first = vertices[indices[index]];
                Vector3 edge1 = vertices[indices[index + 1]] - first;
                Vector3 edge2 = vertices[indices[index + 2]] - first;
                Vector3 across = Vector3.Cross(ray.direction, edge2);
                float determinant = Vector3.Dot(edge1, across);
                if (Mathf.Abs(determinant) < 1e-8f)
                    continue;

                float inverse = 1f / determinant;
                Vector3 fromVertex = ray.origin - first;
                float u = Vector3.Dot(fromVertex, across) * inverse;
                if (u < -edgeTolerance || u > 1f + edgeTolerance)
                    continue;

                Vector3 offset = Vector3.Cross(fromVertex, edge1);
                float v = Vector3.Dot(ray.direction, offset) * inverse;
                if (v < -edgeTolerance || u + v > 1f + edgeTolerance)
                    continue;

                float candidate = Vector3.Dot(edge2, offset) * inverse;
                if (candidate >= 0f && candidate < depth)
                {
                    depth = candidate;
                    hit = true;
                }
            }
            return hit;
        }
    }
}
