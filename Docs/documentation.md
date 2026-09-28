# PathSense 3D — Enterprise Digital Twin
### Complete Technical Reference & Architecture Guide

---

## Table of Contents
1. [Project Overview](#1-project-overview)
2. [System Architecture](#2-system-architecture)
3. [Telemetry Pipeline](#3-telemetry-pipeline)
4. [Core Systems — Consensus Engine](#4-core-systems--consensus-engine)
5. [Camera & Rendering](#5-camera--rendering)
6. [Universal Hazard System](#6-universal-hazard-system)
7. [Environment Optimization](#7-environment-optimization)
8. [Deep Codebase Reference (Folder by Folder)](#8-deep-codebase-reference)
9. [Known Architectural Decisions & Gotchas](#9-known-architectural-decisions--gotchas)
10. [Unity Scene Hierarchy & Script Attachments](#10-unity-scene-hierarchy--script-attachments)
11. [Running the System](#11-running-the-system)

---

## 1. Project Overview

**PathSense 3D** is a Unity 6 enterprise-grade 3D Digital Twin for open-cast mining operations. It ingests real-time position telemetry from a fleet of physical or simulated trucks and builds a live, learning map of the mine's emerging infrastructure — roads that emerge from truck traffic patterns rather than being drawn by hand.

**Core Demo Flow:**
1. Python simulator (`sim.py`) drives two virtual trucks using WASD/Arrow keys.
2. Telemetry is broadcast as CSV strings over a virtual COM port (com0com).
3. Unity reads and routes the data to 3D truck models in real-time.
4. A Voxel Occupancy Grid tallies where trucks drive most.
5. High-traffic areas turn red (5+ passes), flagging them for review.
6. The operator clicks **Approve** on the HUD — the cells solidify to permanent black roads, saved to `road_network.json`.
7. On next startup, Unity auto-loads and restores all previously approved roads.

---

## 2. System Architecture

```
Python sim.py
  │  (CSV @ 115200 baud)
  │  TRUCK_SIM001,lat,lng,sonar,ax,ay,az,gx,gy,heading
  ▼
com0com Virtual Serial Bridge (COM20 ↔ COM8)
  │
  ▼
SerialTelemetryReader.cs  ←── background thread
  │  (ConcurrentQueue<string> rawDataQueue)
  ▼
GroundStationTwinController.cs  (Unity main thread Update())
  ├── Parses CSV tokens
  ├── GeoTransform: GPS → Unity local meters
  ├── SensorFilter: heading calibration
  ├── MapLearningEngine: consensus validation
  ├── Grid-cell gatekeeper → AddPointToGrid()
  ├── Instantiates / updates activeTrucks
  └── Drives Sonar hazard ball per truck
```

---

## 3. Telemetry Pipeline

### CSV Packet Format (10 tokens)
```
TRUCK_SIM001,22.320000,87.298000,999,0,0,9.8,0,0,45.00
[0] NodeId   [1] Lat   [2] Lng   [3] Sonar [4-6] Acc XYZ [7-8] Gyro XY [9] AbsoluteHeading
```

- **Token [3] Sonar**: `999` = clear, `50` = obstacle detected (< 80 triggers the hazard ball).
- **Token [9] AbsoluteHeading**: Python sends the absolute compass heading (0–360°) directly, NOT a gyro rate. This is critical — see section 9.

### Python Simulator (`sim.py`)
- Runs at 10 Hz (100ms loop).
- Maintains per-truck state dictionaries: `lat`, `lng`, `heading`, `speed`, `sonar`.
- **Truck 1 (`TRUCK_SIM001`)**: WASD + F (sonar).
- **Truck 2 (`TRUCK_SIM002`)**: Arrow keys + 1 (sonar).
- Kinematic math: `dx = speed * sin(radians(heading)) * dt`, `dy = speed * cos(radians(heading)) * dt`.
- Converts dx/dy to lat/lng offsets using Mercator approximation.

---

## 4. Core Systems — Consensus Engine

### 4.1 Voxel Occupancy Grid
The mine floor is logically divided into a **4×4 meter grid** (`CELL_SIZE = 4.0f`).

| Cell Hit Count | Visual Color | Meaning |
|---|---|---|
| 1–2 | Light Grey | Rarely visited |
| 3–4 | Dark Grey | Moderate traffic |
| 5+ | **Red** | Ready for operator approval |
| Approved | **Solid Black** | Permanent confirmed road |

**Key fix applied (Prompt 47):** Cell index uses `FloorToInt` (not `RoundToInt`) for consistent boundary alignment. Logging fires from the **telemetry parse loop** (using the raw GPS-derived position), NOT from the visual Lerp position. This ensures a truck parked in one spot never increments the counter beyond 1.

### 4.2 Grid-Cell Gatekeeper (Deduplication)
```
_lastLoggedCell[nodeId] = Vector2Int(FloorToInt(x/4), FloorToInt(z/4))
```
A point is only logged when the truck **physically crosses into a new cell**. If the cell index hasn't changed since the last packet, the data is discarded.

### 4.3 Spatial Awareness Culling
Before logging a new point, the system checks if the truck is within 6 meters of any confirmed road. If yes, the point is silently discarded — this prevents the heatmap from painting on top of already-approved roads.

### 4.4 MapLearningEngine (Secondary Validator)
Operates on top of the voxel grid as a secondary statistical check. Uses:
- `MIN_LOG_MOVEMENT_METERS = 1.5f`: Ignores GPS jitter.
- `HEADING_AGREEMENT_TOLERANCE_DEG = 20°`: Cross-checks GPS-derived heading vs IMU heading. Discards conflicting packets.
- `REQUIRED_CONFIRMATION_PASSES = 15`: Secondary threshold before marking a candidate `readyForReview`.

### 4.5 Operator Approval Gate (HUD)
Implemented using Unity's legacy `OnGUI()` system for maximum reliability.
- Scans `occupancyGrid` for all cells with `hits >= 5`.
- Turns those quads to `Color.black` and moves them to the **Default** layer.
- Calls `RoadNetwork.Save()` which appends the new roads to `road_network.json`.
- On next `Start()`, these roads are automatically restored via `SpawnConfirmedQuad()`.

**Double-spawn guard added:** `SpawnConfirmedQuad` now checks `occupancyQuads.ContainsKey(cell)` before instantiating, preventing overlapping black quads after a scene reload.

---

## 5. Camera & Rendering

### Split-Screen Architecture (`MultiCameraRig.cs`)

| | Ground Control (Left) | Dashcam (Right) |
|---|---|---|
| Camera | Main Camera | Dynamically spawned "Dashcam" |
| Rect | `(0, 0, 0.5, 1)` | `(0.5, 0, 0.5, 1)` |
| Projection | Orthographic, size=150 | Perspective |
| Position | Static (0, 150, 0) | Parented to truck hood (Y=2.5, Z=3) |
| Sees Heatmap | ✅ Yes | ❌ No (culled) |
| Sees Hazard Ball | ❌ No | ✅ Yes |

### Culling Layers
```
"Heatmap"      → Unconfirmed voxel quads (grey → red)
"DashcamOnly"  → Sonar hazard ball
"Default"      → Trucks, approved roads (black quads), buildings
```

**Bitwise culling:**
```csharp
dashCam.cullingMask = ~(1 << LayerMask.NameToLayer("Heatmap"));
mapCam.cullingMask &= ~(1 << LayerMask.NameToLayer("DashcamOnly"));
```

---

## 6. Universal Hazard System

### Design Decision
Replaced the original single-instance `obstacleIndicator` with a per-truck dictionary system. This ensures each truck gets its own independent hazard ball, no sharing.

```csharp
Dictionary<string, GameObject> _obstacleIndicators
Dictionary<string, int> _lastSonarMap
```

### Trigger Logic (every Update frame)
1. If `_obstacleIndicators[id]` is null → instantiate a new sphere on Layer 0.
2. If `_lastSonarMap[id] < 80` → enable, scale to (15,15,15), position 25m ahead + 3m up.
3. If `>= 80` → `SetActive(false)`.

### Python Trigger
- Truck 1: Press **F** → toggles sonar token between `999` and `50`.
- Truck 2: Press **1** → toggles sonar token between `999` and `50`.

---

## 7. Environment Optimization

The native Unity Terrain Engine was completely removed. Reason: Unity 6 URP's `Shader.Find()` is version-sensitive and would crash with `NullReferenceException` when hardcoded shader names didn't match the exact installed URP package version.

### Current Environment Stack
- **Ground**: `SolidGround` — a `10000×10000` primitive Cube at `(0, -0.5, 0)`.
- **Material**: Standard shader, `Glossiness=0`, `Metallic=0` (matte, no skybox reflections).
- **Truck Y-axis**: Hard-clamped to `0.0f` in the telemetry parse loop. Never floats.
- **Confirmed Road Quads**: Spawned at `Y=0.11f` so they sit just above the ground plane.
- **Heatmap Quads**: Spawned at `Y=0.1f`.

---

## 8. Deep Codebase Reference

### `Assets/Scripts/Core/`

---

#### `GeoTransform.cs`
- **Purpose**: Mathematical bridge between WGS-84 GPS coordinates and Unity's local metric coordinate space.
- **Architecture**: **Dynamic First-Fix Anchoring**. There are NO hardcoded coordinates.
- **Key Flow**:
  1. Unity receives the first `BASE_STATION` packet. That coordinate becomes locked as the origin `(0, 0, 0)` in Unity.
  2. Subsequent `BASE_STATION` packets are compared to the locked origin to measure atmospheric drift (DGPS error).
  3. `TRUCK` packets are converted to local meters offset from the origin, minus the measured drift.
- **Key Method**: `GpsToLocalWithDgps(double lat, double lng) → Vector3`
  - This is the single entry point for all truck positions. It calculates relative offset and cancels common-mode GPS error automatically.
- **Dependencies**: None. Pure C# math, no Unity MonoBehaviour.

---

#### `SerialTelemetryReader.cs`
- **Purpose**: Opens a serial port connection in a background thread and pipes all incoming lines to a thread-safe queue for the Unity main thread.
- **Key Variable**: `ConcurrentQueue<string> rawDataQueue` — the bridge between the I/O thread and Unity's Update loop.
- **Key Method**: `ReadLoop()` — the while loop that calls `_serialPort.ReadLine()` continuously.
- **Failure Handling**: Falls back silently if no COM port is available (dry run mode for desk testing).
- **Dependencies**: `System.IO.Ports`, `System.Threading`.

---

#### `TelemetryPacket.cs`
- **Purpose**: A plain data struct representing a single parsed telemetry packet.
- **Fields**: `NodeId`, `Lat`, `Lng`, `SonarDistanceCm`, `AccX/Y/Z`, `GyroX/Y/Z`, `ReceivedAtUtc`, `IsBaseStation`.
- **Dependencies**: None.

---

#### `SensorFilter.cs`
- **Purpose**: Calibrates sensor bias and attempts to compute a corrected IMU heading from gyroscope data.
- **Key Variable**: `ImuHeading` — the integrated heading estimate in degrees.
- **Key Method**: `GetCorrectedHeading()` — returns the bias-corrected heading, normalised to [0, 360).
- **⚠️ Important Gotcha**: `SensorFilter` was designed to integrate gyro **rates** (°/s). However, the Python simulator sends the **absolute heading** (0–360°) directly in the GyroZ slot. As a result, the bias calibration accumulates ~180° as a bias offset, and `GetCorrectedHeading()` returns a degraded value. In the current build, `absoluteHeading` parsed directly from token[9] is used for truck rotation — bypassing `SensorFilter` entirely for visual accuracy.
- **Dependencies**: `TelemetryPacket`.

---

#### `RoadNetwork.cs`
- **Purpose**: Defines the serializable data schema for the entire road database and provides JSON persistence methods.
- **Key Structs**: `MapPoint(x, z)`, `RoadSegment`, `PendingCandidate`, `RoadNetwork`.
- **Key Methods**:
  - `RoadNetwork.Load(path)` — reads `road_network.json`, deserialises with `JsonUtility`.
  - `RoadNetwork.Save(path)` — serialises and writes to disk.
  - `static Save(List<Vector3>, path)` — convenience method used by the HUD on approval.
- **Storage Location**: `C:\PathSenseShared\road_network.json`
- **Dependencies**: `System.IO`, `UnityEngine.JsonUtility`.

---

#### `MapLearningEngine.cs`
- **Purpose**: A secondary statistical validator sitting above the voxel grid. It validates that a truck truly drove a path (not GPS noise) by cross-referencing GPS heading vs IMU heading.
- **Key Constants**: `ROAD_MATCH_RADIUS_METERS=4.0f`, `MIN_LOG_MOVEMENT_METERS=1.5f`, `HEADING_AGREEMENT_TOLERANCE_DEG=20°`, `REQUIRED_CONFIRMATION_PASSES=15`.
- **Key Method**: `ProcessPacket(packet, localPos, filter)` — core validation gate. Returns early if the point is on an existing road, if movement is too small, or if GPS and IMU headings disagree by more than 20°.
- **Dependencies**: `TelemetryPacket`, `SensorFilter`, `RoadNetwork`, `MapMatcher`.

---

#### `MapMatcher.cs`
- **Purpose**: Spatial proximity query utility.
- **Key Method**: `DistanceToNearestRoad(MapPoint, RoadNetwork)` — returns the nearest distance from a given point to any confirmed road segment. Used by `MapLearningEngine` to determine if a truck is on-road or off-road.
- **Dependencies**: `RoadNetwork`.

---

#### `RawTelemetryDB.cs`
- **Purpose**: A lightweight append-only flat-file database that persists every off-road point logged during a session. This allows the heatmap to be rebuilt after Unity restarts.
- **Key Methods**: `LogPoint(Vector3)`, `Load() → RawTelemetryData`, `Clear()`.
- **Dependencies**: `System.IO`.

---

### `Assets/Scripts/GroundStationTwin/`

---

#### `GroundStationTwinController.cs`
- **Purpose**: The monolithic scene controller. Owns and orchestrates the entire pipeline: serial reading → parsing → coordinate transformation → truck animation → voxel grid → hazard balls.
- **Key Variables**:
  - `activeTrucks: Dictionary<string, Transform>` — maps nodeId to the 3D truck Transform.
  - `occupancyGrid: Dictionary<Vector2Int, int>` — the voxel hit counter.
  - `occupancyQuads: Dictionary<Vector2Int, GameObject>` — the visual quad for each cell.
  - `_lastLoggedCell: Dictionary<string, Vector2Int>` — the grid-cell gatekeeper memory per truck.
  - `_obstacleIndicators: Dictionary<string, GameObject>` — per-truck sonar hazard balls.
  - `_lastSonarMap: Dictionary<string, int>` — last sonar reading per truck.
- **Key Methods**:
  - `Start()`: Loads JSON roads, restores heatmap from `RawTelemetryDB`, spawns confirmed quads.
  - `Update()`: Drains `rawDataQueue`, parses CSV, runs gatekeeper, updates trucks.
  - `AddPointToGrid(pt, updateVisuals)`: Increments cell hit count, optionally refreshes color.
  - `UpdateCellVisual(cell)`: Sets the quad's material color based on hit count.
  - `SpawnConfirmedQuad(cell)`: Creates a permanent black quad for an approved road cell.
- **Performance Note**: `Debug.Log("RAW IN:")` is disabled in the parse loop. Re-enabling it causes severe Unity Editor frame drops during live telemetry.
- **Dependencies**: All Core scripts, `MultiCameraRig`, `GroundStationTwinHUD`.

---

#### `GroundStationTwinHUD.cs`
- **Purpose**: The operator-facing approval UI rendered via Unity's `OnGUI()` system.
- **Key Method**: Approval button handler — scans `occupancyGrid` for `hits >= 5`, solidifies those quads to black, moves them to `Default` layer, and calls `RoadNetwork.Save()`.
- **Dependencies**: `GroundStationTwinController` (directly references its dictionaries).

---

### `Assets/Scripts/TruckTwin/`

---

#### `MultiCameraRig.cs`
- **Purpose**: Manages the split-screen dual-presentation camera setup.
- **Key Method**: `Start()` — configures the left-half Orthographic map view and programmatically instantiates the right-half Dashcam.
- **Key Method**: `Update()` — searches for `TRUCK_SIM001`, parents Dashcam to it when found.
- **Culling Magic**:
  - Map cam: sees Heatmap, does NOT see DashcamOnly.
  - Dashcam: does NOT see Heatmap, DOES see DashcamOnly.
- **Dependencies**: Requires `TRUCK_SIM001` to exist in scene before Dashcam can parent itself.

---

### `Assets/Editor/`

---

#### `NuclearVisualReset.cs` — Menu: `Tools > Nuclear Visual Reset`
- Deletes Unity Terrain from scene.
- Creates `SolidGround` (10000×10000 cube, Y=-0.5).

#### `FinalPolish.cs` — Menu: `Tools > Fix Lighting and Sonar`
- Sets `SolidGround` material Glossiness and Metallic to 0 (kills peach skybox reflection).

#### `FinalDemoFixer.cs` — Menu: `Tools > Fix Digital Twin`
- Clears broken terrain material, restores DirtLayer, auto-binds `confirmedRoadMaterial` on the controller.

#### `MineEnvironmentBuilder.cs` — Menu: `PathSense > Generate Mine Environment`
- Sculpts the terrain heightmap into a crater shape.
- Paints a straight 200m reference road North from the Base Station.
- Spawns 4 processing facility buildings.
- **Note**: Material assignment removed from this script to prevent shader crash on different Unity versions.

---

## 9. Known Architectural Decisions & Gotchas

| # | Issue | Decision |
|---|---|---|
| 1 | URP `Shader.Find()` crashes on different Unity 6 sub-versions | All automated shader assignments removed. Colors set via `material.color` on Standard/Unlit. |
| 2 | `SensorFilter` receives absolute heading (0–360°) not gyro rate | Direct `absoluteHeading` from token[9] is used for truck rotation. `SensorFilter.GetCorrectedHeading()` is only used by `MapLearningEngine` as a secondary validation signal. |
| 3 | Terrain engine caused orange/peach glow | Native terrain deleted. Replaced with `SolidGround` primitive with `Glossiness=0, Metallic=0`. |
| 4 | Single `obstacleIndicator` can only serve one truck | Refactored to `Dictionary<string, GameObject> _obstacleIndicators` per truck. |
| 5 | `RoundToInt` for cell index caused boundary mismatches | Changed to `FloorToInt` globally for consistent cell addressing. |
| 6 | Logging used Lerp'd visual position (breadcrumb) | Grid-cell gatekeeper now reads from the raw telemetry-derived `mapPoint`, not the smoothed visual position. |
| 7 | `Debug.Log` in parse loop caused severe frame drops | Disabled in production. Commented with reason. |
| 8 | `SpawnConfirmedQuad` could double-spawn overlapping quads on reload | Added `occupancyQuads.ContainsKey(cell)` guard before instantiation. |

---

## 10. Unity Scene Hierarchy & Script Attachments

```
Scene Root
├── FleetManager  (Empty GameObject)
│   ├── GroundStationTwinController.cs  ← Main brain
│   └── GroundStationTwinHUD.cs         ← Approval gate UI
│
├── Main Camera
│   └── MultiCameraRig.cs               ← Split-screen manager
│
├── SolidGround  (Cube, scale 10000,1,10000)
│   └── No scripts — pure visual primitive
│
├── TRUCK_SIM001  (instantiated at runtime from Prefab)
│   └── No scripts — driven by Controller
│
├── TRUCK_SIM002  (instantiated at runtime from Prefab)
│   └── No scripts — driven by Controller
│
├── Dashcam  (GameObject, created at runtime by MultiCameraRig)
│   └── Camera component
│
├── HazardIndicator_TRUCK_SIM001  (Sphere, created at runtime)
│   └── No scripts — driven by Controller
│
└── HazardIndicator_TRUCK_SIM002  (Sphere, created at runtime)
    └── No scripts — driven by Controller
```

**Inspector Slots on FleetManager:**
- `portNames`: `COM8,COM7,COM21`
- `baudRate`: `115200`
- `sharedNetworkPath`: `C:\PathSenseShared\road_network.json`
- `truckPrefab`: Drag your Truck Prefab here
- `deskTestMode`: Check this if no COM port is connected

**Required Unity Layers** (add in Edit > Project Settings > Tags and Layers):
- `Heatmap` (e.g. Layer 8)
- `DashcamOnly` (e.g. Layer 9)

---

## 11. Running the System

### Prerequisites
```bash
pip install pyserial keyboard
```
Install com0com and create a linked pair: `COM20 ↔ COM8`.

### Startup Sequence
1. Open Unity project and press **Play**.
2. In a terminal (with venv activated):
```bash
python sim.py
```
3. Drive Truck 1 with **WASD**, Truck 2 with **Arrow Keys**.
4. Press **F** / **1** to test the sonar hazard ball.
5. Drive over the same off-road area 5+ times to trigger red voxels.
6. Click **Approve Roads** in the HUD to permanently solidify the road.
7. Stop the simulator. Stop Unity. Press Play again — approved roads reload from JSON automatically.

### Editor Tools
| Menu Item | Function |
|---|---|
| `Tools > Nuclear Visual Reset` | Delete terrain, create flat brown floor |
| `Tools > Fix Lighting and Sonar` | Kill glossy reflections on ground |
| `Tools > Fix Digital Twin` | Restore terrain layers and auto-bind road material |
| `PathSense > Generate Mine Environment` | Sculpt crater, paint road, spawn buildings |

---

## 12. Time-Gated Consensus Logic

### The Problem It Solves

Previous implementations used either a single "last known cell" tracker or a raw Vector3 distance threshold. Both approaches had critical failure modes:

- **Boundary Wiggling**: A truck idling exactly on the invisible line between two cells would alternate between cells each frame, causing infinite votes with the position never truly changing.
- **Reverse Spam**: A truck driving backwards over its own path could continuously re-vote every cell it passed through, running up hit counts far beyond what honest passes would produce.
- **Lerp vs Reality**: Logging from the visually smoothed (Lerp'd) truck position meant the logged position drifted from the true GPS position, causing misaligned heatmap cells.

### The Solution: `TryVoteCell(truckId, worldX, worldZ)`

Every telemetry packet now passes through a 4-rule gate before incrementing a cell's consensus score:

```
Rule 1: INITIALIZE
  If this cell has never been seen before, create a fresh CellData entry.

Rule 2: COOLDOWN GATE  ← The core fix
  If this specific truck voted for this specific cell within the last 10 seconds, DISCARD the vote.
  The truck must physically leave and come back after 10 seconds for the vote to count again.

Rule 3: VOTE
  Increment CellData.hitCount by exactly 1.

Rule 4: MEMORY UPDATE
  Record CellData.lastHitTimePerTruck[truckId] = Time.time.
```

### Why This Is Mathematically Correct

The 10-second cooldown ensures the minimum time between votes from the same truck is decoupled from the tick rate (10Hz = 100 packets/second). Without the gate, a truck parked on a cell would cast `10 * 60 = 600 votes per minute`. With the gate, it casts exactly `1 vote per 10 seconds` regardless of simulator speed.

### Multi-Truck & Multi-Lap Consensus

```
Scenario: Two trucks both independently discover the same path
  - Truck 1 drives over Cell (12, 8) at T=0s  → hitCount becomes 1 (White)
  - Truck 2 drives over Cell (12, 8) at T=3s  → hitCount becomes 2 (Grey)
  - Truck 1 does another lap at T=15s         → hitCount becomes 3 (RED ✓)
  → Cell is ready for operator approval after 3 independent votes, 15s apart.

Scenario: Truck reverses back and forth over the same edge
  - Truck 1 drives forward over Cell (5, 3) at T=0s  → hitCount becomes 1
  - Truck 1 reverses back at T=2s                    → BLOCKED (cooldown: 8s remaining)
  - Truck 1 drives forward again at T=4s             → BLOCKED (cooldown: 6s remaining)
  - Truck 1 drives forward again at T=11s            → hitCount becomes 2 (voted allowed)
  → Boundary wiggling produces exactly 0 extra votes.
```

### Visual Thresholds (Updated)

| `hitCount` | Color | Meaning |
|---|---|---|
| 1 | **White** | Newly discovered — first truck pass |
| 2 | **Grey** | Unconfirmed consensus — second independent pass |
| 3+ | **Red** | High confidence — eligible for operator approval |
| Approved | **Solid Black** | Permanent confirmed road (persisted to JSON) |

### HUD Integration

The approval gate in `GroundStationTwinHUD.cs` scans `occupancyGrid` for cells where `hitCount >= 3`. This threshold is kept intentionally low because the time-gate already ensures each vote is a genuinely independent observation — not noise.


---

## 13. Real Hardware Transition Guide

### Where COM Ports Are Configured

**File**: [`Assets/Scripts/GroundStationTwin/GroundStationTwinController.cs`](../Assets/Scripts/GroundStationTwin/GroundStationTwinController.cs)

```csharp
// Line 7 — Inspector-editable field:
public string portNames = "COM8,COM7,COM21";
```

`SerialTelemetryReader` tries each port left-to-right and stops at the first one that opens successfully. You will see `[SerialTelemetryReader] Successfully connected to COMx` in the Unity Console when it works.

**How to update for your physical USB port:**
1. Plug in the ground station ESP32.
2. Open Windows Device Manager → Ports (COM & LPT) → note the COMxx number.
3. In the Unity Inspector, find the `FleetManager` GameObject → `GroundStationTwinController` → `Port Names` field.
4. Put your real port first: e.g. `COM3,COM8,COM7` — the fallback ports are kept in case you switch machines.

---

### Dynamic GPS Anchoring (True DGPS)

**File**: [`Assets/Scripts/Core/GeoTransform.cs`](../Assets/Scripts/Core/GeoTransform.cs)

Because this Digital Twin uses a procedural/dummy 3D map rather than a real-world satellite overlay, **absolute GPS coordinates are irrelevant**. The only thing that matters is the relative distance between the Base Station laptop and the Truck.

The system uses **Dynamic First-Fix Anchoring**:
1. When you press Play, Unity waits for the first `BASE_STATION` packet from your phone.
2. It assigns that exact GPS coordinate to `(0, 0, 0)` in your dummy 3D world.
3. Every time a `TRUCK` packet arrives, Unity subtracts the Truck's GPS from the Base Station's GPS to find the relative distance in meters, and moves the 3D model.

**Why this is powerful:**
- **Zero Configuration**: No hardcoded Google Maps coordinates. You can run the system in an open field, a parking lot, or a different city without touching the code.
- **Modular Saving**: Because the system saves `road_network.json` using local Unity `(X, Z)` coordinates, any approved road grid you build will spawn perfectly relative to wherever you set down the Base Station laptop on that specific day.
- **Noise Cancellation**: If atmospheric interference makes the satellites drift 3 meters left, the Base Station and the Truck both shift 3 meters left in the raw data. Because Unity maps the difference between them, the 3-meter error mathematically erases itself.
- **Visual Smoothness**: The `GroundStationTwinController` stretches the 2Hz (500ms) LoRa packets across 60 FPS using `Vector3.Lerp` at `3.0f` speed. The truck glides seamlessly instead of teleporting.

**One-time field procedure:** Boot up, wait for the `"Origin locked"` message in the Unity Console. Press **`C`** on the keyboard while the truck is physically sitting next to the base station. This records any remaining inter-device phone bias and snaps the truck perfectly to `(0, 0, 0)`. Drive away.

---

### Hardware Bugs Found & Fixed

| # | Bug | Where | Fix Applied |
|---|---|---|---|
| 1 | `SELF,` prefix from USB direct connection silently dropped | `GroundStationTwinController.cs` | Remap `SELF,` → `TRUCK_USB,` before parsing |
| 2 | Token[9] = raw GyZ rate (±32767) used as absolute heading (0-360°) | `GroundStationTwinController.cs` + `SensorFilter.cs` | Auto-detect: if value outside 0-360, treat as rate and integrate via SensorFilter |
| 3 | Sonar threshold `< 80cm` permanently triggered by floor echo | `GroundStationTwinController.cs` | Changed to `> 5 && < 50` to reject ground bounce |
| 4 | Firmware serial print was `SELF,lat,lng,...` instead of `TRUCK_XXXX,lat,lng,...` | `my_truck_node.ino` | Fixed to print `nodeID + "," + coreData` |
| 5 | `SensorFilter` gutted to pass-through — no IMU integration for real hardware | `SensorFilter.cs` | Restored rate integration with `dt`, `GYRO_SCALE = 131`, noise gate at 8 LSB |
| 6 | GPS drift and teleportation | `GeoTransform.cs` | Replaced hardcoded anchors with Dynamic First-Fix Anchoring and True DGPS noise subtraction. |
| 7 | `Debug.Log("RAW IN:")` every packet — severe frame drop during demo | `GroundStationTwinController.cs` | Commented out |
| 8 | `SpawnConfirmedQuad` double-spawned overlapping black quads on reload | `GroundStationTwinController.cs` | `ContainsKey()` guard added |

---

### Firmware Wire Format (What the Hardware Actually Sends)

**Ground Station (`ground_station.ino`) → Unity via USB:**
```
BASE_STATION,22.319998,87.298033
```
Broadcasts every 200ms. Triggers live DGPS correction in Unity.

**Truck Node (`my_truck_node.ino`) → LoRa mesh → Ground Station → Unity:**
```
TRUCK_A3F2B1,22.320012,87.298101,45,312,-18,16300,3,2,97
[0]NodeId    [1]lat    [2]lng    [3]dist [4]AcX [5]AcY [6]AcZ [7]GyX [8]GyY [9]GyZ
```
- `[3]` = Sonar distance in cm. `999` = no echo (clear). `0` = error.
- `[9]` = Raw MPU6050 GyroZ in LSB/s (range ±32767, 131 LSB per °/s).
  - The firmware runs a 100-sample calibration on boot so this value is **hardware-bias-corrected**.
  - Unity SensorFilter integrates this into an absolute heading.

**Truck Node via USB Direct** (without LoRa relay):
Previously printed `SELF,...`. Now fixed to print `TRUCK_XXXX,...` matching the LoRa format exactly.

---

### IMU Filter Notes for Real Hardware

The MPU6050 on the truck node:
- Raw GyroZ range: ±32767 LSB (default ±250°/s range → 131 LSB per °/s)
- Firmware already bias-calibrates 100 samples on startup — offsets subtracted in C++
- Unity `SensorFilter` integrates `GyZ / 131.0 * dt` into absolute heading
- Noise gate: `abs(GyZ) > 8 LSB` required to update heading (ignores sensor floor noise)
- Real IMU heading will drift over time — this is a known hardware limitation. The DGPS GPS heading validation in `MapLearningEngine` (HEADING_AGREEMENT_TOLERANCE_DEG = 20°) will discard consensus votes when IMU drifts too far from GPS-derived direction, keeping the map clean.
