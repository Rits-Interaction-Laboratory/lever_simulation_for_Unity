using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opens the shared lever scene once per local checkout.</summary>
[InitializeOnLoad]
internal static class LeverProjectStartup
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string MarkerPath = "Library/LeverProjectStartup-v1";

    static LeverProjectStartup()
    {
        EditorApplication.delayCall += InitializeCheckout;
    }

    static void InitializeCheckout()
    {
        if (File.Exists(MarkerPath) || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += InitializeCheckout;
            return;
        }
        // Preserve existing work and explicitly opened scenes.
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isDirty || (!string.IsNullOrEmpty(scene.path) && scene.path != ScenePath))
                return;
        }
        if (!File.Exists(ScenePath)) return;
        if (SceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EditorApplication.delayCall += RestoreView;
    }

    static void RestoreView()
    {
        if (SceneManager.GetActiveScene().path != ScenePath) return;
        SharedSceneView.Restore();
        Directory.CreateDirectory("Library");
        File.WriteAllText(MarkerPath, "Initialized shared lever scene and view.\n");
    }
}
