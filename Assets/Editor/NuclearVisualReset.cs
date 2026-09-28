using UnityEngine;
using UnityEditor;

public class NuclearVisualReset : EditorWindow
{
    [MenuItem("Tools/Nuclear Visual Reset")]
    public static void ResetEnvironment()
    {
        // 1. Kill the Terrain
        GameObject oldTerrain = GameObject.Find("Terrain");
        if (oldTerrain != null)
        {
            DestroyImmediate(oldTerrain);
        }

        // 2. Create Flat Brown Floor
        GameObject ground = GameObject.Find("SolidGround");
        if (ground != null)
        {
            DestroyImmediate(ground);
        }

        ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "SolidGround";
        ground.transform.position = new Vector3(0, -0.5f, 0);
        ground.transform.localScale = new Vector3(10000, 1, 10000);

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Standard");
        Material brownMat = new Material(unlitShader) { color = new Color(0.4f, 0.25f, 0.13f) };
        ground.GetComponent<Renderer>().material = brownMat;

        Debug.Log("Nuclear Visual Reset Complete: Terrain killed, solid brown ground established.");
    }
}
