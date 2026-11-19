using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds a dynamic compound collider from the cam's actual mid-plane outline.</summary>
internal static class LeverCamColliderSetup
{
    const string ModelPath = "Assets/Models/Lever/lever_middle_cam.fbx";
    const string MeshPath = "Assets/Physics/LeverCamContactMeshes.asset";
    const string MaterialPath = "Assets/Physics/LeverContactMaterial.physicMaterial";
    const string ProfileName = "CamProfile";
    // Measured in the axle coordinate system (metres). Excludes the shaft and handle.
    const float InnerRadius = 0.018f;
    const float OuterRadius = 0.0251f;
    const float ChordTolerance = 0.000001f; // 0.001 mm maximum outline simplification error.
    const float HalfWidth = 0.0075f;

    [MenuItem("Tools/Lever/Rebuild Cam Colliders")]
    static void Rebuild()
    {
        var rotor = GameObject.Find("GearRotor");
        if (rotor && Ensure(rotor.transform, true))
        {
            EditorSceneManager.MarkSceneDirty(rotor.scene);
            EditorSceneManager.SaveScene(rotor.scene);
        }
    }

    internal static bool Ensure(Transform rotor, bool force = false)
    {
        var filter = rotor.GetComponentsInChildren<MeshFilter>(true)
            .FirstOrDefault(f => f.sharedMesh && AssetDatabase.GetAssetPath(f.sharedMesh) == ModelPath);
        var colliders = rotor.Find("GearColliders");
        if (!filter || !colliders) return false;
        var existing = colliders.Find(ProfileName);
        if (existing && !force) return true;

        var toAxle = colliders.worldToLocalMatrix * filter.transform.localToWorldMatrix;
        var vertices = filter.sharedMesh.vertices.Select(v => toAxle.MultiplyPoint3x4(v)).ToArray();
        var triangles = filter.sharedMesh.triangles;
        var points = new List<Vector2>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            var intersections = new List<Vector2>(2);
            for (int edge = 0; edge < 3; edge++)
            {
                var a = vertices[triangles[i + edge]];
                var b = vertices[triangles[i + (edge + 1) % 3]];
                if ((a.z < 0f && b.z >= 0f) || (b.z < 0f && a.z >= 0f))
                {
                    var p = Vector3.Lerp(a, b, -a.z / (b.z - a.z));
                    intersections.Add(new Vector2(p.x, p.y));
                }
            }
            if (intersections.Count != 2 ||
                Vector2.Distance(intersections[0], intersections[1]) < 0.0000001f) continue;
            if (intersections.All(p => p.magnitude >= InnerRadius && p.magnitude <= OuterRadius))
                points.AddRange(intersections);
        }

        // Start beside the handle, then follow the circular part through the lower cam valleys.
        // Keep the handle opening open: never close the last point back to the first.
        points.Sort((a, b) => OutlineAngle(a).CompareTo(OutlineAngle(b)));
        var outline = new List<Vector2>();
        foreach (var point in points)
            if (outline.Count == 0 || Vector2.Distance(outline[outline.Count - 1], point) > 0.0000002f)
                outline.Add(point);
        if (outline.Count < 10) throw new InvalidOperationException("Cam outline was not found.");
        for (int i = 1; i < outline.Count; i++)
            if (OutlineAngle(outline[i]) - OutlineAngle(outline[i - 1]) > 20f)
                throw new InvalidOperationException("Unexpected gap in cam outline; colliders were not changed.");
        var simplified = new List<Vector2> { outline[0] };
        Simplify(outline, 0, outline.Count - 1, simplified);

        Undo.SetCurrentGroupName("Fit colliders to lever_middle_cam");
        if (existing) Undo.DestroyObjectImmediate(existing.gameObject);
        // Retain the previous proxies for comparison, outside the contact-toggle hierarchy.
        var legacy = rotor.Find("LegacyToothColliders");
        if (!legacy) legacy = Child(rotor, "LegacyToothColliders");
        for (int i = colliders.childCount - 1; i >= 0; i--)
        {
            var child = colliders.GetChild(i);
            if (!child.name.StartsWith("ToothCollider_", StringComparison.Ordinal)) continue;
            Undo.SetTransformParent(child, legacy, "Archive old tooth collider");
        }
        Undo.RecordObject(legacy.gameObject, "Disable old tooth colliders");
        legacy.gameObject.SetActive(false);
        var profile = Child(colliders, ProfileName);
        if (!colliders.GetComponent<LeverCamSmoothContact>())
            Undo.AddComponent<LeverCamSmoothContact>(colliders.gameObject);
        var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);
        if (!AssetDatabase.LoadMainAssetAtPath(MeshPath))
            AssetDatabase.CreateAsset(new Mesh { name = "CamProfileMeshes" }, MeshPath);
        var saved = AssetDatabase.LoadAllAssetsAtPath(MeshPath).OfType<Mesh>().ToDictionary(m => m.name);
        int count = 0;
        for (int i = 0; i < simplified.Count - 1; i++)
        {
            var a = simplified[i];
            var b = simplified[i + 1];
            // Each piece is a closed convex annular wedge; the outside edge follows the model.
            var mesh = Wedge(a, b, "CamSegment_" + i.ToString("000"));
            if (saved.TryGetValue(mesh.name, out var oldMesh))
            {
                // Replace the mesh object to invalidate persisted PhysX convex cooking data.
                AssetDatabase.RemoveObjectFromAsset(oldMesh);
                UnityEngine.Object.DestroyImmediate(oldMesh, true);
            }
            AssetDatabase.AddObjectToAsset(mesh, MeshPath);
            var child = Child(profile, mesh.name);
            // Cook at millimetre scale so PhysX hull tolerances do not collapse narrow wedges.
            child.localScale = Vector3.one * 0.001f;
            var collider = Undo.AddComponent<MeshCollider>(child.gameObject);
            collider.convex = true;
            collider.sharedMesh = mesh;
            collider.sharedMaterial = material;
            collider.contactOffset = 0.00005f;
            count++;
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(rotor.gameObject.scene);
        Debug.Log($"Cam profile: {outline.Count} outline points -> {count} convex colliders, chord tolerance {ChordTolerance * 1000f:F3} mm.");
        return true;
    }

    static float OutlineAngle(Vector2 point)
    {
        float angle = Mathf.Atan2(point.y, point.x) * Mathf.Rad2Deg;
        return angle < 100f ? angle + 360f : angle;
    }

    static void Simplify(List<Vector2> points, int first, int last, List<Vector2> output)
    {
        float largest = ChordTolerance;
        int split = -1;
        var edge = points[last] - points[first];
        for (int i = first + 1; i < last; i++)
        {
            float t = Mathf.Clamp01(Vector2.Dot(points[i] - points[first], edge) / edge.sqrMagnitude);
            float error = Vector2.Distance(points[i], points[first] + edge * t);
            if (error > largest) { largest = error; split = i; }
        }
        if (split >= 0)
        {
            Simplify(points, first, split, output);
            Simplify(points, split, last, output);
        }
        else output.Add(points[last]);
    }

    static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create cam collider");
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static Mesh Wedge(Vector2 a, Vector2 b, string name)
    {
        // The inner boundary is internal to the solid cam, away from the axle and contact surface.
        var ring = new[] { a.normalized * 0.012f, a, b, b.normalized * 0.012f };
        var vertices = new Vector3[8];
        for (int i = 0; i < 4; i++)
        {
            vertices[i] = new Vector3(ring[i].x, ring[i].y, -HalfWidth) * 1000f;
            vertices[i + 4] = new Vector3(ring[i].x, ring[i].y, HalfWidth) * 1000f;
        }
        var faces = new List<int> { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 };
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            faces.AddRange(new[] { i, next, next + 4, i, next + 4, i + 4 });
        }
        var mesh = new Mesh { name = name, vertices = vertices, triangles = faces.ToArray() };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
