using System;

// GeoTransform — Relative GPS positioning for PathSense Digital Twin
//
// ARCHITECTURE: Dynamic First-Fix Anchoring
// ─────────────────────────────────────────────────────────────────────────
// There are NO hardcoded coordinates anywhere in this file.
// This system works on any planet, any parking lot, any session.
//
// HOW IT WORKS:
//   Step 1 — First BASE_STATION packet arrives (base station phone has GPS fix).
//             That lat/lng is locked as the ORIGIN: Unity (0, 0, 0).
//             Nothing moves in Unity until this step completes.
//
//   Step 2 — Subsequent BASE_STATION packets arrive every 1Hz.
//             We measure how far the live reading has drifted from the
//             locked origin. This drift IS the atmospheric GPS error.
//
//   Step 3 — Truck packet arrives (TRUCK_XXXX,lat,lng,...).
//             Compute truck offset from origin → subtract the measured drift.
//             Because base station and truck phone are <100m apart, they
//             experience identical atmospheric bias. Subtracting the anchor
//             drift cancels the common-mode error on the truck too.
//
// RESULT:
//   • Base station always stays at (0, 0, 0) in Unity
//   • Truck at the same physical spot as base station → Unity (0, 0, 0) ✅
//   • Truck 50m north of base station → Unity (0, 0, 50) ✅
//   • GPS atmospheric drift shifts both phones 5m east → cancels to (0,0) ✅
//   • Saved road_network.json stores LOCAL Unity coords — valid anywhere ✅
//   • No survey, no Google Maps, no hardcoded anything ✅

public static class GeoTransform
{
    private const double MetersPerDegree = 111320.0;

    // ── ORIGIN LOCK (set once from the first BASE_STATION packet) ──────────
    // This is the GPS coordinate of the base station at session start.
    // All subsequent positions are measured RELATIVE to this point.
    private static double _originLat = 0.0;
    private static double _originLng = 0.0;
    private static bool _originLocked = false;

    // ── LIVE DRIFT TRACKING (updated every BASE_STATION packet after lock) ──
    // Measures how far the base station phone GPS has drifted from the locked
    // origin. Applied as a correction to every truck position.
    private static double _liveAnchorLat = 0.0;
    private static double _liveAnchorLng = 0.0;
    private static bool _liveAnchorReady = false;

    /// Returns true once the first BASE_STATION fix has locked the origin.
    public static bool IsOriginLocked => _originLocked;

    /// Called on the FIRST valid BASE_STATION packet to lock the world origin.
    /// After this, (0, 0, 0) in Unity = this GPS coordinate in the real world.
    public static void LockOriginFromFirstFix(double lat, double lng)
    {
        if (_originLocked) return;
        if (lat == 0.0 || lng == 0.0) return;

        _originLat = lat;
        _originLng = lng;
        _liveAnchorLat = lat;
        _liveAnchorLng = lng;
        _liveAnchorReady = true;
        _originLocked = true;

        UnityEngine.Debug.Log($"[GeoTransform] Origin locked: ({lat:F6}, {lng:F6}) = Unity (0, 0, 0). " +
                              "Relative positioning active. All trucks will spawn near origin.");
    }

    /// Called on every subsequent BASE_STATION packet to track atmospheric drift.
    /// The drift vector is subtracted from all truck positions as a DGPS correction.
    public static void UpdateLiveAnchor(double lat, double lng)
    {
        if (!_originLocked) return;
        if (lat == 0.0 || lng == 0.0) return;

        _liveAnchorLat = lat;
        _liveAnchorLng = lng;
        _liveAnchorReady = true;

        // Log the measured drift for debugging (shows GPS quality in real-time)
        double drift = GetDgpsErrorMeters();
        if (drift > 5.0) // Only log if drift is significant
            UnityEngine.Debug.Log($"[GeoTransform] Atmospheric drift: {drift:F1}m (DGPS active)");
    }

    /// Convert truck GPS to local Unity metres with DGPS drift correction applied.
    /// This is the ONLY method that should be called for truck position.
    /// Returns (0, 0) if origin is not yet locked (base station not yet seen).
    public static (double x, double z) GpsToLocalWithDgps(double lat, double lng)
    {
        if (!_originLocked) return (0, 0);

        // Raw offset: how far is the truck from the locked origin?
        double latRad = _originLat * Math.PI / 180.0;
        double rawX = (lng - _originLng) * MetersPerDegree * Math.Cos(latRad);
        double rawZ = (lat - _originLat) * MetersPerDegree;

        // Drift correction: how far has the live anchor drifted from the locked origin?
        // (This is the atmospheric GPS error shared by both phones.)
        double driftX = 0, driftZ = 0;
        if (_liveAnchorReady)
        {
            driftX = (_liveAnchorLng - _originLng) * MetersPerDegree * Math.Cos(latRad);
            driftZ = (_liveAnchorLat - _originLat) * MetersPerDegree;
        }

        // Corrected position = raw truck position - common atmospheric drift
        return (rawX - driftX, rawZ - driftZ);
    }

    /// Raw GPS → local metres from origin (no drift correction).
    /// Use only for debugging or internal checks.
    public static (double x, double z) GpsToLocalMeters(double lat, double lng)
    {
        if (!_originLocked) return (0, 0);
        double latRad = _originLat * Math.PI / 180.0;
        double x = (lng - _originLng) * MetersPerDegree * Math.Cos(latRad);
        double z = (lat - _originLat) * MetersPerDegree;
        return (x, z);
    }

    public static (double lat, double lng) LocalMetersToGps(double x, double z)
    {
        if (!_originLocked) return (0, 0);
        double latRad = _originLat * Math.PI / 180.0;
        double lat = (z / MetersPerDegree) + _originLat;
        double lng = (x / (MetersPerDegree * Math.Cos(latRad))) + _originLng;
        return (lat, lng);
    }

    /// Returns the current atmospheric drift distance in metres (DGPS quality indicator).
    public static float GetDgpsErrorMeters()
    {
        if (!_liveAnchorReady || !_originLocked) return 0f;
        double latRad = _originLat * Math.PI / 180.0;
        double dx = (_liveAnchorLng - _originLng) * MetersPerDegree * Math.Cos(latRad);
        double dz = (_liveAnchorLat - _originLat) * MetersPerDegree;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    /// Reset everything (call if you want a fresh session without restarting Unity).
    public static void ResetOriginLock()
    {
        _originLocked = false;
        _liveAnchorReady = false;
        _originLat = _originLng = 0.0;
        _liveAnchorLat = _liveAnchorLng = 0.0;
        UnityEngine.Debug.Log("[GeoTransform] Origin lock reset. Waiting for next BASE_STATION fix.");
    }

    // ── Legacy compatibility shims ──────────────────────────────────────────
    // Kept so nothing else in the project breaks. Delegate to new methods.
    public static double AnchorTrueLat => _originLat;
    public static double AnchorTrueLng => _originLng;
    public static void SetAnchorFromLiveGPS(double lat, double lng) => LockOriginFromFirstFix(lat, lng);
    public static void ResetAnchorLock() => ResetOriginLock();
    public static (double x, double z) ComputeDgpsCorrection(double lat, double lng) => (0, 0); // Now internal

    public static bool SanityCheck() => _originLocked;
}
