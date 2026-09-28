using System;
using System.Collections.Generic;

public class MapLearningEngine
{
    public const float ROAD_MATCH_RADIUS_METERS = 4.0f;
    public const float CANDIDATE_CLUSTER_RADIUS_METERS = 3.0f;
    public const float MIN_NEW_ROAD_CLEARANCE_METERS = 6.0f;
    public const int REQUIRED_CONFIRMATION_PASSES = 15;
    public const float HEADING_AGREEMENT_TOLERANCE_DEG = 20.0f;
    public const float MIN_LOG_MOVEMENT_METERS = 1.5f;

    private Dictionary<string, (MapPoint lastPoint, DateTime lastTime)> _nodeLastLog = new Dictionary<string, (MapPoint, DateTime)>();
    private RoadNetwork _network;

    public MapLearningEngine(RoadNetwork network)
    {
        _network = network;
    }

    public void ProcessPacket(TelemetryPacket packet, MapPoint localPos, SensorFilter filter)
    {
        float distToRoad = MapMatcher.DistanceToNearestRoad(localPos, _network);
        
        if (distToRoad <= ROAD_MATCH_RADIUS_METERS)
        {
            // Point is on-road, no learning needed.
            return;
        }

        // Point is off-road
        if (!_nodeLastLog.ContainsKey(packet.NodeId))
        {
            _nodeLastLog[packet.NodeId] = (localPos, packet.ReceivedAtUtc);
            return; // Wait for next point to establish movement vector
        }

        var lastLog = _nodeLastLog[packet.NodeId];
        float dx = (float)(localPos.x - lastLog.lastPoint.x);
        float dz = (float)(localPos.z - lastLog.lastPoint.z);
        float distMoved = (float)Math.Sqrt(dx * dx + dz * dz);

        // Efficiency deduplication check
        if (distMoved < MIN_LOG_MOVEMENT_METERS)
        {
            return;
        }

        // Sensor Handshake: Compare GPS vs IMU heading
        float gpsHeading = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        if (gpsHeading < 0) gpsHeading += 360f;
        
        float imuHeading = filter.GetCorrectedHeading();
        
        float headingDiff = Math.Abs(gpsHeading - imuHeading);
        if (headingDiff > 180f) headingDiff = 360f - headingDiff;
        
        if (headingDiff > HEADING_AGREEMENT_TOLERANCE_DEG)
        {
            // Headings disagree, discard this pass
            return;
        }

        // Pass is valid, update state
        _nodeLastLog[packet.NodeId] = (localPos, packet.ReceivedAtUtc);

        // Find or create candidate cluster
        PendingCandidate candidate = null;
        float minDist = float.MaxValue;
        
        foreach (var cand in _network.pendingCandidates)
        {
            if (cand.points != null && cand.points.Count > 0)
            {
                var candCenter = cand.points[0]; // Anchor distance check on the start of the cluster
                float candDist = (float)Math.Sqrt(Math.Pow(candCenter.x - localPos.x, 2) + Math.Pow(candCenter.z - localPos.z, 2));
                
                if (candDist < CANDIDATE_CLUSTER_RADIUS_METERS && candDist < minDist)
                {
                    minDist = candDist;
                    candidate = cand;
                }
            }
        }

        if (candidate == null)
        {
            candidate = new PendingCandidate
            {
                id = "cand_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                points = new List<MapPoint> { localPos },
                passCount = 1,
                requiredPasses = REQUIRED_CONFIRMATION_PASSES,
                firstSeenUtc = packet.ReceivedAtUtc.ToString("o"),
                lastSeenUtc = packet.ReceivedAtUtc.ToString("o"),
                readyForReview = false
            };
            _network.pendingCandidates.Add(candidate);
        }
        else
        {
            candidate.passCount++;
            candidate.lastSeenUtc = packet.ReceivedAtUtc.ToString("o");
            candidate.points.Add(localPos);

            // Promotion Check
            if (!candidate.readyForReview && candidate.passCount >= candidate.requiredPasses)
            {
                // Verify clearance to avoid near-duplicates of existing roads
                bool clearanceOk = true;
                foreach (var pt in candidate.points)
                {
                    if (MapMatcher.DistanceToNearestRoad(pt, _network) < MIN_NEW_ROAD_CLEARANCE_METERS)
                    {
                        clearanceOk = false;
                        break;
                    }
                }

                if (clearanceOk)
                {
                    candidate.readyForReview = true;
                }
            }
        }
    }
}
