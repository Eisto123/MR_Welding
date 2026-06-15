#if !UNITY_WSA_10_0
using System.Collections.Generic;
using Meta.XR;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using CvRect = OpenCVForUnity.CoreModule.Rect;

/// <summary>
/// Builds the initial SRT3D camera-space pose from a YOLO detection and Meta environment raycasts.
/// Output convention is native SRT3D's T_cam_obj, row-major.
/// </summary>
public class YoloEnvironmentPoseSeedProvider : MonoBehaviour, ITrackingPoseSeedProvider, ITrackingPoseSeedRetryStrategy, ISeedPoseDebugSource
{
    private enum NativePoseCameraConvention
    {
        OpenCvYDown,
        UnityCameraYUp
    }

    private enum ModelSurfaceNormalAxis
    {
        PositiveY,
        PositiveZ,
        NegativeY,
        NegativeZ,
        PositiveX,
        NegativeX
    }

    [System.Serializable]
    private struct ClassPoseProfile
    {
        public int ClassId;
        public string Label;
        [Tooltip("Object-local axis that points along the world support-plane normal (i.e. the model's UP axis when sitting on a table).")]
        public ModelSurfaceNormalAxis SurfaceNormalAxis;
        public Vector2 FootprintSizeMeters;
        public bool UseBoundingBoxAspectYaw;
        public float AspectYawSign;
        public float YawOffsetDegrees;
        public Vector3 LocalRotationOffsetEuler;
        [Tooltip("Distance from the model's origin to its bottom face along the model's up axis. Equals -mesh.bounds.min.y for a Y-up model.")]
        public float OriginToSupportOffsetMeters;
        [Tooltip("Heuristic drop along world up applied to the raycast hit. Use this when the YOLO bbox center reliably hits an upright face of the object instead of the table (e.g. TShape's standing block).")]
        public float FallbackVerticalDropMeters;
        [Tooltip("Local Euler rotation, in mesh/body coordinates, applied only to the pose sent to SRT3D. Keep this at zero when the .srt3d file was generated with identity geometry2body and the native Body also uses identity geometry2body. The yellow seed wireframe keeps the physical mesh pose; cyan shows this initial matrix after a round-trip through the SRT3D pose format.")]
        public Vector3 TemplateFrameCorrectionEuler;
    }

    [Header("Dependencies")]
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;
    [SerializeField] private EnvironmentRaycastManager raycastManager;
    [SerializeField] private YoloDetector yoloDetector;

    [Header("Detection Filter")]
    [Tooltip("Use -1 to accept all YOLO classes.")]
    [SerializeField] private int targetClassId = -1;
    [SerializeField, Range(0f, 1f)] private float minimumYoloConfidence = 0.45f;

    [Header("Environment Raycast")]
    [SerializeField] private float maxRaycastDistance = 100f;
    [SerializeField] private bool debugDrawRays = false;
    [Tooltip("When ON the support plane normal is locked to world up regardless of what the depth raycast reports. The seed's tilt (X/Z rotation) is then fully determined by the model's canonical 'lying flat' orientation (per ClassPoseProfile.SurfaceNormalAxis), and only yaw (rotation around world up) is estimated from the YOLO bounding box. This is the recommended setting for tabletop objects: it eliminates jittery tilt caused by Quest depth noise and uneven surfaces. Turn OFF only if you want to use the raw hit normal (e.g. for objects placed on tilted ramps).")]
    [SerializeField] private bool assumeFlatHorizontalSurface = true;
    [Tooltip("Only used when 'Assume Flat Horizontal Surface' is OFF. Below this dot product against world up the centre ray hit is treated as having landed on a vertical face; the support plane is then assumed horizontal and FallbackVerticalDropMeters is applied.")]
    [SerializeField, Range(0f, 1f)] private float minimumSupportPlaneWorldUpDot = 0.7f;

    [Header("Class Pose Profiles")]
    [Tooltip("SurfaceNormalAxis describes the physical OBJ mesh pose used by the yellow wireframe. Keep it separate from TemplateFrameCorrectionEuler, which compensates SRT3D's template/body frame before/after native tracking.")]
    [SerializeField] private ClassPoseProfile[] classPoseProfiles =
    {
        new ClassPoseProfile
        {
            ClassId = 0,
            Label = "ButtShape",
            // Mesh +Y is physically up; SurfaceNormalAxis tells the seed builder which mesh axis
            // sits on the table. PositiveY is correct for Y-up .obj files.
            SurfaceNormalAxis = ModelSurfaceNormalAxis.PositiveY,
            FootprintSizeMeters = new Vector2(0.15f, 0.10f),
            UseBoundingBoxAspectYaw = true,
            AspectYawSign = 1f,
            YawOffsetDegrees = 0f,
            LocalRotationOffsetEuler = Vector3.zero,
            // mesh.bounds.min.y = -0.003 -> lift origin by 3mm so bottom face sits on the table.
            OriginToSupportOffsetMeters = 0.003f,
            FallbackVerticalDropMeters = 0f,
            TemplateFrameCorrectionEuler = Vector3.zero
        },
        new ClassPoseProfile
        {
            ClassId = 1,
            Label = "TShape",
            SurfaceNormalAxis = ModelSurfaceNormalAxis.PositiveY,
            FootprintSizeMeters = new Vector2(0.15f, 0.05f),
            UseBoundingBoxAspectYaw = true,
            AspectYawSign = 1f,
            YawOffsetDegrees = 0f,
            LocalRotationOffsetEuler = Vector3.zero,
            // mesh.bounds.min.y = -0.003 (bottom of horizontal plate) -> lift origin by 3mm.
            OriginToSupportOffsetMeters = 0.003f,
            FallbackVerticalDropMeters = 0f,
            TemplateFrameCorrectionEuler = Vector3.zero
        }
    };

    [Header("Yaw Estimation")]
    [SerializeField] private bool enableEdgeYawEstimation = false;
    [SerializeField] private int minRoiSizePx = 20;
    [SerializeField] private int cannyThresholdLow = 60;
    [SerializeField] private int cannyThresholdHigh = 160;
    [SerializeField] private int houghThreshold = 20;
    [SerializeField] private float minLineLengthRatio = 0.45f;
    [SerializeField] private float houghMaxLineGap = 6f;

    [Header("Seed Pose Offsets")]
    [Tooltip("Applied in object local space after the raycast/edge orientation is built.")]
    [SerializeField] private Vector3 seedPositionOffsetMeters = Vector3.zero;
    [Tooltip("Extra local Euler degrees applied after the class profile correction. Tune this only if the OBJ axes need one more adjustment.")]
    [SerializeField] private Vector3 seedRotationOffsetEuler = Vector3.zero;
    [Tooltip("Camera-space pose convention sent to SRT3D. Use UnityCameraYUp when the native tracker follows correctly with TrackingWireframeOverlay.flipCvYToUnity OFF; use OpenCvYDown only if native image rows and SRT3D projection are verified to be true OpenCV top-left/Y-down.")]
    [SerializeField] private NativePoseCameraConvention nativePoseCameraConvention = NativePoseCameraConvention.UnityCameraYUp;

    [Header("Manual Yaw Calibration")]
    [Tooltip("OVR controller button that increments the seed yaw by 'Manual Yaw Step Degrees' on each press. The bbox-aspect yaw estimate is 180° ambiguous (and 90° ambiguous for nearly-square bboxes), so the seed yaw can be reflected even when SurfaceNormalAxis and the long-axis estimation are otherwise correct. Press this button while observing the yellow seed wireframe in the headset until the seed's long axis matches the real object. Each press logs the new total offset; the offset persists across YOLO scans until the scene reloads or 'Manual Yaw Calibration Degrees' is reset to 0.")]
    [SerializeField] private OVRInput.RawButton manualYawStepButton = OVRInput.RawButton.B;
    [SerializeField] private float manualYawStepDegrees = 90f;
    [Tooltip("Current cumulative manual yaw offset added to every seed yaw. Increments by 'Manual Yaw Step Degrees' on each button press. Set this directly in the inspector to bake a known-good offset, or use the button at runtime to discover one.")]
    [SerializeField] private float manualYawCalibrationDegrees = 0f;

    [Header("Automatic Yaw Retry")]
    [Tooltip("When a bbox-aspect seed is rejected before SRT3D confirms it, try the opposite yaw direction first, then small yaw offsets on both directions.")]
    [SerializeField] private bool enableAutomaticYawRetry = true;
    [Tooltip("Step size for automatic small yaw retries after original/opposite have both failed.")]
    [SerializeField, Range(1f, 10f)] private float yawRetryMicroStepDegrees = 1f;
    [Tooltip("Largest automatic small yaw adjustment to try after original/opposite have both failed.")]
    [SerializeField, Range(1f, 20f)] private float yawRetryMaxMicroAdjustmentDegrees = 5f;

    [Header("Debug")]
    [SerializeField] private bool debugLogging = false;

    [Header("Debug Markers (visible in headset)")]
    [Tooltip("Spawns small GameObjects at the raycast hit point and seed pose so you can verify in passthrough whether YOLO+depth landed correctly, separately from the SRT3D round-trip rendering.")]
    [SerializeField] private bool showDebugMarkersInScene = false;
    [Tooltip("Diameter / cube side length for the hit and seed point markers.")]
    [SerializeField, Range(0.005f, 0.10f)] private float pointMarkerSizeMeters = 0.02f;
    [Tooltip("Length of each axis bar from the seed point.")]
    [SerializeField, Range(0.02f, 0.30f)] private float axisMarkerLengthMeters = 0.10f;
    [Tooltip("Cross-section of each axis bar.")]
    [SerializeField, Range(0.001f, 0.02f)] private float axisMarkerThicknessMeters = 0.005f;

    private Texture2D frameTexture2D;
    private Mat frameRgbaMat;
    private Mat frameGrayMat;
    private GameObject hitPointMarker;
    private GameObject seedPointMarker;
    private GameObject localAxisXMarker;
    private GameObject localAxisYMarker;
    private GameObject localAxisZMarker;

    private bool hasLastSeedWorldPose;
    private Pose lastSeedWorldPose;
    private int lastSeedClassId = -1;
    private string lastSeedLabel;
    private bool hasLastInitialSrt3dPose;
    private readonly float[] lastInitialSrt3dRowMajorPose = new float[16];
    private Pose lastInitialSrt3dPoseReference;
    private int lastInitialSrt3dClassId = -1;
    private string lastInitialSrt3dLabel;
    private bool hasLastInitialSrt3dWorldPose;
    private Pose lastInitialSrt3dWorldPose;
    private float lastInitialRoundTripPositionError;
    private float lastInitialRoundTripAngleError;
    private int yawRetryCandidateIndex;
    private int yawRetryClassId = -1;
    private bool lastSeedUsedBboxAspectYaw;
    private string lastYawRetryCandidateLabel = "original";

    public bool IsReady =>
        passthroughCameraAccess != null &&
        raycastManager != null &&
        yoloDetector != null &&
        yoloDetector.IsModelLoaded;

    public bool TryGetLastSeedWorldPose(out Pose worldPose, out int classId, out string label)
    {
        if (!hasLastSeedWorldPose)
        {
            worldPose = default;
            classId = -1;
            label = null;
            return false;
        }

        worldPose = lastSeedWorldPose;
        classId = lastSeedClassId;
        label = lastSeedLabel;
        return true;
    }

    public bool TryGetLastInitialSrt3dPose(
        out float[] rowMajorPose16,
        out Pose poseReference,
        out int classId,
        out string label)
    {
        if (!hasLastInitialSrt3dPose)
        {
            rowMajorPose16 = null;
            poseReference = default;
            classId = -1;
            label = null;
            return false;
        }

        rowMajorPose16 = new float[16];
        System.Array.Copy(lastInitialSrt3dRowMajorPose, rowMajorPose16, 16);
        poseReference = lastInitialSrt3dPoseReference;
        classId = lastInitialSrt3dClassId;
        label = lastInitialSrt3dLabel;
        return true;
    }

    public bool TryGetLastInitialSrt3dWorldPose(out Pose worldPose, out int classId, out string label)
    {
        if (!hasLastInitialSrt3dWorldPose)
        {
            worldPose = default;
            classId = -1;
            label = null;
            return false;
        }

        worldPose = lastInitialSrt3dWorldPose;
        classId = lastInitialSrt3dClassId;
        label = lastInitialSrt3dLabel;
        return true;
    }

    public bool TryGetTemplateFrameCorrectionForClass(int classId, out Quaternion correction)
    {
        if (classPoseProfiles != null)
        {
            for (int i = 0; i < classPoseProfiles.Length; i++)
            {
                if (classPoseProfiles[i].ClassId == classId)
                {
                    correction = Quaternion.Euler(classPoseProfiles[i].TemplateFrameCorrectionEuler);
                    return true;
                }
            }
        }

        correction = Quaternion.identity;
        return false;
    }

    public void ResetSeedRetryStrategy()
    {
        yawRetryCandidateIndex = 0;
        yawRetryClassId = -1;
        lastSeedUsedBboxAspectYaw = false;
        lastYawRetryCandidateLabel = "original";
    }

    public void NotifySeedRejected(string reason)
    {
        if (!enableAutomaticYawRetry || !lastSeedUsedBboxAspectYaw)
            return;

        yawRetryCandidateIndex = GetNextYawRetryCandidateIndex(yawRetryCandidateIndex);
        lastYawRetryCandidateLabel = GetYawRetryCandidateLabel(yawRetryCandidateIndex);

        if (debugLogging)
        {
            Debug.Log(
                $"[YoloEnvironmentPoseSeedProvider] Seed rejected ({reason}); " +
                $"next automatic yaw retry={lastYawRetryCandidateLabel}.");
        }
    }

    public void NotifySeedConfirmed()
    {
        if (debugLogging && yawRetryCandidateIndex != 0)
            Debug.Log("[YoloEnvironmentPoseSeedProvider] Seed confirmed; automatic yaw retry reset to original.");

        ResetSeedRetryStrategy();
    }

    public bool TryGetSeedPose(FramePacket framePacket, out TrackingPoseSeed seed, out string debugInfo)
    {
        seed = default;
        debugInfo = null;

        if (passthroughCameraAccess == null)
        {
            debugInfo = "PassthroughCameraAccess is missing.";
            return false;
        }

        if (raycastManager == null)
        {
            debugInfo = "EnvironmentRaycastManager is missing.";
            return false;
        }

        if (yoloDetector == null)
        {
            debugInfo = "YoloDetector is missing.";
            return false;
        }

        Texture cameraTexture = passthroughCameraAccess.GetTexture();
        if (cameraTexture == null)
        {
            debugInfo = "Passthrough camera texture is not ready.";
            return false;
        }

        EnsureDetectorInitialized(cameraTexture.width, cameraTexture.height);
        if (!yoloDetector.IsReady)
        {
            debugInfo = "YOLO detector is not ready.";
            return false;
        }

        long seedStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        float yoloInferenceMs;
        try
        {
            long yoloStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            yoloDetector.DetectObjects(cameraTexture, null);
            yoloInferenceMs = TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - yoloStartTicks);
        }
        catch (System.Exception e)
        {
            debugInfo = "YOLO detection failed: " + e.Message;
            return false;
        }

        List<YoloDetector.Detection> detections = yoloDetector.LatestDetections;
        if (!TrySelectBestDetection(detections, out YoloDetector.Detection bestDetection))
        {
            debugInfo = "No YOLO detection passed the filter.";
            return false;
        }

        Pose cameraPose = GetCameraPose(framePacket);
        int imageWidth = Mathf.Max(1, cameraTexture.width);
        int imageHeight = Mathf.Max(1, cameraTexture.height);
        ClassPoseProfile poseProfile = GetPoseProfile(bestDetection.ClassId);
        EnsureYawRetryClass(bestDetection.ClassId);
        lastSeedUsedBboxAspectYaw = false;
        lastYawRetryCandidateLabel = GetYawRetryCandidateLabel(yawRetryCandidateIndex);

        bool hasGrayFrame = false;
        if (enableEdgeYawEstimation && !poseProfile.UseBoundingBoxAspectYaw)
        {
            try
            {
                hasGrayFrame = TryBuildGrayFrame(cameraTexture);
            }
            catch (System.Exception e)
            {
                if (debugLogging)
                    Debug.LogWarning("[YoloEnvironmentPoseSeedProvider] Edge frame build failed: " + e.Message);
            }
        }

        if (!TryBuildWorldPose(
                bestDetection,
                poseProfile,
                cameraPose,
                imageWidth,
                imageHeight,
                hasGrayFrame,
                out Pose worldPose,
                out Vector3 rawHitPoint,
                out string poseInfo))
        {
            debugInfo = poseInfo;
            return false;
        }
        float depthQueryMs = TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - seedStartTicks) - yoloInferenceMs;

        Quaternion seedRotationOffset =
            Quaternion.Euler(poseProfile.LocalRotationOffsetEuler) *
            Quaternion.Euler(seedRotationOffsetEuler);
        worldPose.rotation *= seedRotationOffset;
        worldPose.position += worldPose.rotation * seedPositionOffsetMeters;

        UpdateOrHideDebugMarkers(showDebugMarkersInScene, rawHitPoint, worldPose.position, worldPose.rotation);

        // Cache the PHYSICAL world pose (matches reality, drawn as the yellow seed wireframe)
        // BEFORE applying TemplateFrameCorrection. The correction is purely a SRT3D-template-
        // convention adjustment and must not pollute the debug visualisation.
        hasLastSeedWorldPose = true;
        lastSeedWorldPose = worldPose;
        lastSeedClassId = bestDetection.ClassId;
        lastSeedLabel = GetClassLabel(bestDetection.ClassId);

        // Apply the template-frame correction in mesh-local frame. This rotates only the pose
        // handed to SRT3D; the cached yellow seed pose remains in the physical mesh frame. The
        // wireframe overlay applies the inverse correction to SRT3D output before rendering.
        Quaternion templateCorrection = Quaternion.Euler(poseProfile.TemplateFrameCorrectionEuler);
        Pose srt3dPose = new Pose(worldPose.position, worldPose.rotation * templateCorrection);

        if (!TryConvertWorldPoseToNativeCameraRowMajor(
                srt3dPose,
                cameraPose,
                nativePoseCameraConvention,
                out float[] rowMajorPose16))
        {
            debugInfo = "Failed to convert world pose to native camera pose.";
            return false;
        }

        hasLastInitialSrt3dPose = true;
        System.Array.Copy(rowMajorPose16, lastInitialSrt3dRowMajorPose, 16);
        lastInitialSrt3dPoseReference = cameraPose;
        lastInitialSrt3dClassId = bestDetection.ClassId;
        lastInitialSrt3dLabel = lastSeedLabel;

        hasLastInitialSrt3dWorldPose = false;
        lastInitialRoundTripPositionError = -1f;
        lastInitialRoundTripAngleError = -1f;
        if (PoseConverter.TryBuildUnityPose(
                rowMajorPose16,
                nativePoseCameraConvention == NativePoseCameraConvention.OpenCvYDown,
                1f,
                true,
                cameraPose.position,
                cameraPose.rotation,
                out Vector3 initialWorldPosition,
                out Quaternion initialWorldRotation))
        {
            initialWorldRotation *= Quaternion.Inverse(templateCorrection);
            lastInitialSrt3dWorldPose = new Pose(initialWorldPosition, initialWorldRotation);
            hasLastInitialSrt3dWorldPose = true;
            lastInitialRoundTripPositionError = Vector3.Distance(worldPose.position, initialWorldPosition);
            lastInitialRoundTripAngleError = Quaternion.Angle(worldPose.rotation, initialWorldRotation);
        }

        seed = new TrackingPoseSeed
        {
            RowMajorPose16 = rowMajorPose16,
            ClassId = bestDetection.ClassId,
            Label = lastSeedLabel,
            YoloConfidence = bestDetection.Confidence,
            YoloBoxX = bestDetection.Box.x,
            YoloBoxY = bestDetection.Box.y,
            YoloBoxWidth = bestDetection.Box.width,
            YoloBoxHeight = bestDetection.Box.height,
            DepthQuerySuccess = true,
            InitialDepthMeters = Vector3.Distance(cameraPose.position, rawHitPoint),
            InitialWorldPosition = worldPose.position,
            InitialWorldRotation = worldPose.rotation,
            RetryCandidate = lastYawRetryCandidateLabel,
            YoloInferenceMs = yoloInferenceMs,
            DepthQueryMs = Mathf.Max(0f, depthQueryMs),
            SeedBuildMs = TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - seedStartTicks)
        };

        debugInfo =
            $"class={bestDetection.ClassId}({seed.Label}), conf={bestDetection.Confidence:F2}, " +
            $"box=({bestDetection.Box.x},{bestDetection.Box.y},{bestDetection.Box.width},{bestDetection.Box.height}), " +
            $"{poseInfo}, tmplCorrEuler=({poseProfile.TemplateFrameCorrectionEuler.x:F0},{poseProfile.TemplateFrameCorrectionEuler.y:F0},{poseProfile.TemplateFrameCorrectionEuler.z:F0}), " +
            $"nativePoseConvention={nativePoseCameraConvention}, " +
            $"initRtErr=({lastInitialRoundTripPositionError:F4}m,{lastInitialRoundTripAngleError:F1}deg), " +
            $"nativeT=({rowMajorPose16[3]:F3},{rowMajorPose16[7]:F3},{rowMajorPose16[11]:F3})";

        if (debugLogging)
            Debug.Log("[YoloEnvironmentPoseSeedProvider] " + debugInfo);

        return true;
    }

    private void EnsureDetectorInitialized(int width, int height)
    {
        if (yoloDetector == null || yoloDetector.IsReady || !yoloDetector.IsModelLoaded)
            return;

        yoloDetector.Initialize(width, height);
    }

    private bool TrySelectBestDetection(List<YoloDetector.Detection> detections, out YoloDetector.Detection bestDetection)
    {
        bestDetection = default;
        if (detections == null || detections.Count == 0)
            return false;

        bool found = false;
        float bestScore = float.MinValue;
        for (int i = 0; i < detections.Count; i++)
        {
            YoloDetector.Detection detection = detections[i];
            if (targetClassId >= 0 && detection.ClassId != targetClassId)
                continue;
            if (detection.Confidence < minimumYoloConfidence)
                continue;

            if (!found || detection.Confidence > bestScore)
            {
                found = true;
                bestScore = detection.Confidence;
                bestDetection = detection;
            }
        }

        return found;
    }

    private string GetClassLabel(int classId)
    {
        List<string> labels = yoloDetector != null ? yoloDetector.Classes : null;
        if (labels != null && classId >= 0 && classId < labels.Count)
            return labels[classId];

        return $"class{classId}";
    }

    private ClassPoseProfile GetPoseProfile(int classId)
    {
        if (classPoseProfiles != null)
        {
            for (int i = 0; i < classPoseProfiles.Length; i++)
            {
                if (classPoseProfiles[i].ClassId == classId)
                    return NormalizedPoseProfile(classPoseProfiles[i]);
            }
        }

        return NormalizedPoseProfile(new ClassPoseProfile
        {
            ClassId = classId,
            Label = GetClassLabel(classId),
            SurfaceNormalAxis = ModelSurfaceNormalAxis.PositiveY,
            FootprintSizeMeters = GetDefaultFootprintForClass(classId),
            UseBoundingBoxAspectYaw = true,
            AspectYawSign = 1f,
            YawOffsetDegrees = 0f,
            LocalRotationOffsetEuler = Vector3.zero
        });
    }

    private ClassPoseProfile NormalizedPoseProfile(ClassPoseProfile profile)
    {
        if (profile.FootprintSizeMeters.x <= 0f || profile.FootprintSizeMeters.y <= 0f)
            profile.FootprintSizeMeters = GetDefaultFootprintForClass(profile.ClassId);

        // The serialized OriginToSupportOffsetMeters depends on which mesh-local axis is treated as
        // the "support normal". Provide axis-aware defaults for the known classes so existing
        // serialized scenes with OriginToSupportOffsetMeters == 0 still place the mesh bottom on the
        // table after a SurfaceNormalAxis change in the inspector.
        if (profile.OriginToSupportOffsetMeters <= 0f)
            profile.OriginToSupportOffsetMeters =
                GetDefaultOriginOffsetForClass(profile.ClassId, profile.SurfaceNormalAxis);

        if (Mathf.Abs(profile.AspectYawSign) < 1e-4f)
            profile.AspectYawSign = 1f;
        if (string.IsNullOrWhiteSpace(profile.Label))
            profile.Label = GetClassLabel(profile.ClassId);

        return profile;
    }

    private static float GetDefaultOriginOffsetForClass(int classId, ModelSurfaceNormalAxis surfaceNormalAxis)
    {
        switch (classId)
        {
            case 0: // ButtShape: mesh bounds X[-0.075,0.075] Y[-0.003,0.003] Z[-0.05,0.05]
                switch (surfaceNormalAxis)
                {
                    case ModelSurfaceNormalAxis.PositiveY: return 0.003f;
                    case ModelSurfaceNormalAxis.NegativeY: return 0.003f;
                    case ModelSurfaceNormalAxis.PositiveZ: return 0.050f;
                    case ModelSurfaceNormalAxis.NegativeZ: return 0.050f;
                    case ModelSurfaceNormalAxis.PositiveX: return 0.075f;
                    case ModelSurfaceNormalAxis.NegativeX: return 0.075f;
                }
                break;
            case 1: // TShape: mesh bounds X[-0.075,0.075] Y[-0.003,0.053] Z[-0.025,0.025]
                switch (surfaceNormalAxis)
                {
                    case ModelSurfaceNormalAxis.PositiveY: return 0.003f; // bottom of horizontal bar
                    case ModelSurfaceNormalAxis.NegativeY: return 0.053f; // top of vertical bar
                    case ModelSurfaceNormalAxis.PositiveZ: return 0.025f;
                    case ModelSurfaceNormalAxis.NegativeZ: return 0.025f;
                    case ModelSurfaceNormalAxis.PositiveX: return 0.075f;
                    case ModelSurfaceNormalAxis.NegativeX: return 0.075f;
                }
                break;
        }
        return 0f;
    }

    private static Vector2 GetDefaultFootprintForClass(int classId)
    {
        switch (classId)
        {
            case 0:
                return new Vector2(0.15f, 0.10f);
            case 1:
                return new Vector2(0.15f, 0.05f);
            default:
                return new Vector2(0.15f, 0.05f);
        }
    }

    private Pose GetCameraPose(FramePacket framePacket)
    {
        if (framePacket.HasPoseReference)
            return new Pose(framePacket.PoseReferencePosition, framePacket.PoseReferenceRotation);

        return passthroughCameraAccess.GetCameraPose();
    }

    private void EnsureYawRetryClass(int classId)
    {
        if (yawRetryClassId == classId)
            return;

        yawRetryCandidateIndex = 0;
        yawRetryClassId = classId;
        lastSeedUsedBboxAspectYaw = false;
        lastYawRetryCandidateLabel = "original";
    }

    private int GetNextYawRetryCandidateIndex(int currentIndex)
    {
        int candidateCount = Mathf.Max(1, GetYawRetryCandidateCount());
        return (currentIndex + 1) % candidateCount;
    }

    private int GetYawRetryCandidateCount()
    {
        if (!enableAutomaticYawRetry)
            return 1;

        return 2 + GetYawRetryMicroDegreeCount() * 4;
    }

    private int GetYawRetryMicroDegreeCount()
    {
        if (yawRetryMaxMicroAdjustmentDegrees <= 1e-3f)
            return 0;

        float step = Mathf.Max(0.25f, yawRetryMicroStepDegrees);
        return Mathf.Max(1, Mathf.CeilToInt(yawRetryMaxMicroAdjustmentDegrees / step));
    }

    private string GetYawRetryCandidateLabel(int candidateIndex)
    {
        GetYawRetryCandidate(candidateIndex, out _, out _, out string label);
        return label;
    }

    private float GetCurrentYawRetryDirectionMultiplier()
    {
        GetYawRetryCandidate(yawRetryCandidateIndex, out float yawDirectionMultiplier, out _, out _);
        return yawDirectionMultiplier;
    }

    private float GetCurrentYawRetryAdjustmentDegrees()
    {
        GetYawRetryCandidate(yawRetryCandidateIndex, out _, out float yawAdjustmentDegrees, out _);
        return yawAdjustmentDegrees;
    }

    private void GetYawRetryCandidate(
        int candidateIndex,
        out float yawDirectionMultiplier,
        out float yawAdjustmentDegrees,
        out string label)
    {
        yawDirectionMultiplier = 1f;
        yawAdjustmentDegrees = 0f;
        label = "original";

        if (!enableAutomaticYawRetry || candidateIndex <= 0)
            return;

        if (candidateIndex == 1 || GetYawRetryMicroDegreeCount() <= 0)
        {
            yawDirectionMultiplier = -1f;
            label = "opposite";
            return;
        }

        int microCandidateCount = Mathf.Max(1, GetYawRetryMicroDegreeCount() * 4);
        int microSlot = (candidateIndex - 2) % microCandidateCount;
        int degreeIndex = microSlot / 4;
        int slotInDegree = microSlot % 4;

        float step = Mathf.Max(0.25f, yawRetryMicroStepDegrees);
        float degrees = Mathf.Min(yawRetryMaxMicroAdjustmentDegrees, (degreeIndex + 1) * step);
        bool opposite = slotInDegree == 1 || slotInDegree == 3;
        bool negativeAdjustment = slotInDegree >= 2;

        yawDirectionMultiplier = opposite ? -1f : 1f;
        yawAdjustmentDegrees = negativeAdjustment ? -degrees : degrees;
        string adjustmentSign = yawAdjustmentDegrees >= 0f ? "+" : "";
        label = $"{(opposite ? "opposite" : "original")}{adjustmentSign}{yawAdjustmentDegrees:F1}deg";
    }

    private bool TryBuildWorldPose(
        YoloDetector.Detection detection,
        ClassPoseProfile poseProfile,
        Pose cameraPose,
        int imageWidth,
        int imageHeight,
        bool hasGrayFrame,
        out Pose worldPose,
        out Vector3 rawHitPoint,
        out string debugInfo)
    {
        worldPose = Pose.identity;
        rawHitPoint = Vector3.zero;
        debugInfo = null;

        float centerX = detection.Box.x + detection.Box.width * 0.5f;
        float centerY = detection.Box.y + detection.Box.height * 0.5f;
        Vector2 centerViewport = PixelToViewport(new Vector2(centerX, centerY), imageWidth, imageHeight);

        Ray centerRay = passthroughCameraAccess.ViewportPointToRay(centerViewport, cameraPose);
        if (!TryFindSupportPlaneHit(
                detection,
                centerViewport,
                cameraPose,
                imageWidth,
                imageHeight,
                out EnvironmentRaycastHit supportHit,
                out Ray supportRay,
                out bool usedSupportSamples))
        {
            debugInfo = $"Environment raycast missed at viewport=({centerViewport.x:F3},{centerViewport.y:F3}).";
            return false;
        }

        rawHitPoint = supportHit.point;

        Vector3 rawHitNormal = supportHit.normal.sqrMagnitude > 1e-8f
            ? supportHit.normal.normalized
            : Vector3.up;
        if (Vector3.Dot(rawHitNormal, Vector3.up) < 0f)
            rawHitNormal = -rawHitNormal;

        // Decide which plane normal drives the seed orientation. In tabletop scenarios the
        // raw depth normal is noisy / slightly tilted, which leaks into the seed's X/Z rotation
        // and causes the wireframe to "lean" relative to the real object. When
        // assumeFlatHorizontalSurface is on we lock the support plane to world up, so the seed
        // pose's tilt is fully determined by the per-class SurfaceNormalAxis correction (i.e.
        // the model's "lying flat" orientation) and only yaw is estimated from YOLO.
        bool hitLooksHorizontal;
        Vector3 planeNormal;
        if (assumeFlatHorizontalSurface)
        {
            hitLooksHorizontal = true;
            planeNormal = Vector3.up;
        }
        else
        {
            // If the centre ray landed on a clearly non-horizontal surface (e.g. the upright face
            // of TShape's standing block), assume the real support plane is horizontal under the
            // hit. The seed will only be approximately right but SRT3D only needs to be inside
            // its convergence basin, and per-class FallbackVerticalDropMeters tunes the offset.
            hitLooksHorizontal = Vector3.Dot(rawHitNormal, Vector3.up) >= minimumSupportPlaneWorldUpDot;
            planeNormal = hitLooksHorizontal ? rawHitNormal : Vector3.up;
        }

        Vector3 seedPoint = supportHit.point;
        if (hitLooksHorizontal && !assumeFlatHorizontalSurface)
        {
            // Project the centre ray onto the actual support plane (sub-mm accurate when normal
            // is good). When assumeFlatHorizontalSurface is on the hit point already lies on a
            // world-horizontal plane through itself, so the projection is a no-op and we skip it.
            Plane supportPlane = new Plane(planeNormal, supportHit.point);
            if (TryProjectRayToPlane(centerRay, supportPlane, out Vector3 centerOnSupportPlane))
                seedPoint = centerOnSupportPlane;
        }

        // Push the seed onto a virtual table by `FallbackVerticalDropMeters` along world up.
        // This compensates the case where the ray hit the standing portion of the object rather
        // than the table itself.
        if (poseProfile.FallbackVerticalDropMeters > 0f)
            seedPoint -= Vector3.up * poseProfile.FallbackVerticalDropMeters;

        // Lift the origin so that mesh.bounds.min along the up axis lands on the support plane
        // instead of below it (origin sits 3mm above the bottom face for both buttshape & tshape).
        if (poseProfile.OriginToSupportOffsetMeters > 0f)
            seedPoint += planeNormal * poseProfile.OriginToSupportOffsetMeters;

        Vector3 longAxisWorld = Vector3.zero;
        string yawSource = "fallback";
        float aspectYawDegrees = 0f;
        string yawRetryInfo = "none";
        if (poseProfile.UseBoundingBoxAspectYaw &&
            TryEstimateLongAxisFromBoundingBox(
                detection,
                poseProfile,
                cameraPose,
                planeNormal,
                GetCurrentYawRetryDirectionMultiplier(),
                GetCurrentYawRetryAdjustmentDegrees(),
                out longAxisWorld,
                out aspectYawDegrees))
        {
            yawSource = "bboxAspect";
            lastSeedUsedBboxAspectYaw = true;
            lastYawRetryCandidateLabel = GetYawRetryCandidateLabel(yawRetryCandidateIndex);
            yawRetryInfo = lastYawRetryCandidateLabel;
        }

        bool usedWorldEdge = false;

        if (longAxisWorld.sqrMagnitude < 1e-6f &&
            hasGrayFrame &&
            frameGrayMat != null &&
            TryExtractLongestEdge(frameGrayMat, detection, out Vector2 edgeStartPixel, out Vector2 edgeEndPixel))
        {
            Vector2 edgeStartViewport = PixelToViewport(edgeStartPixel, imageWidth, imageHeight);
            Vector2 edgeEndViewport = PixelToViewport(edgeEndPixel, imageWidth, imageHeight);

            Ray edgeStartRay = passthroughCameraAccess.ViewportPointToRay(edgeStartViewport, cameraPose);
            Ray edgeEndRay = passthroughCameraAccess.ViewportPointToRay(edgeEndViewport, cameraPose);

            if (debugDrawRays)
            {
                Debug.DrawRay(edgeStartRay.origin, edgeStartRay.direction * maxRaycastDistance, Color.green, 0.08f);
                Debug.DrawRay(edgeEndRay.origin, edgeEndRay.direction * maxRaycastDistance, Color.magenta, 0.08f);
            }

            Plane plane = new Plane(planeNormal, seedPoint);
            if (TryProjectRayToPlane(edgeStartRay, plane, out Vector3 worldStart) &&
                TryProjectRayToPlane(edgeEndRay, plane, out Vector3 worldEnd))
            {
                Vector3 edgeDirection = Vector3.ProjectOnPlane(worldEnd - worldStart, planeNormal);
                if (edgeDirection.sqrMagnitude > 1e-6f)
                {
                    usedWorldEdge = true;
                    longAxisWorld = edgeDirection.normalized;
                    yawSource = "edge";

                    if (debugDrawRays)
                        Debug.DrawLine(worldStart, worldEnd, Color.yellow, 0.1f);
                }
            }
        }

        if (longAxisWorld.sqrMagnitude < 1e-6f)
            longAxisWorld = GetCameraRightOnPlane(cameraPose, planeNormal);

        // Apply manual yaw calibration (rotation around the support plane normal). This is the
        // hook for resolving the bbox-aspect 180° ambiguity at runtime via 'Manual Yaw Step
        // Button': each press adds 'Manual Yaw Step Degrees' to this offset, allowing the user
        // to flip / rotate the seed yaw in the headset until the yellow seed wireframe matches
        // the real object.
        if (Mathf.Abs(manualYawCalibrationDegrees) > 1e-3f)
            longAxisWorld = Quaternion.AngleAxis(manualYawCalibrationDegrees, planeNormal) * longAxisWorld;

        Quaternion worldRotation = BuildObjectRotationFromLongAxisAndNormal(
            longAxisWorld,
            planeNormal,
            poseProfile.SurfaceNormalAxis);

        worldPose = new Pose(seedPoint, worldRotation);

        if (debugDrawRays)
        {
            const float axisLen = 0.10f;
            Debug.DrawRay(seedPoint, worldRotation * Vector3.right   * axisLen, Color.red,     2f);
            Debug.DrawRay(seedPoint, worldRotation * Vector3.up      * axisLen, Color.green,   2f);
            Debug.DrawRay(seedPoint, worldRotation * Vector3.forward * axisLen, Color.blue,    2f);
            Debug.DrawRay(supportHit.point, rawHitNormal * 0.05f,               Color.magenta, 2f);
            Debug.DrawLine(supportHit.point, seedPoint,                         Color.cyan,    2f);
        }

        Vector3 localUpInWorld = worldRotation * Vector3.up;
        debugInfo =
            $"raw={poseProfile.Label}, axis={poseProfile.SurfaceNormalAxis}, " +
            $"flatLock={assumeFlatHorizontalSurface}, " +
            $"hitPt=({supportHit.point.x:F3},{supportHit.point.y:F3},{supportHit.point.z:F3}) " +
            $"rawN=({rawHitNormal.x:F2},{rawHitNormal.y:F2},{rawHitNormal.z:F2}) horiz={hitLooksHorizontal}, " +
            $"seedPt=({seedPoint.x:F3},{seedPoint.y:F3},{seedPoint.z:F3}) " +
            $"localUp=({localUpInWorld.x:F2},{localUpInWorld.y:F2},{localUpInWorld.z:F2}), " +
            $"yaw={yawSource}:{aspectYawDegrees:F1} retry={yawRetryInfo} edgeYaw={usedWorldEdge} manualYaw={manualYawCalibrationDegrees:F0}, " +
            $"originOff={poseProfile.OriginToSupportOffsetMeters:F3} drop={poseProfile.FallbackVerticalDropMeters:F3} " +
            $"supportSamples={usedSupportSamples} supportDist={Vector3.Distance(supportRay.origin, supportHit.point):F3}";
        return true;
    }

    private bool TryRaycastViewportPoint(Vector2 viewportPoint, Pose cameraPose, out EnvironmentRaycastHit hit, out Ray ray)
    {
        hit = default;
        ray = default;

        if (passthroughCameraAccess == null || raycastManager == null)
            return false;

        ray = passthroughCameraAccess.ViewportPointToRay(viewportPoint, cameraPose);

        if (debugDrawRays)
            Debug.DrawRay(ray.origin, ray.direction * maxRaycastDistance, Color.cyan, 0.08f);

        if (raycastManager.Raycast(ray, out EnvironmentRaycastHit raycastHit, maxDistance: maxRaycastDistance) &&
            raycastHit.status == EnvironmentRaycastHitStatus.Hit)
        {
            hit = raycastHit;
            return true;
        }

        return false;
    }

    private bool TryFindSupportPlaneHit(
        YoloDetector.Detection detection,
        Vector2 centerViewport,
        Pose cameraPose,
        int imageWidth,
        int imageHeight,
        out EnvironmentRaycastHit bestHit,
        out Ray bestRay,
        out bool usedSupportSamples)
    {
        // Single centre-ray hit. Multi-sample picking is intentionally removed: when YOLO
        // expands the bbox the corner rays often skip past the actual support surface
        // (e.g. onto the keyboard behind), giving a worse seed than the centre alone.
        usedSupportSamples = false;
        return TryRaycastViewportPoint(centerViewport, cameraPose, out bestHit, out bestRay);
    }

    private bool TryBuildGrayFrame(Texture cameraTexture)
    {
        if (cameraTexture == null)
            return false;

        if (frameRgbaMat == null ||
            frameGrayMat == null ||
            frameRgbaMat.width() != cameraTexture.width ||
            frameRgbaMat.height() != cameraTexture.height)
        {
            InitializeProcessingMats(cameraTexture.width, cameraTexture.height);
        }

        Texture2D readableTexture = GetReadableTexture(cameraTexture);
        if (readableTexture == null || frameRgbaMat == null || frameGrayMat == null)
            return false;

        OpenCVMatUtils.Texture2DToMat(readableTexture, frameRgbaMat);
        Imgproc.cvtColor(frameRgbaMat, frameGrayMat, Imgproc.COLOR_RGBA2GRAY);
        return true;
    }

    private void InitializeProcessingMats(int width, int height)
    {
        frameRgbaMat?.Dispose();
        frameGrayMat?.Dispose();

        frameRgbaMat = new Mat(height, width, CvType.CV_8UC4);
        frameGrayMat = new Mat(height, width, CvType.CV_8UC1);
    }

    private Texture2D GetReadableTexture(Texture sourceTexture)
    {
        Texture2D sourceTexture2D = sourceTexture as Texture2D;
        if (sourceTexture2D != null)
            return sourceTexture2D;

        if (frameTexture2D == null ||
            frameTexture2D.width != sourceTexture.width ||
            frameTexture2D.height != sourceTexture.height)
        {
            if (frameTexture2D != null)
                Destroy(frameTexture2D);

            frameTexture2D = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        }

        RenderTexture currentRT = RenderTexture.active;
        RenderTexture tempRT = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);

        Graphics.Blit(sourceTexture, tempRT);
        RenderTexture.active = tempRT;

        frameTexture2D.ReadPixels(new UnityEngine.Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0);
        frameTexture2D.Apply(false, false);

        RenderTexture.active = currentRT;
        RenderTexture.ReleaseTemporary(tempRT);

        return frameTexture2D;
    }

    private bool TryExtractLongestEdge(Mat grayFrame, YoloDetector.Detection detection, out Vector2 edgeStartPixel, out Vector2 edgeEndPixel)
    {
        edgeStartPixel = Vector2.zero;
        edgeEndPixel = Vector2.zero;

        if (grayFrame == null || grayFrame.empty())
            return false;

        CvRect roiRect = CreateClampedRect(detection.Box, grayFrame.width(), grayFrame.height());
        if (roiRect.width < minRoiSizePx || roiRect.height < minRoiSizePx)
            return false;

        using (Mat roi = new Mat(grayFrame, roiRect))
        using (Mat blurred = new Mat())
        using (Mat edges = new Mat())
        using (Mat lines = new Mat())
        {
            Imgproc.GaussianBlur(roi, blurred, new Size(5, 5), 0);
            Imgproc.Canny(blurred, edges, cannyThresholdLow, cannyThresholdHigh);

            double minLineLength = Mathf.Max(roiRect.width, roiRect.height) * minLineLengthRatio;
            Imgproc.HoughLinesP(edges, lines, 1.0, Mathf.PI / 180f, houghThreshold, minLineLength, houghMaxLineGap);

            if (lines.rows() <= 0)
                return false;

            double bestLengthSquared = 0.0;
            double[] bestLine = null;

            for (int i = 0; i < lines.rows(); i++)
            {
                double[] line = lines.get(i, 0);
                if (line == null || line.Length < 4)
                    continue;

                double dx = line[2] - line[0];
                double dy = line[3] - line[1];
                double lengthSquared = dx * dx + dy * dy;

                if (lengthSquared > bestLengthSquared)
                {
                    bestLengthSquared = lengthSquared;
                    bestLine = line;
                }
            }

            if (bestLine == null)
                return false;

            edgeStartPixel = new Vector2((float)bestLine[0] + roiRect.x, (float)bestLine[1] + roiRect.y);
            edgeEndPixel = new Vector2((float)bestLine[2] + roiRect.x, (float)bestLine[3] + roiRect.y);
            return true;
        }
    }

    private static CvRect CreateClampedRect(CvRect box, int imageWidth, int imageHeight)
    {
        int left = Mathf.Clamp(box.x, 0, Mathf.Max(0, imageWidth - 1));
        int top = Mathf.Clamp(box.y, 0, Mathf.Max(0, imageHeight - 1));
        int right = Mathf.Clamp(box.x + box.width, left + 1, Mathf.Max(left + 1, imageWidth));
        int bottom = Mathf.Clamp(box.y + box.height, top + 1, Mathf.Max(top + 1, imageHeight));

        return new CvRect(left, top, right - left, bottom - top);
    }

    private static Vector2 PixelToViewport(Vector2 pixelPoint, int imageWidth, int imageHeight)
    {
        float u = Mathf.Clamp01(pixelPoint.x / Mathf.Max(1f, imageWidth));
        float v = Mathf.Clamp01(1f - pixelPoint.y / Mathf.Max(1f, imageHeight));
        return new Vector2(u, v);
    }

    private static bool TryProjectRayToPlane(Ray ray, Plane plane, out Vector3 worldPoint)
    {
        worldPoint = Vector3.zero;
        if (!plane.Raycast(ray, out float enter) || enter < 0f)
            return false;

        worldPoint = ray.GetPoint(enter);
        return true;
    }

    private static bool TryEstimateLongAxisFromBoundingBox(
        YoloDetector.Detection detection,
        ClassPoseProfile poseProfile,
        Pose cameraPose,
        Vector3 planeNormal,
        float yawDirectionMultiplier,
        float yawAdjustmentDegrees,
        out Vector3 longAxisWorld,
        out float signedYawDegrees)
    {
        longAxisWorld = Vector3.zero;
        signedYawDegrees = 0f;

        if (detection.Box.width <= 0 || detection.Box.height <= 0)
            return false;

        float footprintLong = Mathf.Max(poseProfile.FootprintSizeMeters.x, poseProfile.FootprintSizeMeters.y);
        float footprintShort = Mathf.Min(poseProfile.FootprintSizeMeters.x, poseProfile.FootprintSizeMeters.y);
        if (footprintLong <= 1e-5f || footprintShort <= 1e-5f)
            return false;

        float pixelAspect = detection.Box.width / Mathf.Max(1f, detection.Box.height);
        float unsignedYaw = EstimateUnsignedAabbYawDegrees(pixelAspect, footprintLong, footprintShort);
        float direction = Mathf.Sign(poseProfile.AspectYawSign) * Mathf.Sign(yawDirectionMultiplier);
        signedYawDegrees = unsignedYaw * direction + poseProfile.YawOffsetDegrees + yawAdjustmentDegrees;

        Vector3 cameraRightOnPlane = GetCameraRightOnPlane(cameraPose, planeNormal);
        longAxisWorld = Quaternion.AngleAxis(signedYawDegrees, planeNormal) * cameraRightOnPlane;
        longAxisWorld = Vector3.ProjectOnPlane(longAxisWorld, planeNormal);
        if (longAxisWorld.sqrMagnitude < 1e-6f)
            return false;

        longAxisWorld.Normalize();
        return true;
    }

    private static float EstimateUnsignedAabbYawDegrees(float pixelAspect, float footprintLong, float footprintShort)
    {
        float maxAspect = footprintLong / footprintShort;
        float minAspect = footprintShort / footprintLong;
        float aspect = Mathf.Clamp(pixelAspect, minAspect, maxAspect);

        if (aspect >= maxAspect - 1e-4f)
            return 0f;
        if (aspect <= minAspect + 1e-4f)
            return 90f;

        float numerator = footprintLong - aspect * footprintShort;
        float denominator = aspect * footprintLong - footprintShort;
        if (denominator <= 1e-6f)
            return 90f;

        return Mathf.Clamp(Mathf.Atan(numerator / denominator) * Mathf.Rad2Deg, 0f, 90f);
    }

    private static Vector3 GetCameraRightOnPlane(Pose cameraPose, Vector3 planeNormal)
    {
        Vector3 right = Vector3.ProjectOnPlane(cameraPose.rotation * Vector3.right, planeNormal);
        if (right.sqrMagnitude >= 1e-6f)
            return right.normalized;

        Vector3 forward = Vector3.ProjectOnPlane(cameraPose.rotation * Vector3.forward, planeNormal);
        if (forward.sqrMagnitude < 1e-6f)
        {
            forward = Vector3.ProjectOnPlane(Vector3.forward, planeNormal);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(Vector3.right, planeNormal);
        }

        right = Vector3.Cross(planeNormal, forward.normalized);
        return right.sqrMagnitude >= 1e-6f ? right.normalized : Vector3.right;
    }

    private static Quaternion BuildObjectRotationFromLongAxisAndNormal(
        Vector3 longAxisWorld,
        Vector3 surfaceNormal,
        ModelSurfaceNormalAxis surfaceNormalAxis)
    {
        Vector3 normal = surfaceNormal.sqrMagnitude > 1e-8f ? surfaceNormal.normalized : Vector3.up;
        Vector3 longAxis = Vector3.ProjectOnPlane(longAxisWorld, normal);
        if (longAxis.sqrMagnitude < 1e-6f)
            longAxis = Vector3.ProjectOnPlane(Vector3.right, normal);
        if (longAxis.sqrMagnitude < 1e-6f)
            longAxis = Vector3.ProjectOnPlane(Vector3.forward, normal);
        longAxis.Normalize();

        Vector3 forwardAxis = Vector3.Cross(longAxis, normal);
        if (forwardAxis.sqrMagnitude < 1e-6f)
            forwardAxis = Vector3.Cross(normal, Vector3.right);
        if (forwardAxis.sqrMagnitude < 1e-6f)
            forwardAxis = Vector3.Cross(normal, Vector3.forward);
        forwardAxis.Normalize();

        Quaternion baseRotation = Quaternion.LookRotation(forwardAxis, normal);
        return baseRotation * GetSurfaceNormalAxisCorrection(surfaceNormalAxis);
    }

    private static Quaternion GetSurfaceNormalAxisCorrection(ModelSurfaceNormalAxis axis)
    {
        switch (axis)
        {
            case ModelSurfaceNormalAxis.PositiveY:
                return Quaternion.identity;
            case ModelSurfaceNormalAxis.NegativeY:
                return Quaternion.Euler(180f, 0f, 0f);
            case ModelSurfaceNormalAxis.PositiveZ:
                return Quaternion.Euler(-90f, 0f, 0f);
            case ModelSurfaceNormalAxis.NegativeZ:
                return Quaternion.Euler(90f, 0f, 0f);
            case ModelSurfaceNormalAxis.PositiveX:
                return Quaternion.Euler(0f, 0f, 90f);
            case ModelSurfaceNormalAxis.NegativeX:
                return Quaternion.Euler(0f, 0f, -90f);
            default:
                return Quaternion.identity;
        }
    }

    private static bool TryConvertWorldPoseToNativeCameraRowMajor(
        Pose worldPose,
        Pose cameraPose,
        NativePoseCameraConvention convention,
        out float[] rowMajorPose16)
    {
        rowMajorPose16 = null;

        if (!IsFinite(worldPose.position) || !IsFinite(cameraPose.position) ||
            !IsFinite(worldPose.rotation) || !IsFinite(cameraPose.rotation))
        {
            return false;
        }

        Quaternion invCameraRotation = Quaternion.Inverse(cameraPose.rotation);
        Vector3 localPositionUnity = invCameraRotation * (worldPose.position - cameraPose.position);
        Quaternion localRotationUnity = invCameraRotation * worldPose.rotation;
        if (!IsFinite(localPositionUnity) || !IsFinite(localRotationUnity))
            return false;

        Matrix4x4 unityRotation = Matrix4x4.Rotate(localRotationUnity);

        rowMajorPose16 = new float[16];

        if (convention == NativePoseCameraConvention.UnityCameraYUp)
        {
            // Empirically, Quest passthrough frames delivered to the native tracker behave as a
            // Unity camera-space image: poses that round-trip with flipCvYToUnity=false are the
            // poses SRT3D can lock onto with high confidence.
            rowMajorPose16[0] = unityRotation.m00;
            rowMajorPose16[1] = unityRotation.m01;
            rowMajorPose16[2] = unityRotation.m02;
            rowMajorPose16[3] = localPositionUnity.x;

            rowMajorPose16[4] = unityRotation.m10;
            rowMajorPose16[5] = unityRotation.m11;
            rowMajorPose16[6] = unityRotation.m12;
            rowMajorPose16[7] = localPositionUnity.y;

            rowMajorPose16[8] = unityRotation.m20;
            rowMajorPose16[9] = unityRotation.m21;
            rowMajorPose16[10] = unityRotation.m22;
            rowMajorPose16[11] = localPositionUnity.z;
        }
        else
        {
            // R_cv = M * R_unity * M, p_cv = M * p_unity, M = diag(1, -1, 1).
            rowMajorPose16[0] = unityRotation.m00;
            rowMajorPose16[1] = -unityRotation.m01;
            rowMajorPose16[2] = unityRotation.m02;
            rowMajorPose16[3] = localPositionUnity.x;

            rowMajorPose16[4] = -unityRotation.m10;
            rowMajorPose16[5] = unityRotation.m11;
            rowMajorPose16[6] = -unityRotation.m12;
            rowMajorPose16[7] = -localPositionUnity.y;

            rowMajorPose16[8] = unityRotation.m20;
            rowMajorPose16[9] = -unityRotation.m21;
            rowMajorPose16[10] = unityRotation.m22;
            rowMajorPose16[11] = localPositionUnity.z;
        }

        rowMajorPose16[12] = 0f;
        rowMajorPose16[13] = 0f;
        rowMajorPose16[14] = 0f;
        rowMajorPose16[15] = 1f;
        return true;
    }

    private static bool IsFinite(Vector3 v)
    {
        return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
    }

    private static bool IsFinite(Quaternion q)
    {
        return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
    }

    private static bool IsFinite(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v);
    }

    private static float TicksToMilliseconds(long ticks)
    {
        return (float)(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    private void Update()
    {
        // Manual yaw calibration: each press advances the cumulative offset by step degrees,
        // wrapping at 360°. The new offset is applied on the very next seed and logged so the
        // user can see what value to bake into ClassPoseProfile.YawOffsetDegrees once a stable
        // working setting has been found.
        if (OVRInput.GetDown(manualYawStepButton))
        {
            manualYawCalibrationDegrees += manualYawStepDegrees;
            // Normalize to [-180, 180] for readability in the inspector.
            while (manualYawCalibrationDegrees > 180f) manualYawCalibrationDegrees -= 360f;
            while (manualYawCalibrationDegrees < -180f) manualYawCalibrationDegrees += 360f;
            Debug.Log($"[YoloEnvironmentPoseSeedProvider] Manual yaw calibration -> {manualYawCalibrationDegrees:F0}°");
        }
    }

    private void OnDestroy()
    {
        if (frameTexture2D != null)
            Destroy(frameTexture2D);

        frameRgbaMat?.Dispose();
        frameGrayMat?.Dispose();

        DestroyDebugMarkersIfAny();
    }

    private void UpdateOrHideDebugMarkers(bool show, Vector3 hitPoint, Vector3 seedPoint, Quaternion worldRotation)
    {
        if (!show)
        {
            SetMarkersActive(false);
            return;
        }

        EnsureDebugMarkers();

        hitPointMarker.transform.SetPositionAndRotation(hitPoint, Quaternion.identity);
        hitPointMarker.transform.localScale = Vector3.one * pointMarkerSizeMeters;

        seedPointMarker.transform.SetPositionAndRotation(seedPoint, worldRotation);
        seedPointMarker.transform.localScale = Vector3.one * pointMarkerSizeMeters;

        float l = axisMarkerLengthMeters;
        float t = axisMarkerThicknessMeters;
        Vector3 right   = worldRotation * Vector3.right;
        Vector3 up      = worldRotation * Vector3.up;
        Vector3 forward = worldRotation * Vector3.forward;

        // Each axis bar is a thin cube whose long dimension matches the corresponding object-local axis.
        // It is positioned so that its base coincides with the seed point (centre is at seed + dir * length / 2).
        localAxisXMarker.transform.SetPositionAndRotation(seedPoint + right   * (l * 0.5f), worldRotation);
        localAxisXMarker.transform.localScale = new Vector3(l, t, t);

        localAxisYMarker.transform.SetPositionAndRotation(seedPoint + up      * (l * 0.5f), worldRotation);
        localAxisYMarker.transform.localScale = new Vector3(t, l, t);

        localAxisZMarker.transform.SetPositionAndRotation(seedPoint + forward * (l * 0.5f), worldRotation);
        localAxisZMarker.transform.localScale = new Vector3(t, t, l);

        SetMarkersActive(true);
    }

    private void EnsureDebugMarkers()
    {
        if (hitPointMarker == null)
            hitPointMarker = CreateDebugPrimitive("YoloSeed_HitPoint", PrimitiveType.Sphere, Color.magenta);
        if (seedPointMarker == null)
            seedPointMarker = CreateDebugPrimitive("YoloSeed_SeedPoint", PrimitiveType.Cube, new Color(1f, 0.85f, 0.10f));
        if (localAxisXMarker == null)
            localAxisXMarker = CreateDebugPrimitive("YoloSeed_AxisX", PrimitiveType.Cube, Color.red);
        if (localAxisYMarker == null)
            localAxisYMarker = CreateDebugPrimitive("YoloSeed_AxisY", PrimitiveType.Cube, Color.green);
        if (localAxisZMarker == null)
            localAxisZMarker = CreateDebugPrimitive("YoloSeed_AxisZ", PrimitiveType.Cube, Color.blue);
    }

    private void SetMarkersActive(bool active)
    {
        if (hitPointMarker   != null) hitPointMarker.SetActive(active);
        if (seedPointMarker  != null) seedPointMarker.SetActive(active);
        if (localAxisXMarker != null) localAxisXMarker.SetActive(active);
        if (localAxisYMarker != null) localAxisYMarker.SetActive(active);
        if (localAxisZMarker != null) localAxisZMarker.SetActive(active);
    }

    private static GameObject CreateDebugPrimitive(string name, PrimitiveType type, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.hideFlags = HideFlags.DontSave;

        Collider col = go.GetComponent<Collider>();
        if (col != null)
            Destroy(col);

        Renderer renderer = go.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");

            if (shader != null)
            {
                Material mat = new Material(shader) { hideFlags = HideFlags.DontSave };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color"))     mat.SetColor("_Color", color);
                mat.color = color;
                renderer.material = mat;
            }
        }

        return go;
    }

    private void DestroyDebugMarkersIfAny()
    {
        DestroyMarkerIfExists(ref hitPointMarker);
        DestroyMarkerIfExists(ref seedPointMarker);
        DestroyMarkerIfExists(ref localAxisXMarker);
        DestroyMarkerIfExists(ref localAxisYMarker);
        DestroyMarkerIfExists(ref localAxisZMarker);
    }

    private static void DestroyMarkerIfExists(ref GameObject marker)
    {
        if (marker == null)
            return;

        if (Application.isPlaying)
            Destroy(marker);
        else
            DestroyImmediate(marker);

        marker = null;
    }
}
#endif
