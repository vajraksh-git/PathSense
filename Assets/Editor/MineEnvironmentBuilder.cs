using UnityEngine;
using UnityEditor;
using System.IO;

public class MineEnvironmentBuilder : EditorWindow
{
    [MenuItem("PathSense/Generate Mine Environment")]
    public static void GenerateMine()
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
        {
            Debug.LogError("No active terrain found in the scene! Please ensure a Unity Terrain exists.");
            return;
        }

        TerrainData td = terrain.terrainData;
        
        // Removed automated shader assignment to prevent Unity version crash.
        // We will assign the terrain material manually.
        
        // 1. Sculpt Geometry: Open-Cast Mine Crater
        int res = td.heightmapResolution;
        float[,] heights = new float[res, res];
        
        // We want the crater centered precisely at world (0, 0, 0) since that's our BaseStation anchor.
        // We need to map world (0,0) to terrain coordinates.
        float terrainX = -terrain.transform.position.x;
        float terrainZ = -terrain.transform.position.z;
        
        float normalizedCenterX = terrainX / td.size.x;
        float normalizedCenterY = terrainZ / td.size.z;
        
        int centerX = Mathf.RoundToInt(normalizedCenterX * (res - 1));
        int centerY = Mathf.RoundToInt(normalizedCenterY * (res - 1));

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(centerX, centerY));
                float maxDist = res / 2f; 
                float normalizedDist = Mathf.Clamp01(dist / maxDist);
                
                // Flat center area, gradually sloping up to form steep walls
                // Dist > 200m (which is 200 / (td.size.x/res) in grid units)
                // Let's use world-space distance directly for clarity
                float worldDist = dist * (td.size.x / res);
                float h = 0f;
                
                if (worldDist > 200f)
                {
                    // Basic crater slope
                    float slopeDist = worldDist - 200f;
                    h = Mathf.Pow(slopeDist / 200f, 1.2f) * 0.15f; 
                    
                    // Add jagged perlin noise mountains on the outer rim
                    float noise = Mathf.PerlinNoise(x * 0.05f, y * 0.05f) * 0.1f;
                    h += noise * (slopeDist / 400f); // Noise gets stronger further out
                }
                
                heights[y, x] = Mathf.Clamp01(h);
            }
        }
        td.SetHeights(0, 0, heights);

        // 2. Painting Layers: Dirt and Road
        Texture2D dirtTex = CreateTexture("Assets/DirtTex.png", new Color(0.6f, 0.4f, 0.2f));
        Texture2D roadTex = CreateTexture("Assets/RoadTex.png", new Color(0.2f, 0.2f, 0.2f));

        TerrainLayer dirtLayer = new TerrainLayer { diffuseTexture = dirtTex, tileSize = new Vector2(20, 20) };
        TerrainLayer roadLayer = new TerrainLayer { diffuseTexture = roadTex, tileSize = new Vector2(20, 20) };
        
        AssetDatabase.CreateAsset(dirtLayer, "Assets/DirtLayer.terrainlayer");
        AssetDatabase.CreateAsset(roadLayer, "Assets/RoadLayer.terrainlayer");
        
        td.terrainLayers = new TerrainLayer[] { dirtLayer, roadLayer };

        // 3. Paint Reference Road via Alphamap
        int amRes = td.alphamapResolution;
        float[,,] alpha = new float[amRes, amRes, 2];
        
        for (int y = 0; y < amRes; y++)
        {
            for (int x = 0; x < amRes; x++)
            {
                // Calculate world position for this pixel
                float worldX = (x / (float)(amRes - 1)) * td.size.x + terrain.transform.position.x;
                float worldZ = (y / (float)(amRes - 1)) * td.size.z + terrain.transform.position.z;
                
                // Draw a simple, straight reference road (Dark Grey) starting from (0,0,0) and moving straight North for 200 meters.
                float expectedX = 0f;
                float distToRoad = Mathf.Abs(worldX - expectedX);
                
                // If within 5 meters of the mathematical spine, paint dark grey road
                if (distToRoad < 5f && worldZ >= 0 && worldZ <= 200f)
                {
                    alpha[y, x, 0] = 0f; // 0% dirt
                    alpha[y, x, 1] = 1f; // 100% road
                }
                else
                {
                    alpha[y, x, 0] = 1f; // 100% dirt
                    alpha[y, x, 1] = 0f; // 0% road
                }
            }
        }
        
        td.SetAlphamaps(0, 0, alpha);

        // 4. Spawn Processing Buildings
        SpawnBuildings();

        AssetDatabase.SaveAssets();
        Debug.Log("Procedural Open-Cast Mine generated perfectly!");
    }
    
    private static void SpawnBuildings()
    {
        // Delete old ones if they exist
        GameObject existingGroup = GameObject.Find("ProcessingFacilities");
        if (existingGroup != null) DestroyImmediate(existingGroup);
        
        GameObject group = new GameObject("ProcessingFacilities");
        
        Vector3[] positions = {
            new Vector3(40, 15, 30),
            new Vector3(40, 10, -20),
            new Vector3(-50, 20, 10),
            new Vector3(-30, 25, -40)
        };
        
        Vector3[] scales = {
            new Vector3(30, 30, 40),
            new Vector3(20, 20, 20),
            new Vector3(40, 40, 30),
            new Vector3(30, 50, 30)
        };

        // Removed automated shader assignment for buildings to prevent crash.

        for (int i = 0; i < 4; i++)
        {
            GameObject bldg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bldg.name = "Building_" + i;
            bldg.transform.position = positions[i];
            bldg.transform.localScale = scales[i];
            bldg.transform.parent = group.transform;
        }
    }
    
    private static Texture2D CreateTexture(string path, Color color)
    {
        Texture2D tex = new Texture2D(2, 2);
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                tex.SetPixel(i, j, color);
            }
        }
        tex.Apply();
        
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
