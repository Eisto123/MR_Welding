public struct FramePacket
{
    public byte[] Rgb24;
    public int Width;
    public int Height;
    public long TimestampTicksUtc;
    public int RotationDegrees;
    public bool IsVerticallyMirrored;
    public bool HasPoseReference;
    public UnityEngine.Vector3 PoseReferencePosition;
    public UnityEngine.Quaternion PoseReferenceRotation;
}
