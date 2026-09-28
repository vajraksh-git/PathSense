using System;

public static class GeoTransform
{
    public const double AnchorTrueLat = 22.319998;
    public const double AnchorTrueLng = 87.298033;
    
    private const double MetersPerDegree = 111320.0;
    
    public static (double x, double z) GpsToLocalMeters(double lat, double lng)
    {
        double latRad = AnchorTrueLat * Math.PI / 180.0;
        double x = (lng - AnchorTrueLng) * MetersPerDegree * Math.Cos(latRad);
        double z = (lat - AnchorTrueLat) * MetersPerDegree;
        return (x, z);
    }
    
    public static (double lat, double lng) LocalMetersToGps(double x, double z)
    {
        double latRad = AnchorTrueLat * Math.PI / 180.0;
        double lat = (z / MetersPerDegree) + AnchorTrueLat;
        double lng = (x / (MetersPerDegree * Math.Cos(latRad))) + AnchorTrueLng;
        return (lat, lng);
    }
    
    public static (double x, double z) ComputeDgpsCorrection(double liveAnchorLat, double liveAnchorLng)
    {
        var local = GpsToLocalMeters(liveAnchorLat, liveAnchorLng);
        return (-local.x, -local.z);
    }
    
    public static bool SanityCheck()
    {
        double testLat = 22.321000;
        double testLng = 87.299000;
        
        var local = GpsToLocalMeters(testLat, testLng);
        var gps = LocalMetersToGps(local.x, local.z);
        
        double tolerance = 1e-6;
        if (Math.Abs(testLat - gps.lat) > tolerance || Math.Abs(testLng - gps.lng) > tolerance)
        {
            return false;
        }
        return true;
    }
}
