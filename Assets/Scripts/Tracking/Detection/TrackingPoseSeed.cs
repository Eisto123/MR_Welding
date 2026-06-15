using UnityEngine;

public struct TrackingPoseSeed
{
    public float[] RowMajorPose16;
    public int ClassId;
    public string Label;
    public float YoloConfidence;
    public float YoloBoxX;
    public float YoloBoxY;
    public float YoloBoxWidth;
    public float YoloBoxHeight;
    public bool DepthQuerySuccess;
    public float InitialDepthMeters;
    public Vector3 InitialWorldPosition;
    public Quaternion InitialWorldRotation;
    public string RetryCandidate;
    public float YoloInferenceMs;
    public float DepthQueryMs;
    public float SeedBuildMs;
}
