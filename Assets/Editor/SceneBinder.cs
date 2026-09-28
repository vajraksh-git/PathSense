using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SceneBinder
{
    [MenuItem("PathSense/Wire Up Scene")]
    public static void WireUpScene()
    {
        GameObject fleetManager = GameObject.Find("FleetManager");
        if (fleetManager == null)
        {
            Debug.LogError("FleetManager GameObject not found in the active scene. Please ensure it exists.");
            return;
        }

        // Add GroundStationTwinController if it doesn't exist
        GroundStationTwinController controller = fleetManager.GetComponent<GroundStationTwinController>();
        if (controller == null)
        {
            controller = fleetManager.AddComponent<GroundStationTwinController>();
        }

        // Add GroundStationTwinHUD if it doesn't exist
        GroundStationTwinHUD hud = fleetManager.GetComponent<GroundStationTwinHUD>();
        if (hud == null)
        {
            hud = fleetManager.AddComponent<GroundStationTwinHUD>();
        }

        // Assign the TruckPrefab
        GameObject truckPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TruckPrefab.prefab");
        if (truckPrefab != null)
        {
            controller.truckPrefab = truckPrefab;
        }
        else
        {
            Debug.LogError("Assets/TruckPrefab.prefab not found!");
        }

        // Set explicit values
        controller.portNames = "COM8,COM7,COM21";
        controller.baudRate = 115200;

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("FleetManager successfully wired up with Ground Station components!");
    }
}
