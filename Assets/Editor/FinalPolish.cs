using UnityEngine;
using UnityEditor;

public class FinalPolish : EditorWindow
{
    [MenuItem("Tools/Fix Lighting and Sonar")]
    public static void FixLighting()
    {
        GameObject ground = GameObject.Find("SolidGround");
        if (ground != null)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            
            Material mat = new Material(shader);
            mat.color = new Color(0.4f, 0.25f, 0.13f);
            
            // CRITICAL: Set smoothness and metallic to 0 so it stops reflecting the orange skybox
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
            
            ground.GetComponent<Renderer>().material = mat;
            
            Debug.Log("Lighting fixed: SolidGround is now a matte brown floor.");
        }
        else
        {
            Debug.LogWarning("SolidGround not found! Did you run the Nuclear Visual Reset first?");
        }
    }
}
