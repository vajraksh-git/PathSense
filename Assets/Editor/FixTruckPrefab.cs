using UnityEngine;
using UnityEditor;

public class FixTruckPrefab : EditorWindow
{
    [MenuItem("PathSense/Rebuild Detailed Truck")]
    public static void RebuildTruck()
    {
        string path = "Assets/TruckPrefab.prefab";
        
        GameObject root = new GameObject("TruckPrefab");
        
        // Body
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.parent = root.transform;
        body.transform.localPosition = new Vector3(0, 1.5f, 0);
        body.transform.localScale = new Vector3(2.5f, 2f, 6f);
        
        // Cab
        GameObject cab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cab.name = "Cab";
        cab.transform.parent = root.transform;
        cab.transform.localPosition = new Vector3(0, 3f, 1.5f);
        cab.transform.localScale = new Vector3(2.5f, 1.5f, 2f);

        // Wheels
        Vector3[] wheelPositions = {
            new Vector3(-1.5f, 0.75f, 2f),
            new Vector3(1.5f, 0.75f, 2f),
            new Vector3(-1.5f, 0.75f, -2f),
            new Vector3(1.5f, 0.75f, -2f)
        };

        foreach (Vector3 pos in wheelPositions)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = "Wheel";
            wheel.transform.parent = root.transform;
            wheel.transform.localPosition = pos;
            wheel.transform.localScale = new Vector3(1.5f, 0.5f, 1.5f);
            wheel.transform.localRotation = Quaternion.Euler(0, 0, 90);
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        
        Material yellowMat = new Material(shader) { color = Color.yellow };
        Material blackMat = new Material(shader) { color = new Color(0.1f, 0.1f, 0.1f) };

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        foreach (Renderer r in renderers)
        {
            if (r.gameObject.name == "Wheel")
                r.material = blackMat;
            else
                r.material = yellowMat;
        }

        PrefabUtility.SaveAsPrefabAsset(root, path);
        DestroyImmediate(root);
        Debug.Log("Detailed Truck Prefab saved to " + path);
    }
}
