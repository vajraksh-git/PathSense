using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class MultiCameraRig : MonoBehaviour
{
    private Camera mapCam;
    private Camera dashCam;
    private GameObject targetTruck;

    void Start()
    {
        // Setup Map Camera (Ground Control - Left Half)
        mapCam = GetComponent<Camera>();
        mapCam.rect = new Rect(0, 0, 0.5f, 1f);
        mapCam.orthographic = true;
        mapCam.orthographicSize = 150f;
        mapCam.clearFlags = CameraClearFlags.SolidColor;
        mapCam.backgroundColor = new Color(0.4f, 0.6f, 0.9f); // Sky blue
        mapCam.farClipPlane = 10000f;
        
        // Task 1: Static Ground Control Camera
        mapCam.transform.position = new Vector3(0, 150f, 0);
        mapCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        
        int dashcamOnlyLayer = LayerMask.NameToLayer("DashcamOnly");
        if (dashcamOnlyLayer != -1)
        {
            mapCam.cullingMask &= ~(1 << dashcamOnlyLayer);
        }
        
        // Setup Dashcam (Truck Dashcam - Right Half)
        GameObject dashCamGO = new GameObject("Dashcam");
        dashCam = dashCamGO.AddComponent<Camera>();
        dashCam.rect = new Rect(0.5f, 0, 0.5f, 1f);
        dashCam.clearFlags = CameraClearFlags.SolidColor;
        dashCam.backgroundColor = new Color(0.4f, 0.6f, 0.9f);
        dashCam.farClipPlane = 10000f;
        
        // The Culling Magic: Dashcam explicitly ignores the Heatmap layer
        int heatmapLayer = LayerMask.NameToLayer("Heatmap");
        if (heatmapLayer != -1)
        {
            dashCam.cullingMask = ~(1 << heatmapLayer);
        }
        else
        {
            Debug.LogWarning("Heatmap layer not found! Please add it in Unity Tags and Layers.");
        }
        
        RenderSettings.fog = false;
    }

    void Update()
    {
        if (targetTruck == null)
        {
            // Dynamically find the first active truck regardless of its ID.
            // Works for TRUCK_SIM001 (sim), TRUCK_XXXXXX (LoRa relay), and SELF_USB (direct USB).
            foreach (var go in GameObject.FindObjectsOfType<GameObject>())
            {
                if ((go.name.StartsWith("TRUCK_") || go.name.StartsWith("SELF_")) && go.activeInHierarchy)
                {
                    targetTruck = go;
                    break;
                }
            }

            if (targetTruck != null)
            {
                // Parent Dashcam to the front of the truck's hood
                dashCam.transform.SetParent(targetTruck.transform);
                dashCam.transform.localPosition = new Vector3(0, 2.5f, 3.0f);
                dashCam.transform.localRotation = Quaternion.identity;
                Debug.Log($"[MultiCameraRig] Dashcam locked to: {targetTruck.name}");
            }
            return;
        }

        // MapCam is now static and no longer follows the truck.

        // Scroll-wheel zoom for Ground Control Map — New Input System version.
        // Mouse.current.scroll returns values like ±120 per notch, so multiply by 0.001f
        // to normalize to the same fractional scale that legacy Input.GetAxis used.
        if (mapCam != null && Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y * 0.001f;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                if (mapCam.orthographic)
                {
                    mapCam.orthographicSize -= scroll * 15f;
                    mapCam.orthographicSize = Mathf.Clamp(mapCam.orthographicSize, 5f, 200f);
                }
                else
                {
                    Vector3 pos = mapCam.transform.position;
                    pos.y -= scroll * 50f;
                    pos.y = Mathf.Clamp(pos.y, 10f, 300f);
                    mapCam.transform.position = pos;
                }
            }
        }
        // Keyboard Zoom Controls — held for continuous smooth zoom.
        // Z / Minus = Zoom Out   |   X / Equals(+) = Zoom In
        if (Keyboard.current != null && mapCam != null)
        {
            float keyZoom = 0f;
            if (Keyboard.current.zKey.isPressed || Keyboard.current.minusKey.isPressed)
                keyZoom = +25f * Time.deltaTime;   // positive = zoom out (increase orthSize)
            if (Keyboard.current.xKey.isPressed || Keyboard.current.equalsKey.isPressed)
                keyZoom = -25f * Time.deltaTime;   // negative = zoom in  (decrease orthSize)

            if (Mathf.Abs(keyZoom) > 0f)
            {
                if (mapCam.orthographic)
                {
                    mapCam.orthographicSize += keyZoom;
                    mapCam.orthographicSize = Mathf.Clamp(mapCam.orthographicSize, 5f, 200f);
                }
                else
                {
                    Vector3 pos = mapCam.transform.position;
                    pos.y += keyZoom * 2f;   // scale up slightly for perspective feel
                    pos.y = Mathf.Clamp(pos.y, 10f, 300f);
                    mapCam.transform.position = pos;
                }
            }
        }
    }
}
