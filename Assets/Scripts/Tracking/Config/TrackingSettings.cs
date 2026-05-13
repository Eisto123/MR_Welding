using UnityEngine;

[CreateAssetMenu(fileName = "TrackingSettings", menuName = "Tracking/Tracking Settings")]
public class TrackingSettings : ScriptableObject
{
    [Header("Tracking Success Criteria")]
    public int RequireConsecutiveValidFrames = 3;
    public int MissFramesBeforeLost = 8;

    [Header("Pose Conversion")]
    public bool PoseIsInCameraSpace = true;
    public bool FlipCvYToUnity = true;
    public float TranslationScale = 1f;
    [Range(0f, 1f)] public float PoseSmoothing = 0.35f;
}
