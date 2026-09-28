import serial, keyboard, time, math

# ──────────────────────────────────────────────────────────────
# PathSense Multi-Fleet Simulator
# Matches the exact 10-token wire format that GroundStationTwinController.cs expects.
#
# Token layout (per implementation_plan.md §3.1):
#   TRUCK_{id},{lat},{lng},{distCm},{accX},{accY},{accZ},{gyX},{gyY},{heading_0_360}
#
# Key changes aligned with Unity updates:
#   - Emits a BASE_STATION packet every 200ms so GeoTransform.SetAnchorFromLiveGPS()
#     fires in Unity, auto-anchoring the coordinate system (same as real hardware).
#   - heading is sent as absolute 0-360° in token[9] (not a raw gyro rate).
#     Unity's isAbsoluteHeading check (0 <= val <= 360) correctly detects this.
#   - Sonar: 999 = clear, 20 = obstacle. Unity threshold: 5 < sonar < 50.
#     Toggled by F (Truck 1) and 1 (Truck 2).
# ──────────────────────────────────────────────────────────────

ANCHOR_LAT = 22.319998   # Must match GeoTransform.AnchorTrueLat fallback
ANCHOR_LNG = 87.298033   # Must match GeoTransform.AnchorTrueLng fallback
BASE_STATION_INTERVAL = 0.2   # seconds — matches ground_station.ino 200ms interval
TICK_RATE = 0.1               # 10 Hz, matching firmware txInterval ~100ms


def update_truck(t, dt):
    """Advance truck position by one tick and return the wire-format CSV string."""
    rad = math.radians(t['heading'])
    dy = t['speed'] * math.cos(rad) * dt
    dx = t['speed'] * math.sin(rad) * dt

    t['lat'] += dy / 111320.0
    t['lng'] += dx / (111320.0 * math.cos(math.radians(t['lat'])))

    # 10 tokens: id, lat, lng, sonar, accX, accY, accZ, gyX, gyY, heading
    return (
        f"{t['id']},{t['lat']:.6f},{t['lng']:.6f},{t['sonar']},"
        f"0,0,9800,0,0,{t['heading']:.2f}\n"
    )


def main():
    print("=" * 55)
    print("  PathSense Multi-Fleet Simulator")
    print("=" * 55)
    print("  Truck 1 (TRUCK_SIM001): WASD + F (sonar toggle)")
    print("  Truck 2 (TRUCK_SIM002): ARROWS + 1 (sonar toggle)")
    print("  ESC: Quit")
    print("=" * 55)

    try:
        ser = serial.Serial('COM20', 115200, timeout=0)
        print("  Serial: COM20 open at 115200 baud")
    except Exception as e:
        ser = None
        print(f"  Serial: OFFLINE ({e}). Packets printed to console.")

    print()

    # Truck 1 — starts at anchor origin
    t1 = {'id': 'TRUCK_SIM001', 'lat': ANCHOR_LAT, 'lng': ANCHOR_LNG,
          'heading': 0.0, 'speed': 0.0, 'sonar': 999}

    # Truck 2 — offset ~22m south so they don't overlap
    t2 = {'id': 'TRUCK_SIM002', 'lat': ANCHOR_LAT - 0.0002, 'lng': ANCHOR_LNG + 0.0002,
          'heading': 0.0, 'speed': 0.0, 'sonar': 999}

    last_f = 0.0
    last_1 = 0.0
    last_base_tx = 0.0

    while True:
        now = time.time()

        if keyboard.is_pressed('esc'):
            print("\n[SIM] ESC pressed — shutting down.")
            break

        # ── Truck 1 controls ──────────────────────────────────
        t1['speed'] = 0.0
        if keyboard.is_pressed('w'):     t1['speed'] = 15.0
        if keyboard.is_pressed('s'):     t1['speed'] = -15.0
        if keyboard.is_pressed('a'):     t1['heading'] = (t1['heading'] - 5.0) % 360
        if keyboard.is_pressed('d'):     t1['heading'] = (t1['heading'] + 5.0) % 360
        if keyboard.is_pressed('f') and now - last_f > 0.3:
            t1['sonar'] = 20 if t1['sonar'] == 999 else 999
            print(f"[T1 SONAR] {'OBSTACLE' if t1['sonar'] == 20 else 'CLEAR'}")
            last_f = now

        # ── Truck 2 controls ──────────────────────────────────
        t2['speed'] = 0.0
        if keyboard.is_pressed('up'):    t2['speed'] = 15.0
        if keyboard.is_pressed('down'):  t2['speed'] = -15.0
        if keyboard.is_pressed('left'):  t2['heading'] = (t2['heading'] - 5.0) % 360
        if keyboard.is_pressed('right'): t2['heading'] = (t2['heading'] + 5.0) % 360
        if keyboard.is_pressed('1') and now - last_1 > 0.3:
            t2['sonar'] = 20 if t2['sonar'] == 999 else 999
            print(f"[T2 SONAR] {'OBSTACLE' if t2['sonar'] == 20 else 'CLEAR'}")
            last_1 = now

        # ── Build packets ─────────────────────────────────────
        line1 = update_truck(t1, TICK_RATE)
        line2 = update_truck(t2, TICK_RATE)

        # BASE_STATION packet — fires the GeoTransform.SetAnchorFromLiveGPS()
        # in Unity so the coordinate system auto-anchors even during simulation.
        base_line = None
        if now - last_base_tx >= BASE_STATION_INTERVAL:
            base_line = f"BASE_STATION,{ANCHOR_LAT:.6f},{ANCHOR_LNG:.6f}\n"
            last_base_tx = now

        # ── Send / print ──────────────────────────────────────
        if ser:
            if base_line:
                ser.write(base_line.encode('ascii'))
            ser.write(line1.encode('ascii'))
            ser.write(line2.encode('ascii'))
            ser.flush()
        else:
            if base_line:
                print(base_line.strip())
            print(line1.strip())
            print(line2.strip())

        time.sleep(TICK_RATE)


if __name__ == "__main__":
    main()