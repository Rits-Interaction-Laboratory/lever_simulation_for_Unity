using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Shares Scene view framing without changing scene objects.</summary>
public static class SharedSceneView
{
    private const string MenuRoot = "Tools/Scene View/";

    [Serializable]
    private sealed class ViewData
    {
        public int version = 1;
        public string sceneGuid;
        public Vector3 pivot;
        public Quaternion rotation;
        public float size;
        public bool orthographic;
        public bool in2DMode;
        public float fieldOfView;
    }

    private static string SettingsPath(out string guid)
    {
        var scene = SceneManager.GetActiveScene();
        guid = AssetDatabase.AssetPathToGUID(scene.path);
        if (string.IsNullOrEmpty(guid))
            throw new InvalidOperationException("Save the active scene before saving or restoring its view.");
        return Path.Combine(Application.dataPath, "../ProjectSettings/SharedSceneViews", guid + ".json");
    }

    [MenuItem(MenuRoot + "Save Shared View")]
    public static void Save()
    {
        try
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
                throw new InvalidOperationException("Open a Scene view first.");
            var path = SettingsPath(out var guid);
            var data = new ViewData
            {
                sceneGuid = guid,
                pivot = view.pivot,
                rotation = view.rotation,
                size = view.size,
                orthographic = view.orthographic,
                in2DMode = view.in2DMode,
                fieldOfView = view.cameraSettings.fieldOfView
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(data, true) + "\n");
            Debug.Log("Shared Scene view saved: " + Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            Debug.LogError("Could not save Scene view: " + ex.Message);
        }
    }

    [MenuItem(MenuRoot + "Restore Shared View")]
    public static void Restore()
    {
        try
        {
            var path = SettingsPath(out var guid);
            if (!File.Exists(path))
                throw new InvalidOperationException("No shared view exists for the active scene. Use Save Shared View first.");
            var data = JsonUtility.FromJson<ViewData>(File.ReadAllText(path));
            if (data == null || data.version != 1 || data.sceneGuid != guid ||
                !Finite(data.size) || data.size <= 0 ||
                !Finite(data.fieldOfView) || data.fieldOfView < 4 || data.fieldOfView > 120 ||
                !Finite(data.pivot.x) || !Finite(data.pivot.y) || !Finite(data.pivot.z) ||
                !Finite(data.rotation.x) || !Finite(data.rotation.y) ||
                !Finite(data.rotation.z) || !Finite(data.rotation.w) ||
                Quaternion.Dot(data.rotation, data.rotation) < 0.0001f)
                throw new InvalidOperationException("The shared view settings are invalid.");
            var view = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            view.in2DMode = data.in2DMode;
            var cameraSettings = view.cameraSettings;
            cameraSettings.fieldOfView = data.fieldOfView;
            view.cameraSettings = cameraSettings;
            view.LookAt(data.pivot, data.rotation.normalized, data.size, data.orthographic, true);
            view.Repaint();
            Debug.Log("Shared Scene view restored.");
        }
        catch (Exception ex)
        {
            Debug.LogError("Could not restore Scene view: " + ex.Message);
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
