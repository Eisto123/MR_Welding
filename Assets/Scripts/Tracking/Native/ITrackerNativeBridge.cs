using System;

public interface ITrackerNativeBridge
{
    bool IsInitialized { get; }
    bool SupportsConfidence { get; }
    string LastError { get; }

    bool InitializeBridge();
    bool StartTrackingFromPose(float[] rowMajorPose16);
    bool StartTrackingFromFilePose();
    void StopTracking();
    bool ProcessFrame(byte[] rgb24, int width, int height);
    bool ProcessFrameRgba32(IntPtr rgba32, int width, int height);
    void GetTrackedPose(float[] outMatrix16);
    float GetTrackingConfidence();
    void ShutdownBridge();

    /// <summary>
    /// Atomically switch to tracking a different object.
    /// Stops the current tracker, loads the new object files, and restarts.
    /// On Android this maps to the native SwitchTrackingObject call.
    /// On Windows this sequences ShutdownBridge → reconfigure → InitializeBridge.
    /// </summary>
    bool SwitchObject(string objPath, string metaPath, string posePath);
    bool SwitchObjectByFileName(string objFileName, string metaFileName, string poseFileName);
}
