using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SceneCleanup
{
    [MenuItem("PathSense/Clean Scene And Fix Shaders")]
    public static void CleanAndFix()
    {
        // Find and DestroyImmediate the GroundStationController GameObject
        GameObject oldController = GameObject.Find("GroundStationController");
        if (oldController != null)
        {
            GameObject.DestroyImmediate(oldController);
        }

        // Ensure GroundStationTwinController is attached ONLY to FleetManager
        GameObject fleetManager = GameObject.Find("FleetManager");
        if (fleetManager != null)
        {
            var controller = fleetManager.GetComponent<GroundStationTwinController>();
            if (controller == null)
            {
                fleetManager.AddComponent<GroundStationTwinController>();
            }
        }

        // Fix Shaders on TruckPrefab
        string prefabPath = "Assets/TruckPrefab.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab != null)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit != null)
            {
                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in renderers)
                {
                    if (r.sharedMaterial != null)
                    {
                        r.sharedMaterial.shader = urpLit;
                    }
                }
                EditorUtility.SetDirty(prefab);
                AssetDatabase.SaveAssets();
                Debug.Log("Fixed shaders on TruckPrefab.");
            }
        }
        
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("Scene cleaned up and shaders fixed!");
    }
}
