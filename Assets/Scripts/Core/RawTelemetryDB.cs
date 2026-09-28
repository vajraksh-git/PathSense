using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class RawTelemetryData
{
    public List<Vector3> points = new List<Vector3>();
}

public static class RawTelemetryDB
{
    private static string path = "path_data/raw_telemetry.json";
    private static RawTelemetryData dataCache = null;

    public static void LogPoint(Vector3 point)
    {
        if (dataCache == null) Load();
        dataCache.points.Add(point);
        Save();
    }

    public static RawTelemetryData Load()
    {
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                dataCache = JsonUtility.FromJson<RawTelemetryData>(json);
            }
            catch 
            {
                // In case of corruption or empty file
            }
        }
        
        if (dataCache == null) 
        {
            dataCache = new RawTelemetryData();
        }
        
        return dataCache;
    }

    private static void Save()
    {
        string json = JsonUtility.ToJson(dataCache, true);
        File.WriteAllText(path, json);
    }
    
    public static void Clear()
    {
        dataCache = new RawTelemetryData();
        Save();
    }
}
