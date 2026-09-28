# PathSense Unified System — Implementation Plan
### For handoff to another AI → Antigravity task generation

---

## 0. Assumptions Made (confirm or correct before proceeding)

| # | Assumption | Why |
|---|---|---|
| A1 | Only **one physical anchor** (the Ground Station) is required for correct GPS math. North/East are already fixed, real directions defined by the lat/lng math itself — a single anchor gives you a real compass sense for free, no second point needed for orientation, even on a fully artificial (non-satellite) map. | **CONFIRMED** — no second physical anchor needed. |
| A2 | Ground Station's **true** GPS coordinate is a hardcoded constant (surveyed once — e.g. read precisely off Google Maps/satellite view), separate from its **live** phone-GPS reading, which fluctuates. | **CONFIRMED.** |
| A3 | One Unity project, **two scenes**, named `GroundStation_Twin` and `Truck_Twin` (both "twins" for naming consistency; `Truck_Twin` is the visually polished judge-facing one, `GroundStation_Twin` gets a lighter visual treatment — some terrain/building context, not full polish). | **CONFIRMED**, naming updated per your message. |
| A4 | The two scenes run as **two separate standalone builds** (not two Editor Play sessions — Unity Editor can only run one scene at a time), if you want both visible simultaneously during the demo. | Physical constraint of the Unity Editor. |
| A5 | Only **`GroundStation_Twin`** writes to the shared road-network file, because it's the only one with a compute unit actually receiving and logging real telemetry. **`Truck_Twin`** never logs or writes anything — it only reads the file to render. | **CONFIRMED** — matches your description exactly ("nothing else is logging anything, it doesn't even have a compute unit"). |
| A6 | The Truck Simulator is a **separate standalone .NET console app** (not inside Unity), talking over a virtual COM port. | Simplest, and exercises your real Unity code completely unmodified. |
| A7 | Map/terrain covers a **~4km-diameter operating area** (comfortably covering your stated 2.2km radius with margin), centered on the anchor. | Per your latest scale requirement — see §4.12. |

All assumptions confirmed as of your last message except naming, which is now updated throughout this doc.

---

## 1. System Overview

Three programs total:

1. **GroundStation_Twin app** (Unity, Scene `GroundStation_Twin`) — reads serial data from the Ground Station ESP32 (via USB), ingests both `BASE_STATION` and `TRUCK_` lines, applies DGPS correction, runs the self-updating map logic (clustering, consensus counting, human-approval gate), and **saves** the confirmed road network to a shared file. Visual polish not required here — this is the "brain."
2. **Truck_Twin app** (Unity, Scene `Truck_Twin`) — the visually polished, judge-facing 3D viewer. **Loads** the shared road-network file, renders confirmed roads, shows a live 3D truck model, hazard indicator, and multiple camera views (dashcam / free orbit / top-down map).
3. **Truck Simulator** (standalone .NET console app) — lets you teleoperate a virtual truck via keyboard, back-calculates fake GPS + fabricates plausible IMU values, and writes them to a virtual COM port in the *exact* wire format your real firmware uses — so both Unity apps above run completely unaware they're not talking to real hardware.

Data flow:
```
[Real Ground Station ESP32] --USB Serial--> [GroundStation_Twin.unity] --writes--> road_network.json
                                                                                     |
[Truck Simulator OR real truck via USB] --USB Serial--> [Truck_Twin.unity] <--reads-
```

---

## 2. Project & Folder Structure

```
PathSense_Unity/                          (single Unity project)
  Assets/
    Scenes/
      GroundStation_Twin.unity
      Truck_Twin.unity
    Scripts/
      Core/                               <- shared by both scenes
        GeoTransform.cs
        TelemetryPacket.cs
        SerialTelemetryReader.cs
        RoadNetwork.cs
        MapMatcher.cs
        MapLearningEngine.cs
      GroundStationTwin/
        GroundStationTwinController.cs
        GroundStationTwinHUD.cs
      TruckTwin/
        TruckTwinController.cs
        MultiCameraRig.cs
        HazardVisualizer.cs
        TruckVisual.cs
    Editor/
      SceneAutoBuilder.cs                 <- optional, from earlier Antigravity work

TruckSimulatorConsole/                    (separate .NET console project, outside Unity)
  Program.cs
  GeoTransform.cs                         <- IDENTICAL copy of Core/GeoTransform.cs

C:\PathSenseShared\                       (shared folder, outside both projects)
  road_network.json                       <- the single source of truth both apps agree on
```

**Why an explicit shared folder instead of Unity's `Application.persistentDataPath`:** that path is keyed by Company Name + Product Name in Player Settings, which can silently differ between two separate standalone builds even from the same project, pointing them at two different folders without you noticing. A hardcoded shared path avoids this entirely and is easy to inspect/debug by just opening the file.

**Why `GeoTransform.cs` uses plain `(double x, double z)` tuples, not `UnityEngine.Vector3`, in its public API:** so the *identical* file can be copied verbatim into the non-Unity console Simulator project without needing the UnityEngine assembly. A thin wrapper inside Unity-side code converts the tuple to `Vector3`. This guarantees the simulator's math can never drift from the real app's math.

---

## 3. Shared Data Contracts

### 3.1 Serial wire formats (from your existing firmware — unchanged)

| Prefix | Field count | Format |
|---|---|---|
| `BASE_STATION,` | 3 | `BASE_STATION,{lat},{lng}` |
| `TRUCK_xxxxxxxx,` | 10 | `TRUCK_{id},{lat},{lng},{distCm},{accX},{accY},{accZ},{gyX},{gyY},{gyZ}` |

A reading of `lat == 0.0 && lng == 0.0` means "no GPS fix" — must be filtered, never logged, never positioned in Unity.
A `distCm` value of `999` means "no obstacle" — hide the hazard indicator, don't render it at that distance.

### 3.2 Shared `road_network.json` schema

```json
{
  "lastUpdatedUtc": "2026-01-01T00:00:00Z",
  "confirmedRoads": [
    { "id": "road_0001", "points": [ {"x": 0.0, "z": 0.0}, {"x": 5.2, "z": 12.1} ] }
  ],
  "pendingCandidates": [
    {
      "id": "cand_0001",
      "points": [ {"x": 40.0, "z": 18.0} ],
      "passCount": 7,
      "requiredPasses": 15,
      "firstSeenUtc": "...",
      "lastSeenUtc": "..."
    }
  ]
}
```

Points are stored in Unity-space meters (already GPS-transformed), not raw lat/lng — so Truck_Twin never needs to redo any GPS math, just render the points directly.

### 3.3 Tunable constants (define once, in `MapLearningEngine.cs`, as named constants — not magic numbers)

| Constant | Suggested value | Meaning |
|---|---|---|
| `ROAD_MATCH_RADIUS_METERS` | 4.0 | Within this distance of a confirmed road = "on-road" |
| `CANDIDATE_CLUSTER_RADIUS_METERS` | 3.0 | Off-road points within this distance of each other = same candidate cluster |
| `MIN_NEW_ROAD_CLEARANCE_METERS` | 6.0 | A candidate must be at least this far from *every* confirmed road to avoid creating a near-duplicate |
| `REQUIRED_CONFIRMATION_PASSES` | 15 | Number of distinct passes before a candidate is flagged "pending review" (your stated 10–20 range) |
| `HEADING_AGREEMENT_TOLERANCE_DEG` | 20.0 | Max allowed difference between GPS-derived heading and IMU-derived heading for a pass to count |
| `MIN_LOG_MOVEMENT_METERS` | 1.5 | Don't log a new point for a node if it hasn't moved at least this far since its last logged point ("maximum efficiency" dedupe) |

---

## 4. Component Specifications

### 4.1 `Core/GeoTransform.cs` (shared, no UnityEngine dependency)

- Constants: `AnchorTrueLat`, `AnchorTrueLng` (hardcoded, surveyed once).
- `(double x, double z) GpsToLocalMeters(double lat, double lng)` — equirectangular approximation relative to the anchor:
  ```
  x (east)  = (lng - AnchorTrueLng) * 111320.0 * cos(AnchorTrueLat_in_radians)
  z (north) = (lat - AnchorTrueLat) * 111320.0
  ```
- `(double lat, double lng) LocalMetersToGps(double x, double z)` — the inverse, needed by the Simulator to back-calculate fake GPS from a virtual position.
- `(double x, double z) ComputeDgpsCorrection(double liveAnchorLat, double liveAnchorLng)` — returns `(0,0) - GpsToLocalMeters(liveAnchorLat, liveAnchorLng)`, i.e. "how far off the anchor's own live reading currently is from truth." Apply this same offset additively to any simultaneous truck reading before trusting it.

### 4.2 `Core/TelemetryPacket.cs`

```csharp
public struct TelemetryPacket {
    public string NodeId;
    public bool IsBaseStation;
    public double Lat, Lng;
    public int SonarDistanceCm;
    public float AccX, AccY, AccZ;
    public float GyroX, GyroY, GyroZ;
    public DateTime ReceivedAtUtc;
}
```

### 4.3 `Core/SerialTelemetryReader.cs`

- Opens a `SerialPort` on a configurable port name/baud rate.
- Runs a background `Thread` calling `ReadLine()` in a loop.
- Parses **conditionally by prefix** (not fixed field count — this was the bug flagged earlier):
  - `BASE_STATION,` → 3 tokens → `IsBaseStation = true`, only Lat/Lng populated.
  - `TRUCK_` → 10 tokens → full `TelemetryPacket`.
  - Anything else / wrong token count / parse failure → log and skip, never throw out of the thread.
- Pushes successfully parsed packets into a `ConcurrentQueue<TelemetryPacket>`.
- Exposes `TryDequeue(out TelemetryPacket packet)` for the main thread to drain in `Update()`.
- Used by **both** scenes (Ground Station reads its own port; Digital Twin reads its own port when directly wired to a truck or the Simulator).

### 4.4 `Core/RoadNetwork.cs`

- Plain data model matching the JSON schema in §3.2.
- `Save(string path)` / `Load(string path)` using `JsonUtility` or `Newtonsoft.Json`.
- **Only `GroundStationTwinController` calls `Save()`.** `TruckTwinController` only calls `Load()`.

### 4.5 `Core/MapMatcher.cs`

- `float DistanceToNearestRoad((double x, double z) point, RoadNetwork network)` — checks distance from a point to every line segment in every confirmed road, returns the minimum.

### 4.6 `Core/MapLearningEngine.cs` — the core USP logic

1. For each new truck packet (after DGPS correction + GPS→local-meters conversion):
   - If `DistanceToNearestRoad(...) <= ROAD_MATCH_RADIUS_METERS` → on-road, do nothing further (normal tracking).
   - Else → off-road. Find or create a candidate cluster within `CANDIDATE_CLUSTER_RADIUS_METERS` of this point.
2. Compute heading two ways for this pass:
   - **GPS-derived heading**: direction vector between this reading and the node's previous logged reading: $\text{atan2}(\Delta x, \Delta z)$ in degrees.
   - **IMU-derived heading**: integrate raw **`GyroZ`** (scaled by $131.0\text{ LSB}/(^\circ/\text{s})$) over $\Delta t$ since the last packet: $\theta_{\text{imu}} = \theta_{\text{prev}} + \left(\frac{\text{GyroZ}}{131.0}\right) \Delta t$.
   - If $\vert{}\Delta\theta\vert{} = \vert{}\text{Heading}_{\text{GPS}} - \text{Heading}_{\text{IMU}}\vert{} > \text{HEADING\_AGREEMENT\_TOLERANCE\_DEG}$, **discard this pass** — do not increment the candidate counter (this is your sensor handshake/sanity check).
3. If the pass is valid, increment that candidate cluster's `passCount`.
4. Before allowing a candidate to ever be promoted, verify it is at least `MIN_NEW_ROAD_CLEARANCE_METERS` from every existing confirmed road (prevents near-duplicate parallel roads from GPS jitter — this is your "give enough clearance" requirement).
5. Once `passCount >= REQUIRED_CONFIRMATION_PASSES`, mark the candidate `readyForReview = true` — do **not** auto-promote. Surface it in `GroundStationTwinHUD` for manual approval.
6. On manual **Approve**: move the candidate into `confirmedRoads`, call `RoadNetwork.Save()`.
7. On manual **Reject**: discard the candidate entirely.

### 4.7 `GroundStationTwin/GroundStationTwinController.cs`

- Owns one `SerialTelemetryReader` (Ground Station's own COM port).
- Drains the queue every `Update()`.
- Routes `BASE_STATION` packets → updates the live anchor reading (feeds `GeoTransform.ComputeDgpsCorrection`).
- Routes `TRUCK_` packets → applies DGPS correction → converts to local meters → dedupes via `MIN_LOG_MOVEMENT_METERS` → feeds into `MapLearningEngine`.
- Does **not** need to look pretty — a simple top-down debug view is enough here.

### 4.8 `GroundStationTwin/GroundStationTwinHUD.cs`

- Text log panel (recent events).
- List of pending candidates with live pass-count (`"cand_0003 — 11/15 passes"`).
- Approve / Reject buttons per candidate.

### 4.9 `TruckTwin/TruckTwinController.cs`

- On `Start()`: `RoadNetwork.Load(sharedPath)`, render every confirmed road (e.g. as `LineRenderer`s or extruded meshes).
- Owns its own `SerialTelemetryReader` — either reading a truck relayed via a directly-wired USB connection, or reading the Truck Simulator's virtual COM port.
- Applies the same DGPS correction + local-meters conversion as Ground Station (for correct positioning), and the same `MapMatcher` check purely for a visual "off-road" HUD indicator — but never writes to the file.
- Moves/rotates the 3D truck model via `TruckVisual.cs`.

### 4.10 `TruckTwin/MultiCameraRig.cs`

Three modes, switchable by key press or on-screen buttons:
- **Dashcam** — camera mounted just behind/inside the truck model, forward-facing, follows position + rotation.
- **Free Orbit** — mouse-drag orbit + scroll-wheel zoom around the whole scene (standard orbit-camera pattern).
- **Top-Down Map** — classic orthographic overview, similar to your old 2D view, useful for seeing the whole road network at once.

### 4.11 `TruckTwin/HazardVisualizer.cs` & `TruckVisual.cs`

Reuse the logic patterns from your old `TruckTelemetryManager.cs` almost directly:
- Hide the hazard sphere when `distCm == 999`; otherwise position it in front of the truck along `transform.forward`.
- Smooth position/rotation via `Vector3.Lerp` / `Quaternion.Slerp`, same as before — this part of your old code was solid, keep the pattern.

### 4.12 Map Scale & Terrain Size

Per your latest requirement: the operating area is a ~2.2km-radius circle, so size everything for a **4000m × 4000m** terrain (or a 4000m-diameter circular playable area), centered on the anchor origin `(0, 0, 0)`. This gives comfortable margin around your stated 2.2km radius.

- Unity's `Terrain` component supports this size natively — set `terrainData.size = (4000, terrainHeight, 4000)` (pick a modest `terrainHeight`, e.g. 200–500m, since Bailadila-scale elevation change doesn't need to be dramatic for the demo).
- Since `GeoTransform` already outputs 1 Unity unit = 1 real metre, no extra scale factor is needed anywhere else — the terrain size and the GPS math are already in the same units by construction.
- **Float precision note:** Unity's `Vector3` uses 32-bit floats, which lose meaningful precision only at distances of tens of kilometres or more from the origin. At ±2000m from the anchor, precision is completely fine — no special handling (like floating-origin tricks) needed at this scale.
- Keep the anchor `(0,0,0)` roughly at the terrain's centre, not a corner, so the full 2.2km operating radius fits within the terrain bounds in every direction.

### 4.13 Truck Simulator (`TruckSimulatorConsole`, separate .NET console project)

- Copy `GeoTransform.cs` verbatim from the Unity project (it has no UnityEngine dependency, so this is a plain file copy).
- Open a `SerialPort` on a virtual COM port (e.g. `COM20`, the write-end of a com0com pair).
- Read arrow-key/WASD input in a loop; maintain a virtual `(x, z)` position and a heading angle.
- Each tick (~150ms, matching real firmware's interval):
  - `LocalMetersToGps(x, z)` → fake lat/lng.
  - Fabricate `AccX/Y/Z` and `GyroX/Y/Z` — small baseline noise, with yaw-rate roughly proportional to turning input.
  - Write the line in the exact `TRUCK_{id},...` format from §3.1.
- **Setup requirement:** install com0com, create a virtual pair (e.g. `COM20 <-> COM21`). Simulator writes to `COM20`. Whichever Unity scene you're testing reads `COM21` — just change the `portName` field in the Inspector temporarily, no code changes.

### 4.14 Sensor Fusion & Auto-Calibration (Core/SensorFilter.cs)
Raw MPU6050 data is too noisy for direct integration. All incoming `TelemetryPacket` data must pass through this stateful filter before feeding the MapLearningEngine.

1. **Zero-Velocity Update (ZUPT):** 
   - Calculate velocity from `(Lat, Lng)` changes over time. 
   - If velocity < 0.2 m/s for > 1.0 second, truck is stationary. Average the `GyroX, GyroY, GyroZ` readings to establish the live `GyroBias`. 
   - Subtract `GyroBias` from all raw gyro readings when moving.
2. **Complementary Filter for Tilt:**
   - Calculate raw Accel Pitch/Roll using `atan2` on the gravity vector.
   - Integrate Gyro rates for dynamic Pitch/Roll.
   - Fuse them: `Pitch = 0.98 * (Pitch + GyroPitchRate * dt) + 0.02 * (AccelPitch)`.
3. **Dynamic Yaw Alignment:**
   - When GPS velocity > 2.0 m/s and GPS heading variance is low over a 3-second rolling window, calculate `Yaw_Mount_Bias = GPS_Heading - IMU_Heading`.
   - Apply `Yaw_Mount_Bias` to all future IMU headings to correct physical crabbing/misaligned sensor mounting.
4. **Grade Extraction:**
   - Expose `RoadGradePercent = tan(Pitch) * 100`. Store this in `road_network.json` metadata for the specific road segment.
---

## 5. Step-by-Step Build Order

Follow this order — each step should compile and be testable before moving to the next. Numbered for direct conversion into Antigravity tasks.

1. **Create `Core/GeoTransform.cs`** with the math from §4.1. Write a few `Debug.Assert` sanity checks (round-trip `GpsToLocalMeters` → `LocalMetersToGps` should return the original input within floating-point tolerance).
2. **Create `Core/TelemetryPacket.cs`** (plain struct, no logic).
3. **Create `Core/SerialTelemetryReader.cs`** implementing the conditional-prefix parser and `ConcurrentQueue` pattern from §4.3. Test standalone by opening a real or virtual COM port and confirming packets appear in the queue with correct field values — do this *before* wiring it into any scene.
4. **Create `Core/RoadNetwork.cs`** with Save/Load and the JSON schema from §3.2. Test by manually constructing a network in code, saving it, reloading it, and asserting equality.
5. **Create `Core/MapMatcher.cs`** (distance-to-nearest-road).
6. **Create `Core/MapLearningEngine.cs`** implementing the full pipeline from §4.6. Unit-test with synthetic point sequences (no real hardware needed yet) to confirm: on-road points don't create candidates; off-road points cluster correctly; heading-mismatched passes are discarded; clearance rule prevents near-duplicate roads; promotion only happens at the pass threshold.
7. **Build `GroundStationTwinController.cs` + `GroundStationTwinHUD.cs`** in the `GroundStation_Twin` scene, wiring steps 3–6 together with a real or virtual COM port.
8. **Build the `TruckSimulatorConsole` project** (§4.13). At this point you can fully test steps 3–7 end-to-end without walking anywhere or needing the real truck hardware.
9. **Run a full learning-cycle test using the Simulator**: teleoperate the virtual truck off a known road repeatedly, confirm the candidate's pass count climbs, confirm it reaches "ready for review," approve it via the HUD, confirm `road_network.json` is written correctly with the new road.
10. **Build `TruckTwinController.cs`** — load the file written in step 9, confirm the new road renders correctly in the `Truck_Twin` scene.
11. **Build `TruckVisual.cs` + `HazardVisualizer.cs`** — live truck movement + hazard sphere, reusing your old script's proven Lerp/Slerp patterns.
12. **Build `MultiCameraRig.cs`** — three camera modes, switchable.
13. **Visual polish pass** — terrain texture, truck 3D model, lighting, skybox. Safe to hand this step to Antigravity fairly freely once 1–12 are proven, since it's presentation-only and can't break the underlying logic.
14. **Full integration test**: real Ground Station ESP32 (or Simulator) feeding `GroundStation_Twin.unity`, confirmed map saved, then `Truck_Twin.unity` launched fresh and confirmed it loads and displays that same map correctly.

---

## 6. Testing Checklist (run before demo day)

- [ ] `BASE_STATION` and `TRUCK_` lines both parse without crashing the serial thread, including deliberately truncated/garbled lines.
- [ ] A reading of `(0.0, 0.0)` never gets logged or rendered as a real position.
- [ ] `distCm == 999` never shows a hazard indicator.
- [ ] Round-trip GPS↔local-meters math is verified accurate (step 1's assertions).
- [ ] A single off-road pass does **not** create a confirmed road (must take `REQUIRED_CONFIRMATION_PASSES`).
- [ ] A heading-mismatched pass does **not** count toward the threshold.
- [ ] A candidate too close to an existing road (`< MIN_NEW_ROAD_CLEARANCE_METERS`) is correctly rejected/never promoted.
- [ ] `road_network.json` written by Ground Station is correctly read by a **freshly launched** Truck_Twin instance (not just one already running).
- [ ] Full simulator-driven run-through works with zero real hardware connected.
- [ ] Full real-hardware run-through works at least once, end to end, before demo day.

---

## 7. Known Limitations — say these plainly if asked, don't hide them

- DGPS correction here assumes the truck and Ground Station experience *similar* atmospheric/satellite error at the same moment — a reasonable approximation at hundreds-of-metres-to-low-kilometres range with consumer phone GPS, but not true survey-grade RTK.
- `REQUIRED_CONFIRMATION_PASSES` and the other tunable constants in §3.3 are reasonable starting values, not derived from real operational data — say so if pressed.
- The Simulator proves the software pipeline works correctly; it does not prove real hardware behaves identically in the field (same honest distinction as your ESP32 prototype vs. industrial-grade final vision).