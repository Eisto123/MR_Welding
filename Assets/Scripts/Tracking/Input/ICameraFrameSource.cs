public interface ICameraFrameSource
{
    bool IsReady { get; }
    int CurrentWidth { get; }
    int CurrentHeight { get; }

    void StartSource();
    void StopSource();
    bool TryGetFrame(out FramePacket framePacket);
}
