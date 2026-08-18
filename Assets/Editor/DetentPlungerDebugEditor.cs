using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(DetentPlungerDebug))]
public sealed class DetentPlungerDebugEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var debug = (DetentPlungerDebug)target;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Detent Runtime Monitor", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.FloatField("Current Compression [m]", debug.CurrentCompressionMeters);
            EditorGUILayout.FloatField("Estimated Spring Force [N]", debug.EstimatedSpringForceNewtons);
            EditorGUILayout.FloatField("Actual Joint Force [N]", debug.CurrentJointForceNewtons);
            EditorGUILayout.FloatField("Applied Spring Force [N]", debug.AppliedSpringForceNewtons);
            EditorGUILayout.FloatField("Local Y Joint Force [N]", debug.LocalYJointForceNewtons);
            EditorGUILayout.FloatField("Local Y Velocity [m/s]", debug.LocalYVelocityMetersPerSecond);
            EditorGUILayout.Toggle("Rigidbody Is Sleeping", debug.IsBodySleeping);
            EditorGUILayout.IntField("Gear Contact Count", debug.ContactingGearColliderCount);
            EditorGUILayout.LabelField("Contacting Gear Colliders");
            EditorGUILayout.HelpBox(debug.ContactingGearColliderNames, MessageType.None);
            EditorGUILayout.LabelField("Contacting Collider Pairs");
            EditorGUILayout.HelpBox(debug.ContactingColliderPairs, MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Joint Y Position Diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.FloatField("Anchor Separation Y [m]", debug.JointAnchorSeparationY);
            EditorGUILayout.FloatField("Displacement From Play Start [m]", debug.JointYDisplacementFromPlayStart);
            EditorGUILayout.FloatField("Target Position Y [m]", debug.JointTargetPositionY);
            EditorGUILayout.FloatField("Y Drive Error [m]", debug.JointYDriveError);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gear Collision Test", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            string buttonLabel = debug.AreGearCollidersEnabled
                ? "Temporarily Disable Gear Colliders"
                : "Re-enable Gear Colliders";
            if (GUILayout.Button(buttonLabel))
                debug.SetGearCollidersEnabled(!debug.AreGearCollidersEnabled);
        }
        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("This diagnostic button is available in Play mode.", MessageType.Info);

        if (Application.isPlaying)
            Repaint();
    }
}

public static class DetentPlungerDebugReferenceRepair
{
    [InitializeOnLoadMethod]
    static void ScheduleRepair()
    {
        EditorApplication.delayCall += RepairOpenScene;
    }

    static void RepairOpenScene()
    {
        foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (candidate.name != "DetentPlunger" || !candidate.scene.IsValid())
                continue;

            bool changed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(candidate) > 0;
            if (!candidate.TryGetComponent<DetentPlungerDebug>(out _))
            {
                candidate.AddComponent<DetentPlungerDebug>();
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(candidate);
                EditorSceneManager.MarkSceneDirty(candidate.scene);
                EditorSceneManager.SaveScene(candidate.scene);
            }
        }
    }
}
