using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public struct MapPoint
{
    public double x;
    public double z;

    public MapPoint(double x, double z)
    {
        this.x = x;
        this.z = z;
    }
}

[Serializable]
public class RoadSegment
{
    public string id;
    public List<MapPoint> points;
}

[Serializable]
public class PendingCandidate
{
    public string id;
    public List<MapPoint> points;
    public int passCount;
    public int requiredPasses;
    public string firstSeenUtc;
    public string lastSeenUtc;
    public bool readyForReview;
}

[Serializable]
public class RoadNetwork
{
    public string lastUpdatedUtc;
    public List<RoadSegment> confirmedRoads = new List<RoadSegment>();
    public List<PendingCandidate> pendingCandidates = new List<PendingCandidate>();

    public void Save(string path)
    {
        // Update timestamp in ISO-8601 format
        lastUpdatedUtc = DateTime.UtcNow.ToString("o"); 
        
        string json = JsonUtility.ToJson(this, true); // true for pretty print
        File.WriteAllText(path, json);
    }

    public static RoadNetwork Load(string path)
    {
        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            RoadNetwork network = JsonUtility.FromJson<RoadNetwork>(json);
            if (network != null)
            {
                return network;
            }
        }
        // Return an empty network if file doesn't exist or is invalid
        return new RoadNetwork(); 
    }

    public static void Save(List<Vector3> confirmedPoints, string path)
    {
        RoadNetwork network = Load(path);
        RoadSegment newSegment = new RoadSegment();
        newSegment.id = "Road_" + System.Guid.NewGuid().ToString().Substring(0, 8);
        newSegment.points = new List<MapPoint>();
        
        foreach (Vector3 pt in confirmedPoints)
        {
            newSegment.points.Add(new MapPoint(pt.x, pt.z));
        }
        
        network.confirmedRoads.Add(newSegment);
        network.Save(path);
    }
}
