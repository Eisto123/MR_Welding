using UnityEngine;

public struct TrackingResult
{
    public TrackingState State;
    public bool ProcessOk;
    public bool PoseWasWritten;
    public bool PoseValid;
    /// <summary>
    /// True only when downstream visualisers should render the tracked pose. Set to false during
    /// the StartConfidenceGate probation window so the wireframe doesn't draw an unstable seed.
    /// </summary>
    public bool IsConfirmed;
    /// <summary>
    /// True while SRT3D is inside the StartConfidenceGate probation window: the bridge has been
    /// seeded and is producing a pose, but it has not yet been confirmed (or rejected) by the
    /// confidence threshold. Useful for debug overlays that want to visualise the in-progress pose.
    /// </summary>
    public bool IsInProbation;
    public bool HasConfidence;
    public float Confidence;
    public int ChangedCount;
    public int FirstChangedIndex;
    public float FirstChangedValue;
    public float[] RowMajorPose16;
    public Vector3 TranslationRowMajor;
    public int TrackedClassId;
    public string TrackedLabel;
    public long TimestampTicksUtc;
    public int FrameWidth;
    public int FrameHeight;
    public bool HasPoseReference;
    public Vector3 PoseReferencePosition;
    public Quaternion PoseReferenceRotation;
    public string ErrorMessage;
    public float FrameSourceMs;
    public float Srt3dUpdateMs;
}
