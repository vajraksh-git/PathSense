using System;

public static class MapMatcher
{
    public static float DistanceToNearestRoad(MapPoint point, RoadNetwork network)
    {
        if (network == null || network.confirmedRoads == null || network.confirmedRoads.Count == 0)
            return float.MaxValue;
            
        float minDistance = float.MaxValue;

        foreach (var road in network.confirmedRoads)
        {
            if (road.points == null || road.points.Count < 2) continue;

            for (int i = 0; i < road.points.Count - 1; i++)
            {
                float dist = DistancePointToSegment(point, road.points[i], road.points[i + 1]);
                if (dist < minDistance)
                {
                    minDistance = dist;
                }
            }
        }

        return minDistance == float.MaxValue ? float.MaxValue : minDistance;
    }

    private static float DistancePointToSegment(MapPoint p, MapPoint v, MapPoint w)
    {
        double l2 = (w.x - v.x) * (w.x - v.x) + (w.z - v.z) * (w.z - v.z);
        if (l2 == 0) return (float)Math.Sqrt((p.x - v.x) * (p.x - v.x) + (p.z - v.z) * (p.z - v.z));
        
        double t = ((p.x - v.x) * (w.x - v.x) + (p.z - v.z) * (w.z - v.z)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        
        double projX = v.x + t * (w.x - v.x);
        double projZ = v.z + t * (w.z - v.z);
        
        return (float)Math.Sqrt((p.x - projX) * (p.x - projX) + (p.z - projZ) * (p.z - projZ));
    }
}
