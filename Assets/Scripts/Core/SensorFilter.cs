using System;

// SensorFilter for real MPU6050 hardware on the truck node.
// The firmware (my_truck_node.ino) sends RAW GyroZ in LSB/s (range ±32767 for ±250°/s range).
// The firmware already runs a 100-sample hardware bias calibration on boot.
//
// DRIFT FIX — ZUPT (Zero Velocity Update):
//   The MPU6050 is a MEMS chip whose bias shifts as the chip warms up after boot.
//   The startup calibration captures the cold bias, but within minutes the chip
//   is warmer and a residual bias (typically 5-30 LSB) leaks through, causing
//   slow perpetual rotation even when the truck is stationary.
//   Solution: when GPS shows the truck hasn't moved, stop integrating GyroZ entirely.
//   This is the standard real-world fix used in all professional IMU systems.
public class SensorFilter
{
    // MPU6050 sensitivity scale factor: 131 LSB per degree/second (±250 dps range)
    private const float GYRO_SCALE = 131.0f;

    // Noise gate: raised from 8 → 20 LSB to account for temperature-induced
    // post-calibration bias drift. 20 LSB = 0.15°/s = 9°/min max drift.
    // A real vehicle turn is typically >500 LSB so this does not affect turn detection.
    private const float NOISE_GATE_LSB = 20.0f;

    // ZUPT gyro override threshold: if |GyroZ| exceeds this, the truck IS deliberately
    // rotating even if GPS shows no movement (spinning in place).
    // 100 LSB = ~0.76°/s — well above thermal drift (~15-25 LSB) but catches slow spins.
    private const float ZUPT_GYRO_OVERRIDE_LSB = 100.0f;

    // ZUPT threshold: if the truck moves less than this (metres) between consecutive
    // GPS packets, treat it as stationary and freeze heading integration.
    private const double ZUPT_MOVEMENT_THRESHOLD_M = 0.8;

    // How many consecutive stationary packets before ZUPT kicks in.
    // Prevents ZUPT triggering on momentary GPS jitter when actually moving.
    private const int ZUPT_CONFIRM_FRAMES = 3;

    public float ImuHeading { get; private set; } = 0f;
    private DateTime _lastTime = DateTime.MinValue;

    // ZUPT state
    private (double x, double z) _lastGpsPos = (0, 0);
    private bool _lastGpsPosSet = false;
    private int _stationaryFrameCount = 0;
    private bool _isStationary = false;

    public void ProcessPacket(TelemetryPacket packet, (double x, double z) localPos)
    {
        DateTime now = packet.ReceivedAtUtc;
        if (_lastTime == DateTime.MinValue)
        {
            _lastTime = now;
            _lastGpsPos = localPos;
            _lastGpsPosSet = true;
            return; // Need at least two samples for dt
        }

        float dt = (float)(now - _lastTime).TotalSeconds;
        _lastTime = now;

        if (dt <= 0f || dt > 2.0f) return; // Skip if dt is invalid or stale

        // --- ZUPT: check GPS movement ---
        if (_lastGpsPosSet)
        {
            double dx = localPos.x - _lastGpsPos.x;
            double dz = localPos.z - _lastGpsPos.z;
            double moved = Math.Sqrt(dx * dx + dz * dz);

            if (moved < ZUPT_MOVEMENT_THRESHOLD_M)
            {
                _stationaryFrameCount++;
            }
            else
            {
                // Truck is moving — reset ZUPT counter and unfreeze
                _stationaryFrameCount = 0;
                _isStationary = false;
            }

            if (_stationaryFrameCount >= ZUPT_CONFIRM_FRAMES)
            {
                _isStationary = true;
            }
        }
        _lastGpsPos = localPos;
        _lastGpsPosSet = true;

        // If ZUPT says we're stationary, check gyro override before freezing.
        // If GyroZ is large enough to be a real intentional rotation (spinning in place),
        // bypass ZUPT and allow heading to integrate normally.
        bool gyroSaysRotating = Math.Abs(packet.GyroZ) > ZUPT_GYRO_OVERRIDE_LSB;
        if (_isStationary && !gyroSaysRotating) return;

        float rawRate = packet.GyroZ; // Already hardware-bias-corrected by firmware

        // Noise gate: ignore rates below threshold to catch residual temperature drift
        if (Math.Abs(rawRate) > NOISE_GATE_LSB)
        {
            float degreesPerSecond = rawRate / GYRO_SCALE;
            ImuHeading += degreesPerSecond * dt;
        }
    }

    public float GetCorrectedHeading()
    {
        float corrected = ImuHeading;
        while (corrected < 0f) corrected += 360f;
        while (corrected >= 360f) corrected -= 360f;
        return corrected;
    }
}
