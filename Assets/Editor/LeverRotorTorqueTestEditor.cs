using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LeverRotorTorqueTest))]
public sealed class LeverRotorTorqueTestEditor : Editor
{
    SerializedProperty angleSetMode;
    SerializedProperty targetAngleRadians;

    void OnEnable()
    {
        angleSetMode = serializedObject.FindProperty("angleSetMode");
        targetAngleRadians = serializedObject.FindProperty("targetAngleRadians");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawPropertiesExcluding(serializedObject, "m_Script", "angleSetMode", "targetAngleRadians");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Lever Angle Test", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(angleSetMode, new GUIContent("Angle Set Mode (CSV Off)"));
        EditorGUILayout.PropertyField(targetAngleRadians, new GUIContent("Target Angle [rad]"));
        bool settingsChanged = EditorGUI.EndChangeCheck();
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.HelpBox(
            "Angle Set Mode holds the lever at the specified angle and does not create or write a CSV. " +
            "The controls below only operate in Play mode, so the test pose is not saved to the scene.",
            MessageType.Info);

        var lever = (LeverRotorTorqueTest)target;
        if (settingsChanged && Application.isPlaying)
            lever.SetAngleMode(angleSetMode.boolValue, targetAngleRadians.floatValue);

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            string modeLabel = lever.AngleSetMode
                ? "Release Lever (Angle Mode OFF)"
                : "Set and Hold Angle (CSV OFF)";
            if (GUILayout.Button(modeLabel))
            {
                serializedObject.ApplyModifiedProperties();
                lever.SetAngleMode(!lever.AngleSetMode, targetAngleRadians.floatValue);
                Repaint();
            }

            using (new EditorGUI.DisabledScope(!lever.AngleSetMode))
            {
                if (GUILayout.Button("Apply Angle Again"))
                {
                    serializedObject.ApplyModifiedProperties();
                    lever.SetAngleMode(true, targetAngleRadians.floatValue);
                }
            }
        }

        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Enter Play mode to use the angle controls.", MessageType.None);

        if (Application.isPlaying)
            Repaint();
    }
}
