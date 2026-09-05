using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(GearRotorMassProperties))]
public sealed class GearRotorMassPropertiesEditor : Editor
{
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
