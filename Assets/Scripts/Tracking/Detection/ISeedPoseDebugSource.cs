using UnityEngine;

/// <summary>
/// Optional debug-only contract implemented by pose seed providers that want to expose the most
/// recent successfully-built world-space seed pose. Visualisers can use this to render the seed
/// pose directly (i.e. before any downstream tracker — e.g. SRT3D — has had a chance to optimise
/// it), which is useful for separating "seed construction was wrong" bugs from "tracker drifted
/// after the seed" bugs.
/// </summary>
public interface ISeedPoseDebugSource
{
    /// <summary>
    /// Returns true if the provider has built at least one successful seed pose since startup.
    /// The returned pose stays valid (frozen) until a new seed is generated, so it can be used
    /// as a stable visual reference during the SRT3D probation window or normal tracking.
    /// </summary>
    bool TryGetLastSeedWorldPose(out Pose worldPose, out int classId, out string label);

    /// <summary>
    /// Returns the exact OpenCV/SRT3D camera-space initial pose matrix that was handed to the
    /// tracker, plus the Unity camera pose used to build that camera-space matrix. Visualisers can
    /// convert this matrix back to Unity and apply the inverse template correction to verify the
    /// seed conversion path independently from SRT3D's first optimisation frame.
    /// </summary>
    bool TryGetLastInitialSrt3dPose(
        out float[] rowMajorPose16,
        out Pose poseReference,
        out int classId,
        out string label);

    /// <summary>
    /// Returns the same initial SRT3D pose after a full Unity -> OpenCV row-major -> Unity
    /// round-trip and inverse template correction. This should overlap the yellow seed pose.
    /// </summary>
    bool TryGetLastInitialSrt3dWorldPose(out Pose worldPose, out int classId, out string label);

    /// <summary>
    /// Returns the per-class template-frame correction quaternion that the provider applies on
    /// the seed BEFORE handing it to the downstream tracker (and therefore that downstream
    /// visualisers must apply the inverse of, AFTER receiving the tracker's output, to project
    /// the wireframe back to the physical pose). Returns false if the provider has no profile
    /// for the given class (in which case visualisers should skip the inverse correction).
    /// </summary>
    bool TryGetTemplateFrameCorrectionForClass(int classId, out Quaternion correction);
}
