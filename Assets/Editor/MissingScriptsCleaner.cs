using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MissingScriptsCleaner
{
    [MenuItem("Tools/Project/Clean Missing Scripts/Active Scene")]
    private static void CleanActiveScene()
    {
        var scene = SceneManager.GetActiveScene();
        if (!scene.isLoaded)
        {
            Debug.LogWarning("[MissingScriptsCleaner] Active scene is not loaded.");
            return;
        }

        var removed = RemoveMissingScriptsInScene(scene);
        if (removed > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }

        Debug.Log($"[MissingScriptsCleaner] Active Scene: removed {removed} missing script component(s).");
    }

    [MenuItem("Tools/Project/Clean Missing Scripts/Open Scenes")]
    private static void CleanOpenScenes()
    {
        var totalRemoved = 0;
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            var removed = RemoveMissingScriptsInScene(scene);
            totalRemoved += removed;

            if (removed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                Debug.Log($"[MissingScriptsCleaner] {scene.path}: removed {removed} missing script component(s).");
            }
        }

        Debug.Log($"[MissingScriptsCleaner] Open Scenes: removed {totalRemoved} missing script component(s) total.");
    }

    [MenuItem("Tools/Project/Clean Missing Scripts/Prefabs In Project")]
    private static void CleanPrefabsInProject()
    {
        var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        var totalRemoved = 0;
        var totalPrefabsChanged = 0;

        for (var i = 0; i < prefabGuids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var removed = RemoveMissingScriptsInGameObjectHierarchy(root);
                if (removed > 0)
                {
                    totalRemoved += removed;
                    totalPrefabsChanged++;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    Debug.Log($"[MissingScriptsCleaner] {path}: removed {removed} missing script component(s).");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        Debug.Log($"[MissingScriptsCleaner] Prefabs: changed {totalPrefabsChanged}, removed {totalRemoved} missing script component(s) total.");
    }

    private static int RemoveMissingScriptsInScene(Scene scene)
    {
        var roots = scene.GetRootGameObjects();
        var removed = 0;
        foreach (var root in roots)
        {
            removed += RemoveMissingScriptsInGameObjectHierarchy(root);
        }

        return removed;
    }

    private static int RemoveMissingScriptsInGameObjectHierarchy(GameObject root)
    {
        var removed = 0;
        var stack = new Stack<Transform>();
        stack.Push(root.transform);

        while (stack.Count > 0)
        {
            var t = stack.Pop();
            for (var i = t.childCount - 1; i >= 0; i--)
            {
                stack.Push(t.GetChild(i));
            }

            removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        }

        return removed;
    }
}

