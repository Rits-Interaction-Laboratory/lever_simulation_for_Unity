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
        var lever = (LeverRotorTorqueTest)target;

        DrawContactDamperStatus(lever);
        EditorGUILayout.Space();
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

    static void DrawContactDamperStatus(LeverRotorTorqueTest lever)
    {
        EditorGUILayout.LabelField("Contact Damper Status", EditorStyles.boldLabel);

        string status;
        MessageType messageType;
        if (!Application.isPlaying)
        {
            status = "NOT RUNNING - Enter Play mode to monitor the damper.";
            messageType = MessageType.None;
        }
        else if (lever.IsContactDampingActing)
        {
            status = "ACTIVE - The contact damper is applying torque.";
            messageType = MessageType.Info;
        }
        else if (lever.IsContactDampingEngaged)
        {
            status = "ENGAGED - Damping is ready, but the lever is not moving.";
            messageType = MessageType.Warning;
        }
        else
        {
            status = "INACTIVE - The contact damper is not engaged.";
            messageType = MessageType.None;
        }

        EditorGUILayout.HelpBox(status, messageType);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Toggle("Applying Damping Torque", lever.IsContactDampingActing);
            EditorGUILayout.Slider("Contact Strength", lever.CurrentContactDampingStrength, 0f, 1f);
            EditorGUILayout.FloatField(
                "Current Coefficient [N m s/rad]", lever.CurrentContactDampingCoefficient);
            EditorGUILayout.FloatField("Current Torque [N m]", lever.CurrentContactDampingTorque);
        }
    }
}
