using System;
using System.IO.Ports;
using System.Threading;
using System.Globalization;

class Program
{
    // Virtual truck state (shared between input and tick threads)
    static double posX = 0.0;       // local meters east
    static double posZ = 0.0;       // local meters north
    static double heading = 0.0;    // degrees, 0 = north, CW positive
    static double speed = 0.0;      // m/s
    static double turnRate = 0.0;   // degrees/sec for steering
    static bool running = true;
    static bool obstacleDetected = false;

    // Physics constants
    const double MAX_SPEED = 15.0;      // m/s (~54 km/h, realistic for a haul truck)
    const double ACCEL = 2.0;           // m/s²
    const double BRAKE = 4.0;           // m/s²
    const double DRAG = 0.3;            // passive deceleration m/s²
    const double STEER_RATE = 45.0;     // degrees/sec max turning
    const double STEER_DECAY = 120.0;   // how fast steering auto-centers (deg/s²)
    const int TICK_MS = 150;            // telemetry tick interval

    static readonly object stateLock = new object();

    static void Main(string[] args)
    {
        // Sanity check GeoTransform
        if (!GeoTransform.SanityCheck())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: GeoTransform sanity check failed!");
            Console.ResetColor();
            return;
        }

        string portName = "COM20";
        int baudRate = 115200;

        // Allow override from command line
        if (args.Length >= 1) portName = args[0];
        if (args.Length >= 2 && int.TryParse(args[1], out int baud)) baudRate = baud;

        SerialPort port = null;
        try
        {
            port = new SerialPort(portName, baudRate);
            port.NewLine = "\n";
            port.DtrEnable = true;
            port.RtsEnable = true;
            port.Handshake = Handshake.None;
            port.Open();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Serial port {portName} opened at {baudRate} baud.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNING: Could not open {portName}: {ex.Message}");
            Console.WriteLine("Running in DRY RUN mode (no serial output).");
            Console.ResetColor();
            port = null;
        }

        // Emit a base station line so the Ground Station has its anchor
        if (port != null && port.IsOpen)
        {
            string baseLine = string.Format(CultureInfo.InvariantCulture,
                "BASE_STATION,{0:F6},{1:F6}",
                GeoTransform.AnchorTrueLat, GeoTransform.AnchorTrueLng);
            port.WriteLine(baseLine);
            port.BaseStream.Flush();
        }

        // Start the telemetry tick thread
        Thread tickThread = new Thread(() => TelemetryTick(port)) { IsBackground = true };
        tickThread.Start();

        // Print instructions
        Console.Clear();
        PrintHeader();

        // Main thread: non-blocking keyboard input
        while (running)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                lock (stateLock)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.W:
                        case ConsoleKey.UpArrow:
                            speed = Math.Min(speed + ACCEL * 0.2, MAX_SPEED);
                            break;

                        case ConsoleKey.S:
                        case ConsoleKey.DownArrow:
                            speed = Math.Max(speed - BRAKE * 0.2, -MAX_SPEED * 0.3);
                            break;

                        case ConsoleKey.A:
                        case ConsoleKey.LeftArrow:
                            turnRate = -STEER_RATE;
                            break;

                        case ConsoleKey.D:
                        case ConsoleKey.RightArrow:
                            turnRate = STEER_RATE;
                            break;

                        case ConsoleKey.Spacebar:
                            speed = 0;
                            turnRate = 0;
                            break;

                        case ConsoleKey.R:
                            posX = 0; posZ = 0; heading = 0; speed = 0; turnRate = 0;
                            break;

                        case ConsoleKey.F:
                            obstacleDetected = !obstacleDetected;
                            break;

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            running = false;
                            break;
                    }
                }
            }
            else
            {
                Thread.Sleep(16); // ~60 Hz input polling
            }
        }

        // Cleanup
        if (port != null && port.IsOpen)
        {
            port.Close();
            port.Dispose();
        }
        Console.Clear();
        Console.WriteLine("Truck Simulator stopped.");
    }

    static void TelemetryTick(SerialPort port)
    {
        DateTime lastTick = DateTime.UtcNow;

        while (running)
        {
            Thread.Sleep(TICK_MS);

            DateTime now = DateTime.UtcNow;
            double dt = (now - lastTick).TotalSeconds;
            lastTick = now;

            double currentSpeed, currentTurnRate, currentHeading, currentX, currentZ;
            bool currentObstacle;

            lock (stateLock)
            {
                // Apply steering
                heading += turnRate * dt;

                // Normalize heading to [0, 360)
                while (heading < 0) heading += 360.0;
                while (heading >= 360.0) heading -= 360.0;

                // Auto-decay turn rate back to zero (steering wheel centering)
                if (Math.Abs(turnRate) > 0.1)
                {
                    double decay = STEER_DECAY * dt;
                    if (turnRate > 0)
                        turnRate = Math.Max(0, turnRate - decay);
                    else
                        turnRate = Math.Min(0, turnRate + decay);
                }

                // Apply friction multiplier
                speed *= 0.95;

                // Move position along heading
                double headingRad = heading * Math.PI / 180.0;
                posX += Math.Sin(headingRad) * speed * dt;  // East component
                posZ += Math.Cos(headingRad) * speed * dt;  // North component

                currentSpeed = speed;
                currentTurnRate = turnRate;
                currentHeading = heading;
                currentX = posX;
                currentZ = posZ;
                currentObstacle = obstacleDetected;
            }

            // Convert local meters to GPS
            var gps = GeoTransform.LocalMetersToGps(currentX, currentZ);

            // Fabricate IMU values
            float gyroZ_raw = (float)(currentTurnRate * 131.0 / 1.0);
            int sonarCm = currentObstacle ? 50 : 999;

            // Wire format: TRUCK_SIM001,lat,lng,sonar,accX,accY,accZ,gyroX,gyroY,gyroZ
            string line = string.Format(CultureInfo.InvariantCulture,
                "TRUCK_SIM001,{0:F6},{1:F6},{2},0.00,0.00,9.80,0.00,0.00,{3:F2}",
                gps.lat, gps.lng, sonarCm, gyroZ_raw);

            // Write to serial port
            if (port != null && port.IsOpen)
            {
                try
                {
                    port.WriteLine(line);
                    port.BaseStream.Flush();
                }
                catch (Exception)
                {
                    // Port may have disconnected
                }
            }

            // Update console dashboard
            PrintDashboard(currentSpeed, currentHeading, currentX, currentZ, gps.lat, gps.lng, gyroZ_raw, sonarCm, port != null && port.IsOpen);
        }
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("+==============================================+");
        Console.WriteLine("|     PathSense Truck Simulator Console        |");
        Console.WriteLine("|==============================================|");
        Console.WriteLine("|  W/Up    Accelerate    S/Down  Brake         |");
        Console.WriteLine("|  A/Left  Steer Left    D/Right Steer Right   |");
        Console.WriteLine("|  SPACE   Emergency Stop   R    Reset Pos     |");
        Console.WriteLine("|  F       Toggle Obstacle                     |");
        Console.WriteLine("|  Q/ESC   Quit                                |");
        Console.WriteLine("+==============================================+");
        Console.ResetColor();
        Console.WriteLine();
    }

    static void PrintDashboard(double spd, double hdg, double x, double z, double lat, double lng, double gyroZ, int sonar, bool portOpen)
    {
        int dashRow = 11;

        try
        {
            Console.SetCursorPosition(0, dashRow);
        }
        catch
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("----------- LIVE TELEMETRY -----------     \n");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  Speed:    {spd,7:F2} m/s  ({spd * 3.6,5:F1} km/h)     \n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"  Heading:  {hdg,7:F2} deg                       \n");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  Local X:  {x,10:F3} m                       \n");
        Console.Write($"  Local Z:  {z,10:F3} m                       \n");

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write($"  GPS Lat:  {lat,12:F6}                      \n");
        Console.Write($"  GPS Lng:  {lng,12:F6}                      \n");

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"  GyroZ:    {gyroZ,10:F2} raw                  \n");

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write($"  Sonar:    {sonar,4} cm                          \n");

        Console.ForegroundColor = portOpen ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"  Serial:   {(portOpen ? "CONNECTED" : "DRY RUN  ")}                     \n");

        // Direction indicator
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("--------------------------------------     \n");

        string compass = GetCompassArrow(hdg);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"  Direction: {compass}                            \n");

        // Obstacle status
        if (sonar < 80)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  !! OBSTACLE DETECTED [Press F to clear] !!  \n");
        }
        else
        {
            Console.Write("                                              \n");
        }

        Console.ResetColor();
    }

    static string GetCompassArrow(double hdg)
    {
        while (hdg < 0) hdg += 360;
        while (hdg >= 360) hdg -= 360;

        if (hdg >= 337.5 || hdg < 22.5)   return "^ N";
        if (hdg >= 22.5 && hdg < 67.5)    return "> NE";
        if (hdg >= 67.5 && hdg < 112.5)   return "> E";
        if (hdg >= 112.5 && hdg < 157.5)  return "> SE";
        if (hdg >= 157.5 && hdg < 202.5)  return "v S";
        if (hdg >= 202.5 && hdg < 247.5)  return "< SW";
        if (hdg >= 247.5 && hdg < 292.5)  return "< W";
        if (hdg >= 292.5 && hdg < 337.5)  return "< NW";
        return "?";
    }
}
