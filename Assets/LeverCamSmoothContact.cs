using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;

// Smooths spatial contact normals before the solver. Does not filter measured torque over time.
[DisallowMultipleComponent]
public sealed class LeverCamSmoothContact : MonoBehaviour
{
    struct Edge
    {
        public Vector3 A, B, NormalA, NormalB;
    }
    Dictionary<EntityId, Edge> edges;
    EntityId tipId;
    bool subscribed;

    public void Configure(Transform profile, SphereCollider tip)
    {
        Unsubscribe();
        if (!profile || !tip) return;
        var colliders = profile.GetComponentsInChildren<MeshCollider>()
            .Where(c => c.sharedMesh && c.name.StartsWith("CamSegment_", StringComparison.Ordinal))
            .OrderBy(c => c.name, StringComparer.Ordinal).ToArray();
        if (colliders.Length < 2) return;
        var a = new Vector3[colliders.Length];
        var b = new Vector3[colliders.Length];
        var normals = new Vector3[colliders.Length];
        for (int i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i]; var vertices = c.sharedMesh.vertices;
            if (vertices.Length != 8) return;
            a[i] = profile.InverseTransformPoint(c.transform.TransformPoint(vertices[1]));
            b[i] = profile.InverseTransformPoint(c.transform.TransformPoint(vertices[2]));
            a[i].z = b[i].z = 0f;
            Vector3 tangent = (b[i] - a[i]).normalized;
            normals[i] = new Vector3(tangent.y, -tangent.x, 0f);
        }
        var cache = new Dictionary<EntityId, Edge>(colliders.Length);
        for (int i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i];
            Vector3 na = i > 0 && (a[i] - b[i-1]).sqrMagnitude < 1e-10f
                ? (normals[i-1] + normals[i]).normalized : normals[i];
            Vector3 nb = i+1 < colliders.Length && (b[i] - a[i+1]).sqrMagnitude < 1e-10f
                ? (normals[i] + normals[i+1]).normalized : normals[i];
            Quaternion toCollider = Quaternion.Inverse(c.transform.rotation) * profile.rotation;
            cache.Add(c.GetEntityId(), new Edge {
                A = Quaternion.Inverse(c.transform.rotation) * (profile.TransformPoint(a[i]) - c.transform.position),
                B = Quaternion.Inverse(c.transform.rotation) * (profile.TransformPoint(b[i]) - c.transform.position),
                NormalA = toCollider * na, NormalB = toCollider * nb
            });
            c.hasModifiableContacts = true;
        }
        tipId = tip.GetEntityId();
        // Publish once. Callback threads read immutable value data only; no Transform calls.
        edges = cache;
        Physics.ContactModifyEvent += Modify;
        Physics.ContactModifyEventCCD += Modify;
        subscribed = true;
    }

    void Modify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
    {
        var cache = edges;
        if (cache == null) return;
        for (int p = 0; p < pairs.Length; p++)
        {
            var pair = pairs[p]; Edge edge; bool first;
            if (pair.otherColliderEntityId == tipId && cache.TryGetValue(pair.colliderEntityId, out edge)) first = true;
            else if (pair.colliderEntityId == tipId && cache.TryGetValue(pair.otherColliderEntityId, out edge)) first = false;
            else continue;
            Quaternion rotation = first ? pair.rotation : pair.otherRotation;
            Vector3 position = first ? pair.position : pair.otherPosition;
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 tangent = edge.B-edge.A;
            float lengthSquared = tangent.sqrMagnitude;
            if (lengthSquared < 1e-14f) continue;
            for (int i = 0; i < pair.contactCount; i++)
            {
                Vector3 point = inverse * (pair.GetPoint(i)-position);
                Vector3 original = pair.GetNormal(i);
                // Keep contacts on the front/back faces as authored.
                if (Mathf.Abs((inverse*original).z) > 0.5f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(point-edge.A,tangent)/lengthSquared);
                Vector3 normal = rotation * Vector3.Lerp(edge.NormalA,edge.NormalB,t).normalized;
                if (Vector3.Dot(normal,original) < 0f) normal = -normal;
                pair.SetNormal(i,normal);
            }
        }
    }

    void OnDisable() => Unsubscribe();
    void OnDestroy() => Unsubscribe();
    void Unsubscribe()
    {
        if (!subscribed) return;
        Physics.ContactModifyEvent -= Modify;
        Physics.ContactModifyEventCCD -= Modify;
        subscribed = false;
    }
}
