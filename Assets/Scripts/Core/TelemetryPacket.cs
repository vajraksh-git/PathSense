using System;

public struct TelemetryPacket
{
    public string NodeId;
    public bool IsBaseStation;
    public double Lat, Lng;
    public int SonarDistanceCm;
    public float AccX, AccY, AccZ;
    public float GyroX, GyroY, GyroZ;
    public DateTime ReceivedAtUtc;
}
