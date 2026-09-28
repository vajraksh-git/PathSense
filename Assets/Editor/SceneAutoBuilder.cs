using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SceneAutoBuilder
{
    private static Shader GetURPShader()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null && UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null)
        {
            shader = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.defaultShader;
        }
        if (shader == null) shader = Shader.Find("Standard");
        return shader;
    }

    private static void SafelyDestroy(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null)
        {
            GameObject.DestroyImmediate(go);
        }
    }

    [MenuItem("PathSense/Automate Mine Scene")]
    public static void BuildScene()
    {
        // Clean up any existing generated objects to make this idempotent
        SafelyDestroy("Terrain"); // CreateTerrainGameObject names it "Terrain"
        SafelyDestroy("BaseStationAnchor");
        SafelyDestroy("Morning Sun");
        SafelyDestroy("FleetManager");
        SafelyDestroy("GroundStationController"); // Clean up old isolated controller if present

        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            GameObject.DestroyImmediate(mainCam.gameObject);
        }
        SafelyDestroy("Main Camera");

        // 1. Terrain
        TerrainData terrainData = new TerrainData();
        terrainData.size = new Vector3(4000, 500, 4000);
        terrainData.heightmapResolution = 513;
        
        GameObject terrainGO = Terrain.CreateTerrainGameObject(terrainData);
        terrainGO.transform.position = new Vector3(-2000, 0, -2000);
        
        Material dirtMat = new Material(GetURPShader());
        dirtMat.color = new Color(0.25f, 0.15f, 0.05f); // Dark brown dirt
        terrainGO.GetComponent<Terrain>().materialTemplate = dirtMat;

        float[,] heights = new float[terrainData.heightmapResolution, terrainData.heightmapResolution];
        for (int y = 0; y < terrainData.heightmapResolution; y++)
        {
            for (int x = 0; x < terrainData.heightmapResolution; x++)
            {
                float nx = (float)x / (terrainData.heightmapResolution - 1);
                float ny = (float)y / (terrainData.heightmapResolution - 1);
                
                // Distance from center (0 to 1)
                float dx = nx * 2f - 1f;
                float dy = ny * 2f - 1f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                dist = Mathf.Clamp01(dist);
                
                // Tiered crater effect sloping up to edges
                float baseHeight = Mathf.Pow(dist, 2f);
                float tiers = Mathf.Floor(baseHeight * 8f) / 8f;
                
                float noise = Mathf.PerlinNoise(nx * 20f, ny * 20f) * 0.02f;
                heights[y, x] = Mathf.Lerp(baseHeight, tiers, 0.8f) + noise;
            }
        }
        terrainData.SetHeights(0, 0, heights);

        // 2. Base Station Tower
        GameObject baseAnchor = new GameObject("BaseStationAnchor");
        baseAnchor.transform.position = Vector3.zero;

        GameObject baseCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseCube.transform.SetParent(baseAnchor.transform);
        baseCube.transform.localPosition = new Vector3(0, 1, 0);
        baseCube.transform.localScale = new Vector3(4, 2, 4);
        baseCube.GetComponent<Renderer>().sharedMaterial = new Material(GetURPShader()) { color = Color.gray };

        GameObject mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mast.transform.SetParent(baseAnchor.transform);
        mast.transform.localPosition = new Vector3(0, 17, 0); // 2 + 15
        mast.transform.localScale = new Vector3(1, 15, 1); // height is 2 * 15 = 30
        mast.GetComponent<Renderer>().sharedMaterial = new Material(GetURPShader()) { color = Color.gray };

        GameObject pointLightGO = new GameObject("RedBeacon");
        pointLightGO.transform.SetParent(baseAnchor.transform);
        pointLightGO.transform.localPosition = new Vector3(0, 32.5f, 0);
        Light light = pointLightGO.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = Color.red;
        light.range = 500f;
        light.intensity = 5f;

        // 3. CAT 797 Dumper Truck Prefab
        GameObject truckPrefab = new GameObject("TruckPrefab");
        
        Material yellowMat = new Material(GetURPShader()) { color = new Color(1f, 0.8f, 0f) };
        Material blackMat = new Material(GetURPShader()) { color = new Color(0.1f, 0.1f, 0.1f) };

        // Chassis
        GameObject chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chassis.transform.SetParent(truckPrefab.transform);
        chassis.transform.localPosition = new Vector3(0, 2, 0);
        chassis.transform.localScale = new Vector3(6, 2, 10);
        chassis.GetComponent<Renderer>().sharedMaterial = yellowMat;

        // Dump bed
        GameObject bed = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bed.transform.SetParent(truckPrefab.transform);
        bed.transform.localPosition = new Vector3(0, 4.5f, -1.5f);
        bed.transform.localScale = new Vector3(7, 3, 8);
        bed.transform.localRotation = Quaternion.Euler(-15, 0, 0);
        bed.GetComponent<Renderer>().sharedMaterial = yellowMat;

        // Cab
        GameObject cab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cab.transform.SetParent(truckPrefab.transform);
        cab.transform.localPosition = new Vector3(-1.5f, 4f, 3.5f);
        cab.transform.localScale = new Vector3(2, 2.5f, 2);
        cab.GetComponent<Renderer>().sharedMaterial = blackMat;

        // Tires
        Vector3[] tirePositions = new Vector3[]
        {
            new Vector3(-3.5f, 1.5f, 3.5f), new Vector3(3.5f, 1.5f, 3.5f),
            new Vector3(-3.5f, 1.5f, -3.5f), new Vector3(3.5f, 1.5f, -3.5f)
        };
        foreach (var pos in tirePositions)
        {
            GameObject tire = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tire.transform.SetParent(truckPrefab.transform);
            tire.transform.localPosition = pos;
            tire.transform.localRotation = Quaternion.Euler(0, 0, 90);
            tire.transform.localScale = new Vector3(3f, 1f, 3f);
            tire.GetComponent<Renderer>().sharedMaterial = blackMat;
        }

        string prefabPath = "Assets/TruckPrefab.prefab";
        #if UNITY_2018_3_OR_NEWER
        PrefabUtility.SaveAsPrefabAsset(truckPrefab, prefabPath);
        #else
        PrefabUtility.CreatePrefab(prefabPath, truckPrefab);
        #endif
        GameObject.DestroyImmediate(truckPrefab);

        // 4. Environment
        GameObject dirLightGO = new GameObject("Morning Sun");
        Light dirLight = dirLightGO.AddComponent<Light>();
        dirLight.type = LightType.Directional;
        dirLight.color = new Color(1f, 0.95f, 0.85f); // Warm morning
        dirLight.shadows = LightShadows.Soft;
        dirLightGO.transform.rotation = Quaternion.Euler(30, 45, 0);

        GameObject camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        Camera cam = camGO.AddComponent<Camera>();
        camGO.transform.position = new Vector3(0, 600, -500);
        camGO.transform.rotation = Quaternion.Euler(50, 0, 0);

        // 5. Setup
        new GameObject("FleetManager");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        
        Debug.Log("Procedural Mine Scene Built Successfully (Idempotent)!");
    }
}
