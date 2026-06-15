public interface ITrackingPoseSeedProvider
{
    bool IsReady { get; }
    bool TryGetSeedPose(FramePacket framePacket, out TrackingPoseSeed seed, out string debugInfo);
}

public interface ITrackingPoseSeedRetryStrategy
{
    void ResetSeedRetryStrategy();
    void NotifySeedRejected(string reason);
    void NotifySeedConfirmed();
}
