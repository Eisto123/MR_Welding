public interface ITrackingPoseSeedProvider
{
    bool IsReady { get; }
    bool TryGetSeedPose(FramePacket framePacket, out TrackingPoseSeed seed, out string debugInfo);
}
