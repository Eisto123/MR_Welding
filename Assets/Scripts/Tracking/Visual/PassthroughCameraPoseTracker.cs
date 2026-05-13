using Meta.XR;
using UnityEngine;

/// <summary>
/// Keeps this Transform in sync with the physical passthrough camera pose by applying
/// PassthroughCameraAccess.Intrinsics.LensOffset as a LOCAL offset relative to this
/// GameObject's parent (which should be CenterEyeAnchor or equivalent).
///
/// Place this as a CHILD of CenterEyeAnchor in the OVRCameraRig hierarchy:
///
///   OVRCameraRig
///   └── TrackingSpace
///       └── CenterEyeAnchor
///           └── PhysicalCameraOffset   ← this script lives here
///
/// Then assign this GameObject's Transform (not a Camera component) to:
///   - TrackingVisualizer.poseReferenceTransform
///   - TrackingWireframeOverlay.poseReferenceTransform
///
/// Why this is better than GetCameraPose():
///   - Uses Unity's own transform hierarchy — always correct regardless of where
///     OVRCameraRig is positioned/scaled in world space.
///   - No dependency on MRUKNativeFuncs.GetHeadsetPoseAtTime being available.
///   - LensOffset is a fixed calibrated value — no per-frame API lookup needed.
/// </summary>
[DefaultExecutionOrder(-50)]
public class PassthroughCameraPoseTracker : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;

    private void Update()
    {
        if (passthroughCameraAccess == null || !passthroughCameraAccess.IsPlaying)
            return;

        var lensOffset = passthroughCameraAccess.Intrinsics.LensOffset;
        transform.localPosition = lensOffset.position;
        transform.localRotation = lensOffset.rotation;
    }
}
