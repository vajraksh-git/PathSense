using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(GroundStationTwinController))]
public class GroundStationTwinHUD : MonoBehaviour
{
    private GroundStationTwinController controller;
    private string notificationMsg = "";
    private float notificationTimer = 0f;

    void Start()
    {
        controller = GetComponent<GroundStationTwinController>();
    }

    void OnGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 150), "PathSense Consensus Engine");

        if (controller != null && controller.pendingPoints != null)
        {
            GUI.Label(new Rect(20, 40, 280, 30), $"Pending Unconfirmed Points: {controller.pendingPoints.Count}");

            GUI.enabled = controller.pendingPoints.Count > 5;
            if (GUI.Button(new Rect(20, 80, 260, 40), "APPROVE PENDING ROAD"))
            {
                ApproveRoad();
            }
            GUI.enabled = true;
        }

        if (notificationTimer > 0f)
        {
            // Flash a bright green notification when saved
            GUI.contentColor = Color.green;
            GUI.Label(new Rect(20, 130, 280, 30), notificationMsg);
            GUI.contentColor = Color.white; // Reset
            
            // Time.deltaTime isn't technically perfect in OnGUI, but it works fine for simple fading text
            notificationTimer -= Time.deltaTime;
        }
    }

    private void ApproveRoad()
    {
        // 1. Gather all high-count red cells (5+ hits)
        List<Vector3> approvedPoints = new List<Vector3>();
        foreach (var kvp in controller.occupancyGrid)
        {
            if (kvp.Value >= 3) // hitCount >= 3 = High Confidence (Time-Gated)
            {
                approvedPoints.Add(new Vector3(kvp.Key.x * GroundStationTwinController.CELL_SIZE, 0.0f, kvp.Key.y * GroundStationTwinController.CELL_SIZE));
                
                // Immediately turn the quad solid black and move to Default layer
                if (controller.occupancyQuads.TryGetValue(kvp.Key, out GameObject quad))
                {
                    quad.GetComponent<Renderer>().material.color = Color.black;
                    quad.layer = LayerMask.NameToLayer("Default");
                }
            }
        }

        // 2. Call RoadNetwork.Save to write specifically the high-count voxels to JSON
        RoadNetwork.Save(approvedPoints, controller.sharedNetworkPath);
        
        // 3. Reset raw telemetry
        RawTelemetryDB.Clear();
        controller.pendingPoints.Clear();

        // We do NOT clear the occupancyGrid here anymore, because we want the black quads to stay visible.
        // The newly saved Network will handle overlaps in the future.
        
        // 4. Reload network inside controller to make the Spatial Awareness engine immediately recognize the new road
        controller.Network = RoadNetwork.Load(controller.sharedNetworkPath);

        // 5. Display text notification
        notificationMsg = "Road Saved to JSON Database!";
        notificationTimer = 3.0f;
    }
}
