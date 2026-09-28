using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class GroundStationTwinController : MonoBehaviour
{
    public string portNames = "COM8,COM7,COM21";
    public int baudRate = 115200;
    public string sharedNetworkPath = "path_data/road_network.json";
    
    public GameObject truckPrefab;
    public bool deskTestMode = false;

    private Material truckMaterial;
    private SerialTelemetryReader _telemetryReader;
    public RoadNetwork Network { get; set; }
    private MapLearningEngine _learningEngine;

    private Dictionary<string, Transform> activeTrucks = new Dictionary<string, Transform>();
    private Dictionary<string, SensorFilter> _sensorFilters = new Dictionary<string, SensorFilter>();
    
    private Dictionary<string, Vector3> _truckTargetPos = new Dictionary<string, Vector3>();
    private Dictionary<string, Quaternion> _truckTargetRot = new Dictionary<string, Quaternion>();
    // Per-truck exponential moving average of GPS position to damp phone GPS jitter (~10-15m jumps)
    private Dictionary<string, UnityEngine.Vector2> _smoothedPos = new Dictionary<string, UnityEngine.Vector2>();

    // Smoothed anchor coordinates — EMA on incoming BASE_STATION packets.
    // Prevents a jittery base station phone GPS from creating a jittery DGPS correction.
    private double _smoothedAnchorLat = 0.0;
    private double _smoothedAnchorLng = 0.0;
    private const double ANCHOR_SMOOTH_ALPHA = 0.1; // 0.1 = heavy smoothing (~10 readings to converge)

    // Per-session GPS calibration offset.
    // Compensates for systematic inter-device GPS bias (two phones at the same
    // physical location reporting different coordinates due to independent chip errors).
    // Press 'C' in Play mode while truck is physically at the base station to calibrate.
    private UnityEngine.Vector2 _gpsCalibrationOffset = UnityEngine.Vector2.zero;

    private float headingOffset = 0f;

    [Header("Legacy IMU Post-Processing")]
    public float gyroNoiseThreshold = 4.0f;
    private const int TOTAL_CALIBRATION_SAMPLES = 50;
    
    // Per-truck state for legacy IMU
    private Dictionary<string, float> _legacyGyroBias = new Dictionary<string, float>();
    private Dictionary<string, int> _legacyCalibSamples = new Dictionary<string, int>();
    private Dictionary<string, float> _legacyAccumBias = new Dictionary<string, float>();
    private Dictionary<string, bool> _legacyIsCalibrated = new Dictionary<string, bool>();
    private Dictionary<string, float> _legacyCurrentHeading = new Dictionary<string, float>();
    private Dictionary<string, float> _legacyLastYawRate = new Dictionary<string, float>();
    private Dictionary<string, bool> _isAbsoluteHeading = new Dictionary<string, bool>();

    // Time-Gated Consensus Grid
    public class CellData
    {
        public int hitCount = 0;
        public Dictionary<string, float> lastHitTimePerTruck = new Dictionary<string, float>();
    }
    
    [System.NonSerialized]
    public Dictionary<Vector2Int, CellData> _gridMap = new Dictionary<Vector2Int, CellData>();
    [System.NonSerialized]
    public Dictionary<Vector2Int, int> occupancyGrid = new Dictionary<Vector2Int, int>(); // kept for HUD compat
    [System.NonSerialized]
    public Dictionary<Vector2Int, GameObject> occupancyQuads = new Dictionary<Vector2Int, GameObject>();
    public const float CELL_SIZE = 4.0f;
    private const float CELL_VOTE_COOLDOWN = 10.0f; // seconds a truck must wait before re-voting the same cell
    public List<Vector3> pendingPoints = new List<Vector3>();
    private List<Vector3> confirmedRoadPoints = new List<Vector3>();
    private LineRenderer confirmedRoadRenderer;
    private Material greyMat;
    private Material orangeMat;
    private Material greenMat;

    // Hazard indicators per truck
    private Dictionary<string, GameObject> _obstacleIndicators = new Dictionary<string, GameObject>();
    private Dictionary<string, int> _lastSonarMap = new Dictionary<string, int>();
    
    // No _lastLoggedCell needed — time-gated voting handles all dedup

    private double _liveAnchorLat = GeoTransform.AnchorTrueLat;
    private double _liveAnchorLng = GeoTransform.AnchorTrueLng;

    private void Start()
    {
        System.IO.Directory.CreateDirectory("path_data");
        Network = RoadNetwork.Load(sharedNetworkPath);
        _learningEngine = new MapLearningEngine(Network);
        _telemetryReader = new SerialTelemetryReader(portNames, baudRate);

        if (truckPrefab == null)
        {
            #if UNITY_EDITOR
            truckPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TruckPrefab.prefab");
            #endif
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        truckMaterial = new Material(shader) { color = Color.yellow };

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.farClipPlane = 25000f;
        }
        RenderSettings.fog = false;

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");

        greyMat = new Material(unlitShader);
        greyMat.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);

        orangeMat = new Material(unlitShader);
        orangeMat.color = new Color(1.0f, 0.6f, 0.0f, 0.8f);

        // Auto-load previously saved JSON roads as Solid Black confirmed voxels
        if (Network != null && Network.confirmedRoads != null)
        {
            foreach (var segment in Network.confirmedRoads)
            {
                foreach (var pt in segment.points)
                {
                    Vector2Int cell = new Vector2Int(Mathf.FloorToInt((float)pt.x / CELL_SIZE), Mathf.FloorToInt((float)pt.z / CELL_SIZE));
                    SpawnConfirmedQuad(cell);
                }
            }
        }

        // Load Raw Telemetry and populate grid
        var rawData = RawTelemetryDB.Load();
        foreach (var pt in rawData.points)
        {
            pendingPoints.Add(pt);
            AddPointToGrid(pt, false);
        }
        UpdateGridVisuals();

        // Obstacle indicators are now spawned dynamically per truck in the Update loop.
    }

    private void Update()
    {
        if (_telemetryReader != null && _telemetryReader.rawDataQueue != null)
        {
            while (_telemetryReader.rawDataQueue.TryDequeue(out string rawLine))
            {
                // Debug.Log($"RAW IN: {rawLine}"); // DISABLED: severe perf hit during demo

                try
                {
                    string[] tokens = rawLine.Trim().Split(',');

                    if (tokens.Length == 3 && rawLine.StartsWith("BASE_STATION"))
                    {
                        double baseLat = double.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture);
                        double baseLng = double.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture);

                        if (baseLat == 0.0 || baseLng == 0.0) continue; // no fix yet

                        // EMA-smooth the incoming anchor to remove phone GPS noise.
                        if (_smoothedAnchorLat == 0.0)
                        {
                            _smoothedAnchorLat = baseLat;
                            _smoothedAnchorLng = baseLng;
                        }
                        else
                        {
                            _smoothedAnchorLat += ANCHOR_SMOOTH_ALPHA * (baseLat - _smoothedAnchorLat);
                            _smoothedAnchorLng += ANCHOR_SMOOTH_ALPHA * (baseLng - _smoothedAnchorLng);
                        }

                        // Phase 1: first valid fix → lock as world origin (0,0,0)
                        // Phase 2: every subsequent fix → update drift measurement for DGPS
                        if (!GeoTransform.IsOriginLocked)
                            GeoTransform.LockOriginFromFirstFix(_smoothedAnchorLat, _smoothedAnchorLng);
                        else
                            GeoTransform.UpdateLiveAnchor(_smoothedAnchorLat, _smoothedAnchorLng);

                        _liveAnchorLat = _smoothedAnchorLat;
                        _liveAnchorLng = _smoothedAnchorLng;
                        continue;
                    }

                    // Handle direct USB connection: truck firmware sends "SELF," prefix over USB.
                    // MUST remap BEFORE splitting tokens so nodeId resolves correctly.
                    if (rawLine.StartsWith("SELF,"))
                    {
                        rawLine = "TRUCK_USB" + rawLine.Substring(4); // SELF, -> TRUCK_USB,
                    }

                    // Re-split after potential SELF remap so tokens[0] = correct nodeId
                    tokens = rawLine.Trim().Split(',');

                    if (tokens.Length != 10 || !rawLine.StartsWith("TRUCK_"))
                    {
                        continue;
                    }

                    string nodeId = tokens[0];
                    double lat = double.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture);
                    double lng = double.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture);
                    int sonar = int.Parse(tokens[3], System.Globalization.CultureInfo.InvariantCulture);

                    // --- Coordinate sanity check ---
                    // LoRa bit corruption can drop the decimal point: '22.319575' becomes '22319575'
                    // which parses as 22,319,575 — a valid double but physically impossible.
                    // Drop any packet whose coordinates are outside physical Earth bounds.
                    if (lat < -90.0 || lat > 90.0 || lng < -180.0 || lng > 180.0)
                    {
                        Debug.LogWarning($"[GPS] Corrupted packet dropped — lat={lat:F2}, lng={lng:F2} out of range. Raw: {rawLine}");
                        continue;
                    }
                    // Also drop if suspiciously far from anchor (>10km) — catches partial corruption
                    if (_liveAnchorLat != 0.0)
                    {
                        double dLat = (lat - _liveAnchorLat) * 111320.0;
                        double dLng = (lng - _liveAnchorLng) * 111320.0 * System.Math.Cos(_liveAnchorLat * System.Math.PI / 180.0);
                        double distMeters = System.Math.Sqrt(dLat * dLat + dLng * dLng);
                        if (distMeters > 10000.0)
                        {
                            Debug.LogWarning($"[GPS] Packet dropped — truck is {distMeters:F0}m from anchor. Likely corrupt. Raw: {rawLine}");
                            continue;
                        }
                    }
                    
                    // Token[9] is GyroZ:
                    // - Python sim: sends pre-computed absolute heading (0-360°) → use directly for rotation
                    // - Real hardware: sends raw MPU6050 GyZ rate (LSB/s) → SensorFilter integrates it
                    // We store raw value in the packet; SensorFilter decides how to handle it.
                    float rawGyroZ = float.Parse(tokens[9], System.Globalization.CultureInfo.InvariantCulture);
                    
                    // Detect if this looks like an absolute heading (0-360) or a raw rate (>360 or <0)
                    // Python sim always sends 0-360; real hardware GyZ can be ±32767
                    bool isAbsoluteHeading = (rawGyroZ >= 0f && rawGyroZ <= 360f);
                    float absoluteHeading = rawGyroZ; // will be overridden below if real hardware

                    if (!deskTestMode && (lat == 0.0 || lng == 0.0))
                    {
                        if (activeTrucks.TryGetValue(nodeId, out Transform t))
                        {
                            if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                        }
                        continue;
                    }

                    if (!activeTrucks.ContainsKey(nodeId))
                    {
                        if (truckPrefab == null) 
                        { 
                            Debug.LogError("ERROR: Truck Prefab is missing! Drag it into the FleetManager Inspector."); 
                            continue; 
                        }

                        GameObject newTruck = Instantiate(truckPrefab);
                        newTruck.name = nodeId;
                        Renderer[] renderers = newTruck.GetComponentsInChildren<Renderer>(true);
                        foreach (Renderer r in renderers)
                        {
                            r.material = truckMaterial;
                        }
                        activeTrucks[nodeId] = newTruck.transform;
                    }

                    Transform activeTruck = activeTrucks[nodeId];
                    if (!activeTruck.gameObject.activeSelf) activeTruck.gameObject.SetActive(true);

                    // Update the legacy IMU state with the latest incoming packet
                    _isAbsoluteHeading[nodeId] = isAbsoluteHeading;
                    if (isAbsoluteHeading)
                    {
                        // Python simulator sending 0-360 absolute heading
                        _truckTargetRot[nodeId] = Quaternion.Euler(0, absoluteHeading, 0);
                        activeTruck.rotation = _truckTargetRot[nodeId]; // Snap directly for sim
                    }
                    else
                    {
                        // Real hardware sending raw GyZ LSB. Store it for the legacy 60FPS integration loop below.
                        _legacyLastYawRate[nodeId] = rawGyroZ / 131.0f;
                    }

                    // --- GPS → Unity position (True DGPS corrected) ---
                    // GpsToLocalWithDgps measures truck offset from AnchorTrue,
                    // then subtracts the live anchor's measured error from AnchorTrue.
                    // Both phones share the same atmospheric bias at <100m separation,
                    // so this correction cancels the common-mode GPS error.
                    var localPos = GeoTransform.GpsToLocalWithDgps(lat, lng);
                    bool anchorReady = (_liveAnchorLat != 0.0 && _liveAnchorLng != 0.0);
                    // Note: anchorReady guard is now inside GeoTransform (returns raw if no live anchor)

                    // Apply per-session GPS calibration offset.
                    // Set by pressing 'C' when truck and base station are physically co-located.
                    localPos.x += _gpsCalibrationOffset.x;
                    localPos.z += _gpsCalibrationOffset.y;

                    // GPS Jitter Smoothing — phone GPS has ~10-15m accuracy and can jump suddenly.
                    // Apply an exponential moving average on the raw GPS-derived position
                    // before handing it to the Lerp target. Alpha 0.2 = heavy smoothing.
                    const float GPS_SMOOTH_ALPHA = 0.2f;
                    if (!_smoothedPos.ContainsKey(nodeId))
                        _smoothedPos[nodeId] = new UnityEngine.Vector2((float)localPos.x, (float)localPos.z);
                    var sp = _smoothedPos[nodeId];
                    sp.x = sp.x + GPS_SMOOTH_ALPHA * ((float)localPos.x - sp.x);
                    sp.y = sp.y + GPS_SMOOTH_ALPHA * ((float)localPos.z - sp.y);
                    _smoothedPos[nodeId] = sp;
                    localPos.x = sp.x;
                    localPos.z = sp.y;

                    MapPoint mapPoint = new MapPoint(localPos.x, localPos.z);

                    if (!_sensorFilters.ContainsKey(nodeId))
                    {
                        _sensorFilters[nodeId] = new SensorFilter();
                    }
                    SensorFilter filter = _sensorFilters[nodeId];
                    
                    // Reconstruct packet for MapLearningEngine and SensorFilter
                    TelemetryPacket packet = new TelemetryPacket
                    {
                        IsBaseStation = false,
                        NodeId = nodeId,
                        Lat = lat,
                        Lng = lng,
                        SonarDistanceCm = sonar,
                        AccX = float.Parse(tokens[4], System.Globalization.CultureInfo.InvariantCulture),
                        AccY = float.Parse(tokens[5], System.Globalization.CultureInfo.InvariantCulture),
                        AccZ = float.Parse(tokens[6], System.Globalization.CultureInfo.InvariantCulture),
                        GyroX = float.Parse(tokens[7], System.Globalization.CultureInfo.InvariantCulture),
                        GyroY = float.Parse(tokens[8], System.Globalization.CultureInfo.InvariantCulture),
                        GyroZ = absoluteHeading,
                        ReceivedAtUtc = System.DateTime.UtcNow
                    };

                    filter.ProcessPacket(packet, (mapPoint.x, mapPoint.z));
                    _learningEngine.ProcessPacket(packet, mapPoint, filter);

                    _lastSonarMap[nodeId] = sonar;

                    // If real hardware: GyZ is a rate, use SensorFilter integrated heading for rotation
                    // If Python sim: GyZ is already 0-360, use directly
                    if (!isAbsoluteHeading)
                    {
                        absoluteHeading = filter.GetCorrectedHeading();
                    }

                    Vector3 targetPos = new Vector3((float)mapPoint.x, 0.0f, (float)mapPoint.z);

                    // Time-Gated Consensus Voting
                    TryVoteCell(nodeId, (float)mapPoint.x, (float)mapPoint.z);

                    float filteredHeading = filter.GetCorrectedHeading();
                    Quaternion targetRot = Quaternion.Euler(0, absoluteHeading, 0); 

                    _truckTargetPos[nodeId] = targetPos;
                    _truckTargetRot[nodeId] = targetRot;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"PARSE FAILED: {rawLine} | Error: {e.Message}");
                }
            }
        }

        // Press 'C' to calibrate GPS offset — use when truck and base station are physically co-located.
        // Records the current truck position error and inverts it so truck snaps to origin.
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            // Find the first active truck's current smoothed position
            foreach (var kvp in _smoothedPos)
            {
                // The truck should be at (0,0) since it's co-located with the base station.
                // Whatever offset it currently shows IS the inter-device GPS bias.
                _gpsCalibrationOffset = new UnityEngine.Vector2(-kvp.Value.x, -kvp.Value.y);
                Debug.Log($"[GPS Calibration] Offset set to ({_gpsCalibrationOffset.x:F2}, {_gpsCalibrationOffset.y:F2})m. " +
                          $"Truck will now appear at origin. Drive away to see accurate relative movement.");
                // Clear the smoothed position so it rebuilds from the corrected coordinates
                _smoothedPos.Clear();
                break;
            }
        }

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            foreach (var filter in _sensorFilters.Values)
            {
                headingOffset = filter.GetCorrectedHeading();
                break;
            }
        }

        // Fallback injection for immediate visual testing without serial dependency:
        if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
        {
            if (_telemetryReader != null && _telemetryReader.rawDataQueue != null)
            {
                _telemetryReader.rawDataQueue.Enqueue("TRUCK_SIM001,22.320000,87.298000,999,0,0,9.8,0,0,0.0");
                Debug.Log("[Manual Inject] Enqueued test packet for TRUCK_SIM001");
            }
        }

        Transform firstActiveTruck = null;

        foreach (var kvp in activeTrucks)
        {
            string id = kvp.Key;
            Transform truck = kvp.Value;
            
            if (_truckTargetPos.TryGetValue(id, out Vector3 tPos))
            {
                // Dynamic DGPS Positional Lerp (Unchanged)
                truck.position = Vector3.Lerp(truck.position, tPos, Time.deltaTime * 3.0f);
                
                // --- LEGACY IMU POST-PROCESSING ---
                if (_isAbsoluteHeading.ContainsKey(id) && !_isAbsoluteHeading[id])
                {
                    // Ensure state dictionaries are initialized
                    if (!_legacyIsCalibrated.ContainsKey(id)) _legacyIsCalibrated[id] = false;
                    if (!_legacyCalibSamples.ContainsKey(id)) _legacyCalibSamples[id] = 0;
                    if (!_legacyAccumBias.ContainsKey(id)) _legacyAccumBias[id] = 0f;
                    if (!_legacyGyroBias.ContainsKey(id)) _legacyGyroBias[id] = 0f;
                    if (!_legacyCurrentHeading.ContainsKey(id)) _legacyCurrentHeading[id] = 0f;
                    if (!_legacyLastYawRate.ContainsKey(id)) _legacyLastYawRate[id] = 0f;

                    float rate = _legacyLastYawRate[id];

                    if (!_legacyIsCalibrated[id])
                    {
                        // Note: Because rate only updates every 500ms, this 50-frame calibration
                        // will just sum the exact same 1st reading 50 times in less than 1 second.
                        _legacyAccumBias[id] += rate;
                        _legacyCalibSamples[id]++;
                        if (_legacyCalibSamples[id] >= TOTAL_CALIBRATION_SAMPLES)
                        {
                            _legacyGyroBias[id] = _legacyAccumBias[id] / TOTAL_CALIBRATION_SAMPLES;
                            _legacyIsCalibrated[id] = true;
                            Debug.Log($"[Legacy IMU] Truck {id} calibrated. Bias: {_legacyGyroBias[id]:F2}");
                        }
                    }
                    else
                    {
                        float correctedTurnRate = rate - _legacyGyroBias[id];
                        if (Mathf.Abs(correctedTurnRate) > gyroNoiseThreshold)
                        {
                            _legacyCurrentHeading[id] -= correctedTurnRate * Time.deltaTime;
                        }
                        
                        // Apply rotation using the user's exact proven Slerp logic
                        truck.rotation = Quaternion.Slerp(truck.rotation, Quaternion.Euler(0f, _legacyCurrentHeading[id], 0f), Time.deltaTime * 6f);
                    }
                }

                if (firstActiveTruck == null)
                    firstActiveTruck = truck;

                // Breadcrumb logic handled via TryVoteCell in the parse loop.

                if (!_obstacleIndicators.ContainsKey(id) || _obstacleIndicators[id] == null)
                {
                    GameObject newIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    newIndicator.name = "HazardIndicator_" + id;
                    newIndicator.GetComponent<Renderer>().material.color = Color.red;
                    newIndicator.layer = 0; // Brute force default layer
                    var col = newIndicator.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                    _obstacleIndicators[id] = newIndicator;
                }

                GameObject hazardBall = _obstacleIndicators[id];
                int currentSonar = _lastSonarMap.ContainsKey(id) ? _lastSonarMap[id] : 999;

                // Sonar threshold: >0 to skip error/no-echo reads, <50cm for real obstacle proximity
                // (Real hardware ultrasonic sensor may echo off the ground at <5cm when truck is moving,
                //  use 5 < sonar < 50 to avoid permanent false triggers from floor bounce)
                if (currentSonar > 5 && currentSonar < 50)
                {
                    hazardBall.SetActive(true);
                    hazardBall.transform.localScale = new Vector3(15, 15, 15);
                    hazardBall.transform.position = truck.position + truck.forward * 25f + Vector3.up * 3f;
                }
                else
                {
                    hazardBall.SetActive(false);
                }
            }
        }

        // Camera logic has been moved to MultiCameraRig.cs
    }

    // Time-Gated Consensus Voting: the core of the enterprise consensus engine.
    // Rule 1: Initialize cell if missing.
    // Rule 2: If this truck voted for this cell within the last 10s, ignore (prevents boundary wiggling & reverse spam).
    // Rule 3: Otherwise increment hit count and log the breadcrumb.
    // Rule 4: Update the per-truck timestamp.
    private void TryVoteCell(string truckId, float worldX, float worldZ)
    {
        Vector2Int cell = new Vector2Int(Mathf.FloorToInt(worldX / CELL_SIZE), Mathf.FloorToInt(worldZ / CELL_SIZE));

        // Rule 1: Initialize cell
        if (!_gridMap.ContainsKey(cell))
            _gridMap[cell] = new CellData();

        CellData data = _gridMap[cell];

        // Rule 2: Cooldown gate
        if (data.lastHitTimePerTruck.TryGetValue(truckId, out float lastTime))
        {
            if (Time.time - lastTime < CELL_VOTE_COOLDOWN)
                return; // Truck voted for this cell too recently — ignore
        }

        // Spatial Awareness: skip if adjacent to a confirmed road
        bool nearConfirmed = false;
        foreach (var segment in Network.confirmedRoads)
        {
            foreach (var pt in segment.points)
            {
                float dx = (float)(pt.x - worldX);
                float dz = (float)(pt.z - worldZ);
                if ((dx * dx + dz * dz) < 36f) { nearConfirmed = true; break; }
            }
            if (nearConfirmed) break;
        }
        if (nearConfirmed) return;

        // Rule 3: Cast the vote
        data.hitCount++;

        // Rule 4: Update timestamp memory
        data.lastHitTimePerTruck[truckId] = Time.time;

        // Sync occupancyGrid for HUD compatibility
        occupancyGrid[cell] = data.hitCount;

        // Log breadcrumb for persistence
        Vector3 breadcrumb = new Vector3(worldX, 0.1f, worldZ);
        pendingPoints.Add(breadcrumb);
        RawTelemetryDB.LogPoint(breadcrumb);

        // Update visual immediately
        UpdateCellVisual(cell);
    }

    private void AddPointToGrid(Vector3 pt, bool updateVisuals = false)
    {
        // Used only for loading historical data from RawTelemetryDB on startup.
        // Does NOT apply the time-gate so legacy data restores accurately.
        Vector2Int cell = new Vector2Int(Mathf.FloorToInt(pt.x / CELL_SIZE), Mathf.FloorToInt(pt.z / CELL_SIZE));
        if (!_gridMap.ContainsKey(cell))
            _gridMap[cell] = new CellData();
        _gridMap[cell].hitCount++;
        occupancyGrid[cell] = _gridMap[cell].hitCount;

        if (updateVisuals)
            UpdateCellVisual(cell);
    }

    private void UpdateGridVisuals()
    {
        foreach (var cell in occupancyGrid.Keys)
        {
            UpdateCellVisual(cell);
        }
    }

    private void UpdateCellVisual(Vector2Int cell)
    {
        int hits = _gridMap.ContainsKey(cell) ? _gridMap[cell].hitCount : 0;

        // Time-Gated consensus thresholds:
        Color cellColor;
        if (hits == 1)      cellColor = Color.white;                              // Discovered
        else if (hits == 2) cellColor = new Color(0.6f, 0.6f, 0.6f, 1f);         // Unconfirmed consensus
        else                cellColor = new Color(1f, 0.1f, 0.1f, 1f);           // High confidence — ready for approval

        GameObject quad;
        if (!occupancyQuads.TryGetValue(cell, out quad))
        {
            quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"Cell_{cell.x}_{cell.y}";
            quad.transform.rotation = Quaternion.Euler(90, 0, 0); // Flat on ground
            quad.transform.position = new Vector3(cell.x * CELL_SIZE, 0.1f, cell.y * CELL_SIZE);
            quad.transform.localScale = new Vector3(CELL_SIZE, CELL_SIZE, 1f);
            
            var col = quad.GetComponent<Collider>();
            if (col != null) Destroy(col);
            
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");
            Material mat = new Material(unlitShader);
            quad.GetComponent<Renderer>().material = mat;
            
            // Assign to Heatmap layer for culling
            int hLayer = LayerMask.NameToLayer("Heatmap");
            quad.layer = hLayer != -1 ? hLayer : 0;
            
            occupancyQuads[cell] = quad;
        }
        
        Material cellMat = quad.GetComponent<Renderer>().material;
        cellMat.color = cellColor;
    }

    public void SpawnConfirmedQuad(Vector2Int cell)
    {
        // Guard: do not double-spawn if a confirmed quad already exists for this cell
        if (occupancyQuads.ContainsKey(cell)) return;

        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = $"ConfirmedCell_{cell.x}_{cell.y}";
        quad.transform.rotation = Quaternion.Euler(90, 0, 0); // Flat on ground
        quad.transform.position = new Vector3(cell.x * CELL_SIZE, 0.11f, cell.y * CELL_SIZE);
        quad.transform.localScale = new Vector3(CELL_SIZE, CELL_SIZE, 1f);
        
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");
        Material mat = new Material(unlitShader) { color = Color.black };
        quad.GetComponent<Renderer>().material = mat;
        
        // Ensure confirmed roads are visible to all cameras
        quad.layer = LayerMask.NameToLayer("Default");
    }

    public void ClearOccupancyGrid()
    {
        foreach (var quad in occupancyQuads.Values)
        {
            if (quad != null) Destroy(quad);
        }
        occupancyQuads.Clear();
        occupancyGrid.Clear();
        _gridMap.Clear();
    }

    private void OnDestroy()
    {
        if (_telemetryReader != null)
        {
            _telemetryReader.Stop();
        }
    }

    private void OnApplicationQuit()
    {
        if (_telemetryReader != null)
        {
            _telemetryReader.Stop();
        }
    }
}
