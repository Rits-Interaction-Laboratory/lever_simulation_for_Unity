using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(GearRotorMassProperties))]
public sealed class GearRotorMassPropertiesEditor : Editor
{
    internal static void DrawCenterOfMass(GearRotorMassProperties properties)
    {
        var body = properties.GetComponent<Rigidbody>();
        if (!body)
            return;

        // Show the center actually used by physics, including during Play mode.
        Vector3 center = body.worldCenterOfMass;
        float size = HandleUtility.GetHandleSize(center) * 0.06f;
        Color previousColor = Handles.color;
        var previousZTest = Handles.zTest;
        try
        {
            // Keep the marker visible even when the center is inside the mesh.
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Handles.color = new Color(1f, 0.75f, 0.05f, 1f);
            Handles.SphereHandleCap(0, center, Quaternion.identity, size * 0.5f, EventType.Repaint);
            Handles.DrawLine(center - Vector3.right * size, center + Vector3.right * size);
            Handles.DrawLine(center - Vector3.up * size, center + Vector3.up * size);
            Handles.DrawLine(center - Vector3.forward * size, center + Vector3.forward * size);
            Handles.Label(center + Vector3.up * size * 1.3f, "Center of Mass", EditorStyles.boldLabel);
        }
        finally
        {
            Handles.color = previousColor;
            Handles.zTest = previousZTest;
        }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var properties = (GearRotorMassProperties)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Uniform-Density Mesh Result", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector3Field("Center Of Mass (Local)", properties.CalculatedCenterOfMassLocal);
            EditorGUILayout.Vector3Field("Principal Inertia [kg m^2]", properties.CalculatedPrincipalInertia);
            EditorGUILayout.Vector3Field("Principal Axes (Local Degrees)", properties.CalculatedInertiaTensorRotation.eulerAngles);
            EditorGUILayout.DoubleField("Total Volume [m^3]", properties.CalculatedVolume);
            EditorGUILayout.IntField("Meshes Used", properties.MeshesUsed);
            EditorGUILayout.TextField("Status", properties.CalculationStatus);
        }

        if (GUILayout.Button("Recalculate From Visible Meshes"))
        {
            Undo.RecordObject(properties, "Recalculate GearRotor mass properties");
            properties.RecalculateFromVisibleMeshes();
            EditorUtility.SetDirty(properties);
            EditorUtility.SetDirty(properties.GetComponent<Rigidbody>());
            EditorSceneManager.MarkSceneDirty(properties.gameObject.scene);
        }
    }
}

public static class GearRotorMassPropertiesInstaller
{
    [InitializeOnLoadMethod]
    static void ScheduleInstall()
    {
        EditorApplication.delayCall += InstallAndCalculate;
    }

    static void InstallAndCalculate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        GameObject rotor = GameObject.Find("GearRotor");
        if (!rotor || !rotor.scene.IsValid())
            return;

        var properties = rotor.GetComponent<GearRotorMassProperties>();
        if (!properties)
            properties = rotor.AddComponent<GearRotorMassProperties>();

        if (properties.RecalculateFromVisibleMeshes())
        {
            EditorUtility.SetDirty(properties);
            EditorUtility.SetDirty(rotor.GetComponent<Rigidbody>());
            EditorSceneManager.MarkSceneDirty(rotor.scene);
            EditorSceneManager.SaveScene(rotor.scene);
        }
    }
}

[InitializeOnLoad]
public static class GearRotorCenterOfMassOverlay
{
    static GearRotorCenterOfMassOverlay()
    {
        SceneView.duringSceneGui += DrawSelectedCenterOfMass;
        Selection.selectionChanged += SceneView.RepaintAll;
    }

    static void DrawSelectedCenterOfMass(SceneView sceneView)
    {
        if (Event.current.type != EventType.Repaint)
            return;

        // Draw independently of whether an Inspector is open or the component is folded.
        foreach (GameObject selected in Selection.gameObjects)
        {
            var properties = selected.GetComponent<GearRotorMassProperties>();
            if (properties)
                GearRotorMassPropertiesEditor.DrawCenterOfMass(properties);
        }
    }
}