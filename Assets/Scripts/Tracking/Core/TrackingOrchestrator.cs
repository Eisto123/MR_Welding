using System;
using UnityEngine;

public class TrackingOrchestrator : MonoBehaviour
{
    [Serializable]
    private struct TrackingObjectClassBinding
    {
        public int ClassId;
        public string Label;
        public string ObjFileName;
        public string MetaFileName;
        public string PoseFileName;
        public MeshFilter WireframeMeshSource;
    }

    [Header("Dependencies")]
    [SerializeField] private MonoBehaviour frameSourceBehaviour;
    [SerializeField] private MonoBehaviour nativeBridgeBehaviour;
    [SerializeField] private MonoBehaviour poseSeedProviderBehaviour;
    [SerializeField] private TrackingWireframeOverlay wireframeOverlay;
    [SerializeField] private TrackingSettings trackingSettings;

    [Header("Runtime Control")]
    [Tooltip("When true, camera input and native SRT3D initialise from Start(). Turn this off when another project wants to control detection with StartDetection/StopDetection.")]
    [SerializeField] private bool startDetectionOnStart = true;

    [Header("Tracking Success Criteria")]
    [SerializeField] private int requireConsecutiveValidFrames = 3;
    [SerializeField] private int missFramesBeforeLost = 8;

    [Header("YOLO Seeded Startup")]
    [SerializeField] private bool useYoloPoseSeeding = true;
    [Tooltip("YOLO is only evaluated while SRT3D is not actively tracking.")]
    [SerializeField] private int yoloScanEveryNFrames = 3;
    [SerializeField, Range(0f, 1f)] private float minimumSrt3dConfidence = 0.25f;
    [SerializeField] private int lowConfidenceFramesBeforeYoloReacquire = 8;
    [SerializeField] private bool logSeedEvents = true;

    [Header("Fast Seed Retry")]
    [Tooltip("When an unconfirmed YOLO seed fails the confidence gate, try the next automatic yaw candidate on the next frame instead of waiting for Yolo Scan Every N Frames.")]
    [SerializeField] private bool forceYoloScanNextFrameAfterSeedFailure = true;

    [Header("Manual Start Trigger")]
    [Tooltip("If true, YOLO scanning waits for the manual trigger button before searching for a seed pose. Useful when you want to control the moment of capture (head still, object visible) instead of letting YOLO scan continuously while the head is moving.")]
    [SerializeField] private bool requireManualStartTrigger = false;
    [Tooltip("OVR controller button that arms a YOLO scan when Require Manual Start Trigger is enabled.")]
    [SerializeField] private OVRInput.RawButton manualStartButton = OVRInput.RawButton.A;
    [Tooltip("If true, the trigger remains armed across probation/seed failures so YOLO keeps retrying until SRT3D locks. If false, every seed failure requires a fresh button press.")]
    [SerializeField] private bool keepTriggerArmedAcrossSeedFailures = true;

    [Header("Start Confidence Gate")]
    [Tooltip("After StartTrackingFromPose, evaluate SRT3D's confidence over the next probation frames. If the maximum confidence stays below the threshold, abandon this seed and re-scan with YOLO. The wireframe is not rendered while in probation.")]
    [SerializeField] private bool requireMinimumStartConfidence = false;
    [SerializeField, Range(0f, 1f)] private float startConfidenceThreshold = 0.7f;
    [SerializeField, Range(1, 60)] private int startConfidenceProbationFrames = 8;
    [Tooltip("When ON, accept an unconfirmed seed as soon as any probation frame reaches Start Confidence Threshold instead of always waiting for the full probation window.")]
    [SerializeField] private bool clearStartConfidenceGateAsSoonAsThresholdReached = true;

    [Header("YOLO Class to Tracking Object")]
    [SerializeField] private TrackingObjectClassBinding[] trackingObjectsByClass =
    {
        new TrackingObjectClassBinding
        {
            ClassId = 0,
            Label = "ButtShape",
            ObjFileName = "buttshape.obj",
            MetaFileName = "buttshape.srt3d",
            PoseFileName = "buttshape_pose.txt"
        },
        new TrackingObjectClassBinding
        {
            ClassId = 1,
            Label = "TShape",
            ObjFileName = "tshape.obj",
            MetaFileName = "tshape.srt3d",
            PoseFileName = "tshape_pose.txt"
        }
    };

    [Header("Debug Logging")]
    [SerializeField] private int logEveryNFrames = 30;
    [SerializeField] private bool verbosePoseMatrix = false;

    [Header("Profiling")]
    [SerializeField] private bool logStageTimings = false;
    [SerializeField] private int timingLogEveryNFrames = 60;

    private const float PoseSentinelBase = 1000f;
    private const int PostSeedDebugFrameCount = 3;
    private readonly float[] poseBuffer = new float[16];
    private readonly float[] acceptedSeedPose = new float[16];
    private ICameraFrameSource frameSource;
    private ITrackerNativeBridge nativeBridge;
    private ITrackingPoseSeedProvider poseSeedProvider;
    private ITrackingPoseSeedRetryStrategy poseSeedRetryStrategy;
    private int frameCounter;
    private int consecutiveValidFrames;
    private int consecutiveMissFrames;
    private int lowConfidenceFrames;
    private bool nativeTrackingStarted;
    private bool nativeStoppedForSearch;
    private int currentTrackedClassId = -1;
    private string currentTrackedLabel;
    private string lastSeedStatus;
    private bool manualTriggerArmed;
    private int probationFramesElapsed;
    private float probationMaxConfidence;
    private bool probationCleared;
    private bool hasAcceptedSeedPose;
    private bool currentSeedConfirmed;
    private bool forceYoloScanNextFrame;
    private int postSeedDebugFramesRemaining;
    private int postSeedDebugFrameIndex;
    private bool detectionEnabled;

    public TrackingState State { get; private set; } = TrackingState.NotInitialized;
    public TrackingResult LastResult { get; private set; }
    public bool IsDetectionEnabled => detectionEnabled;
    public event Action<TrackingResult> OnTrackingResultUpdated;
    public event Action<TrackingPoseSeed> OnSeedAccepted;
    public event Action<string> OnSeedRejected;
    public event Action<TrackingResult, string> OnSeedConfirmed;
    public float StartConfidenceThreshold => startConfidenceThreshold;

    private void Awake()
    {
        if (wireframeOverlay == null)
            wireframeOverlay = GetComponentInChildren<TrackingWireframeOverlay>();
        ResolveDependencies();
    }

    private void Start()
    {
        ApplyTrackingSettings();
        if (startDetectionOnStart)
            StartDetection();
    }

    /// <summary>
    /// Starts camera frame acquisition, initialises the native bridge, and allows YOLO/SRT3D to run.
    /// Use this from host-project UI or gameplay code when detection should begin.
    /// </summary>
    public bool StartDetection()
    {
        ResolveDependencies();
        ApplyTrackingSettings();

        if (frameSource == null || nativeBridge == null || (useYoloPoseSeeding && poseSeedProvider == null))
        {
            State = TrackingState.Error;
            detectionEnabled = false;
            return false;
        }

        if (detectionEnabled)
            return true;

        ResetTrackingSessionState();
        frameSource.StartSource();
        bool initOk = nativeBridge.InitializeBridge();
        detectionEnabled = initOk;
        State = initOk ? TrackingState.Ready : TrackingState.Error;
        if (!initOk)
            Debug.LogError($"[TrackingOrchestrator] Native bridge init failed: {nativeBridge.LastError}");
        return initOk;
    }

    /// <summary>
    /// Stops YOLO scanning and SRT3D tracking, releases native runtime state, and stops frame input.
    /// Call StartDetection again to reinitialise and resume detection.
    /// </summary>
    public void StopDetection()
    {
        nativeBridge?.StopTracking();
        nativeBridge?.ShutdownBridge();
        frameSource?.StopSource();
        detectionEnabled = false;
        ResetTrackingSessionState();
        LastResult = default;
        State = TrackingState.NotInitialized;
    }

    /// <summary>
    /// Arms one YOLO scan when Require Manual Start Trigger is enabled. Useful for host-project UI.
    /// If manual triggering is disabled, StartDetection is enough.
    /// </summary>
    public void ArmManualStartTrigger()
    {
        manualTriggerArmed = true;
    }

    private void Update()
    {
        if (!detectionEnabled || frameSource == null || nativeBridge == null || State == TrackingState.Error)
            return;

        var stopwatch = logStageTimings ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (!frameSource.TryGetFrame(out FramePacket framePacket))
            return;
        long frameSourceTicks = stopwatch != null ? stopwatch.ElapsedTicks : 0L;

        frameCounter++;
        TrackingResult result;

        if (!nativeBridge.IsInitialized)
        {
            result = BuildIdleResult(framePacket, TrackingState.Ready, "Native bridge is still initialising.");
            PublishResult(result, stopwatch, frameSourceTicks);
            return;
        }

        if (useYoloPoseSeeding && !nativeTrackingStarted)
        {
            EnsureNativeStoppedForSearch();

            if (requireManualStartTrigger)
            {
                if (OVRInput.GetDown(manualStartButton))
                {
                    manualTriggerArmed = true;
                    if (logSeedEvents)
                        Debug.Log($"[TrackingOrchestrator] Manual trigger armed via {manualStartButton}.");
                }

                if (!manualTriggerArmed)
                {
                    lastSeedStatus = $"Press {manualStartButton} to start a YOLO scan.";
                    result = BuildIdleResult(framePacket, TrackingState.Ready, lastSeedStatus);
                    PublishResult(result, stopwatch, frameSourceTicks);
                    return;
                }
            }

            if (!ShouldRunYoloScan() || !TryStartTrackingFromYoloSeed(framePacket))
            {
                result = BuildIdleResult(framePacket, TrackingState.Ready, lastSeedStatus);
                PublishResult(result, stopwatch, frameSourceTicks);
                return;
            }
        }

        result = BuildTrackingResult(framePacket);
        if (useYoloPoseSeeding)
            ApplyYoloSeededLossPolicy(ref result);

        PublishResult(result, stopwatch, frameSourceTicks);
    }

    private void PublishResult(TrackingResult result, System.Diagnostics.Stopwatch stopwatch, long frameSourceTicks)
    {
        long totalTicks = stopwatch?.ElapsedTicks ?? 0L;
        if (logStageTimings && stopwatch != null)
            result.FrameSourceMs = (float)TicksToMilliseconds(frameSourceTicks);
        LastResult = result;
        State = result.State;
        OnTrackingResultUpdated?.Invoke(result);

        if (logEveryNFrames > 0 && frameCounter % logEveryNFrames == 0)
        {
            string confText = result.HasConfidence ? result.Confidence.ToString("F3") : "N/A";
            Debug.Log(
                $"[TrackingOrchestrator] frame={frameCounter}, state={result.State}, processOk={result.ProcessOk}, " +
                $"poseValid={result.PoseValid}, changed={result.ChangedCount}/16, conf={confText}, t={result.TranslationRowMajor}, " +
                $"frameSize={result.FrameWidth}x{result.FrameHeight}");

            if (verbosePoseMatrix && result.RowMajorPose16 != null)
                Debug.Log("[TrackingOrchestrator] pose(4x4):\n" + FormatPose(result.RowMajorPose16));
        }

        if (logStageTimings &&
            stopwatch != null &&
            timingLogEveryNFrames > 0 &&
            frameCounter % timingLogEveryNFrames == 0)
        {
            double frameSourceMs = TicksToMilliseconds(frameSourceTicks);
            double totalMs = TicksToMilliseconds(totalTicks);
            Debug.Log($"[TrackingOrchestrator] timing frame={frameCounter}, " +
                      $"frameSource={frameSourceMs:F2}ms, native+pose={(totalMs - frameSourceMs):F2}ms, total={totalMs:F2}ms");
        }
    }

    private TrackingResult BuildTrackingResult(FramePacket framePacket)
    {
        TrackingResult result = CreateBaseResult(framePacket, State, null);

        long srtStartTicks = logStageTimings ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        bool processOk = nativeBridge.ProcessFrame(framePacket.Rgb24, framePacket.Width, framePacket.Height);
        result.ProcessOk = processOk;

        for (int i = 0; i < 16; i++)
            poseBuffer[i] = PoseSentinelBase + i;

        nativeBridge.GetTrackedPose(poseBuffer);
        Array.Copy(poseBuffer, result.RowMajorPose16, 16);

        for (int i = 0; i < 16; i++)
        {
            float expected = PoseSentinelBase + i;
            if (Mathf.Abs(poseBuffer[i] - expected) > 1e-6f)
            {
                result.ChangedCount++;
                if (result.FirstChangedIndex < 0)
                {
                    result.FirstChangedIndex = i;
                    result.FirstChangedValue = poseBuffer[i];
                }
            }
        }

        result.PoseWasWritten = result.ChangedCount > 0;
        result.PoseValid = processOk && result.PoseWasWritten;
        // Default confirmation: a valid pose is considered confirmed unless probation overrides it
        // in ApplyYoloSeededLossPolicy.
        result.IsConfirmed = result.PoseValid;
        result.IsInProbation = false;
        result.TranslationRowMajor = new Vector3(poseBuffer[3], poseBuffer[7], poseBuffer[11]);

        if (nativeBridge.SupportsConfidence)
        {
            float conf = nativeBridge.GetTrackingConfidence();
            if (conf >= 0f)
            {
                result.HasConfidence = true;
                result.Confidence = conf;
            }
        }
        if (logStageTimings)
        {
            long srtEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            result.Srt3dUpdateMs = (float)TicksToMilliseconds(srtEndTicks - srtStartTicks);
        }

        LogPostSeedPoseDeltaIfNeeded(result);

        UpdateState(ref result);
        return result;
    }

    private TrackingResult BuildIdleResult(FramePacket framePacket, TrackingState state, string message)
    {
        consecutiveValidFrames = 0;
        return CreateBaseResult(framePacket, state, message);
    }

    private TrackingResult CreateBaseResult(FramePacket framePacket, TrackingState state, string message)
    {
        return new TrackingResult
        {
            State = state,
            ProcessOk = false,
            PoseWasWritten = false,
            PoseValid = false,
            IsConfirmed = false,
            HasConfidence = false,
            Confidence = -1f,
            ChangedCount = 0,
            FirstChangedIndex = -1,
            FirstChangedValue = 0f,
            RowMajorPose16 = new float[16],
            TranslationRowMajor = Vector3.zero,
            TrackedClassId = currentTrackedClassId,
            TrackedLabel = currentTrackedLabel,
            TimestampTicksUtc = framePacket.TimestampTicksUtc,
            FrameWidth = framePacket.Width,
            FrameHeight = framePacket.Height,
            HasPoseReference = framePacket.HasPoseReference,
            PoseReferencePosition = framePacket.PoseReferencePosition,
            PoseReferenceRotation = framePacket.PoseReferenceRotation,
            ErrorMessage = message
        };
    }

    private bool ShouldRunYoloScan()
    {
        if (forceYoloScanNextFrame)
        {
            forceYoloScanNextFrame = false;
            return true;
        }

        return yoloScanEveryNFrames <= 1 || frameCounter % yoloScanEveryNFrames == 0;
    }

    private void EnsureNativeStoppedForSearch()
    {
        if (nativeStoppedForSearch)
            return;

        nativeBridge.StopTracking();
        nativeStoppedForSearch = true;
    }

    private bool TryStartTrackingFromYoloSeed(FramePacket framePacket)
    {
        if (poseSeedProvider == null)
        {
            lastSeedStatus = "Pose seed provider is missing.";
            return false;
        }

        if (!poseSeedProvider.IsReady)
        {
            lastSeedStatus = "Pose seed provider is not ready.";
            return false;
        }

        if (!poseSeedProvider.TryGetSeedPose(framePacket, out TrackingPoseSeed seed, out string debugInfo))
        {
            lastSeedStatus = debugInfo;
            return false;
        }

        if (!EnsureTrackingObjectForSeed(seed))
            return false;

        bool started = nativeBridge.StartTrackingFromPose(seed.RowMajorPose16);
        if (!started)
        {
            lastSeedStatus = $"StartTrackingFromPose failed: {nativeBridge.LastError}";
            if (logSeedEvents)
                Debug.LogWarning("[TrackingOrchestrator] " + lastSeedStatus);
            return false;
        }

        nativeTrackingStarted = true;
        nativeStoppedForSearch = false;
        forceYoloScanNextFrame = false;
        Array.Copy(seed.RowMajorPose16, acceptedSeedPose, 16);
        hasAcceptedSeedPose = true;
        postSeedDebugFramesRemaining = PostSeedDebugFrameCount;
        postSeedDebugFrameIndex = 0;
        consecutiveValidFrames = 0;
        consecutiveMissFrames = 0;
        lowConfidenceFrames = 0;
        probationFramesElapsed = 0;
        probationMaxConfidence = 0f;
        // No probation if the gate is disabled, so consider it cleared immediately.
        probationCleared = !requireMinimumStartConfidence;
        currentSeedConfirmed = false;
        lastSeedStatus = debugInfo;
        OnSeedAccepted?.Invoke(seed);

        if (logSeedEvents)
            Debug.Log("[TrackingOrchestrator] YOLO seed accepted: " + debugInfo);

        return true;
    }

    private void LogPostSeedPoseDeltaIfNeeded(TrackingResult result)
    {
        if (!hasAcceptedSeedPose ||
            postSeedDebugFramesRemaining <= 0 ||
            !result.PoseWasWritten ||
            result.RowMajorPose16 == null ||
            result.RowMajorPose16.Length < 16)
        {
            return;
        }

        postSeedDebugFrameIndex++;
        postSeedDebugFramesRemaining--;

        float tErr = TranslationDistance(acceptedSeedPose, result.RowMajorPose16);
        float rErr = RotationAngleDegrees(acceptedSeedPose, result.RowMajorPose16);
        string confText = result.HasConfidence ? result.Confidence.ToString("F3") : "N/A";
        Debug.Log(
            $"[TrackingOrchestrator] postSeedFrame={postSeedDebugFrameIndex}, processOk={result.ProcessOk}, " +
            $"deltaFromSeed=({tErr:F5}m,{rErr:F2}deg), conf={confText}, " +
            $"seedT={FormatCvTranslation(acceptedSeedPose)}, trackedT={FormatCvTranslation(result.RowMajorPose16)}");
    }

    private static float TranslationDistance(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 12 || b.Length < 12)
            return float.PositiveInfinity;

        float dx = a[3] - b[3];
        float dy = a[7] - b[7];
        float dz = a[11] - b[11];
        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float RotationAngleDegrees(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 11 || b.Length < 11)
            return float.PositiveInfinity;

        float trace =
            b[0] * a[0] + b[1] * a[1] + b[2] * a[2] +
            b[4] * a[4] + b[5] * a[5] + b[6] * a[6] +
            b[8] * a[8] + b[9] * a[9] + b[10] * a[10];
        float cosTheta = Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f);
        return Mathf.Acos(cosTheta) * Mathf.Rad2Deg;
    }

    private static string FormatCvTranslation(float[] m)
    {
        if (m == null || m.Length < 12)
            return "(invalid)";

        return $"({m[3]:F4},{m[7]:F4},{m[11]:F4})";
    }

    private bool EnsureTrackingObjectForSeed(TrackingPoseSeed seed)
    {
        if (!TryGetObjectBinding(seed.ClassId, out TrackingObjectClassBinding binding))
        {
            lastSeedStatus = $"No SRT3D object binding configured for YOLO class {seed.ClassId} ({seed.Label}).";
            if (logSeedEvents)
                Debug.LogWarning("[TrackingOrchestrator] " + lastSeedStatus);
            return false;
        }

        string label = string.IsNullOrWhiteSpace(binding.Label) ? seed.Label : binding.Label;
        bool sameClass = currentTrackedClassId == binding.ClassId;
        if (!sameClass)
        {
            bool switched = nativeBridge.SwitchObjectByFileName(
                binding.ObjFileName,
                binding.MetaFileName,
                binding.PoseFileName);
            if (!switched)
            {
                lastSeedStatus =
                    $"SwitchObjectByFileName failed for class {binding.ClassId} ({label}): {nativeBridge.LastError}";
                if (logSeedEvents)
                    Debug.LogWarning("[TrackingOrchestrator] " + lastSeedStatus);
                return false;
            }

            currentTrackedClassId = binding.ClassId;
            currentTrackedLabel = label;
            if (binding.WireframeMeshSource != null)
                wireframeOverlay?.SetMeshSource(binding.WireframeMeshSource);
            else if (wireframeOverlay != null && logSeedEvents)
                Debug.LogWarning($"[TrackingOrchestrator] No wireframe mesh assigned for class {binding.ClassId} ({label}).");

            if (logSeedEvents)
            {
                Debug.Log(
                    $"[TrackingOrchestrator] Switched SRT3D object to class {binding.ClassId} ({label}): " +
                    $"{binding.ObjFileName}, {binding.MetaFileName}, {binding.PoseFileName}");
            }
        }
        else if (string.IsNullOrEmpty(currentTrackedLabel))
        {
            currentTrackedLabel = label;
        }

        return true;
    }

    private bool TryGetObjectBinding(int classId, out TrackingObjectClassBinding binding)
    {
        if (trackingObjectsByClass != null)
        {
            for (int i = 0; i < trackingObjectsByClass.Length; i++)
            {
                if (trackingObjectsByClass[i].ClassId == classId)
                {
                    binding = trackingObjectsByClass[i];
                    return true;
                }
            }
        }

        binding = default;
        return false;
    }

    private void ApplyYoloSeededLossPolicy(ref TrackingResult result)
    {
        if (!nativeTrackingStarted)
            return;

        // 1) Probation phase: SRT3D has just been seeded; do not render the wireframe until the
        //    probation window passes the configured confidence threshold.
        if (requireMinimumStartConfidence && !probationCleared)
        {
            probationFramesElapsed++;
            if (result.HasConfidence && result.Confidence > probationMaxConfidence)
                probationMaxConfidence = result.Confidence;

            // Confirmed wireframe must NOT render during probation — the seed could still be
            // wildly off. We expose IsInProbation separately so debug overlays can opt in to
            // visualising the in-progress pose (e.g. in red) while waiting for the gate to clear.
            result.IsConfirmed = false;
            result.IsInProbation = true;

            if (clearStartConfidenceGateAsSoonAsThresholdReached &&
                result.PoseValid &&
                result.HasConfidence &&
                result.Confidence >= startConfidenceThreshold)
            {
                ClearStartConfidenceGate(ref result,
                    $"conf {result.Confidence:F3} reached threshold {startConfidenceThreshold:F3} early");
                return;
            }

            if (probationFramesElapsed < startConfidenceProbationFrames)
                return;

            if (probationMaxConfidence < startConfidenceThreshold)
            {
                RejectCurrentSeedAndAbandon(ref result,
                    $"probation max conf {probationMaxConfidence:F3} < threshold {startConfidenceThreshold:F3}");
                return;
            }

            ClearStartConfidenceGate(ref result,
                $"max conf {probationMaxConfidence:F3} >= threshold {startConfidenceThreshold:F3}");
        }

        // 2) Normal post-seed loss policy.
        bool lowConfidence = result.HasConfidence && result.Confidence < minimumSrt3dConfidence;
        if (result.PoseValid && !lowConfidence)
        {
            MarkCurrentSeedConfirmed();
            lowConfidenceFrames = 0;
            return;
        }

        lowConfidenceFrames++;
        bool reacquire =
            result.State == TrackingState.Lost ||
            lowConfidenceFrames >= Mathf.Max(1, lowConfidenceFramesBeforeYoloReacquire);

        if (!reacquire)
            return;

        string reason = lowConfidence
            ? $"confidence {result.Confidence:F3} below {minimumSrt3dConfidence:F3}"
            : "SRT3D pose was not valid";

        RejectCurrentSeedAndAbandon(ref result, reason);

        // Lost during steady-state tracking: require an explicit re-trigger if the manual gate is on.
        if (requireManualStartTrigger)
            manualTriggerArmed = false;
    }

    private void ClearStartConfidenceGate(ref TrackingResult result, string reason)
    {
        probationCleared = true;
        result.IsConfirmed = true;
        result.IsInProbation = false;
        lowConfidenceFrames = 0;
        MarkCurrentSeedConfirmed();

        // Successful lock: consume the manual trigger so a follow-up scan requires a fresh press.
        manualTriggerArmed = false;
        if (logSeedEvents)
            Debug.Log($"[TrackingOrchestrator] Probation cleared: {reason}.");
        OnSeedConfirmed?.Invoke(result, reason);
    }

    private void MarkCurrentSeedConfirmed()
    {
        if (currentSeedConfirmed)
            return;

        currentSeedConfirmed = true;
        poseSeedRetryStrategy?.NotifySeedConfirmed();
    }

    private void RejectCurrentSeedAndAbandon(ref TrackingResult result, string reason)
    {
        bool rejectedBeforeConfirmation = !currentSeedConfirmed;
        if (rejectedBeforeConfirmation)
            poseSeedRetryStrategy?.NotifySeedRejected(reason);
        else
            poseSeedRetryStrategy?.ResetSeedRetryStrategy();

        OnSeedRejected?.Invoke(reason);
        AbandonCurrentSeed(ref result, reason);

        if (rejectedBeforeConfirmation && forceYoloScanNextFrameAfterSeedFailure)
            forceYoloScanNextFrame = true;
    }

    private void AbandonCurrentSeed(ref TrackingResult result, string reason)
    {
        nativeBridge.StopTracking();
        nativeTrackingStarted = false;
        nativeStoppedForSearch = true;
        lowConfidenceFrames = 0;
        consecutiveValidFrames = 0;
        consecutiveMissFrames = 0;
        probationFramesElapsed = 0;
        probationMaxConfidence = 0f;
        probationCleared = false;
        hasAcceptedSeedPose = false;
        currentSeedConfirmed = false;
        forceYoloScanNextFrame = false;
        postSeedDebugFramesRemaining = 0;
        postSeedDebugFrameIndex = 0;

        // Manual trigger handling: the trigger remains armed across seed failures iff the user
        // opted in. The caller decides whether normal lost-during-tracking should disarm it.
        if (requireManualStartTrigger && !keepTriggerArmedAcrossSeedFailures)
            manualTriggerArmed = false;

        result.State = TrackingState.Lost;
        result.IsConfirmed = false;
        result.IsInProbation = false;
        result.ErrorMessage = "Returning to YOLO search: " + reason;

        if (logSeedEvents)
            Debug.Log("[TrackingOrchestrator] " + result.ErrorMessage);
    }

    private void UpdateState(ref TrackingResult result)
    {
        if (result.PoseValid)
        {
            consecutiveValidFrames++;
            consecutiveMissFrames = 0;
            result.State = consecutiveValidFrames >= Mathf.Max(1, requireConsecutiveValidFrames)
                ? TrackingState.Tracking
                : TrackingState.Ready;
            return;
        }

        consecutiveValidFrames = 0;
        consecutiveMissFrames++;
        result.State = consecutiveMissFrames >= Mathf.Max(1, missFramesBeforeLost)
            ? TrackingState.Lost
            : TrackingState.Ready;
    }

    private static string FormatPose(float[] m)
    {
        if (m == null || m.Length < 16)
            return "invalid pose";
        return
            $"{m[0],10:F5} {m[1],10:F5} {m[2],10:F5} {m[3],10:F5}\n" +
            $"{m[4],10:F5} {m[5],10:F5} {m[6],10:F5} {m[7],10:F5}\n" +
            $"{m[8],10:F5} {m[9],10:F5} {m[10],10:F5} {m[11],10:F5}\n" +
            $"{m[12],10:F5} {m[13],10:F5} {m[14],10:F5} {m[15],10:F5}";
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void ApplyTrackingSettings()
    {
        if (trackingSettings == null)
            return;

        requireConsecutiveValidFrames = trackingSettings.RequireConsecutiveValidFrames;
        missFramesBeforeLost = trackingSettings.MissFramesBeforeLost;
    }

    private void ResetTrackingSessionState()
    {
        frameCounter = 0;
        consecutiveValidFrames = 0;
        consecutiveMissFrames = 0;
        lowConfidenceFrames = 0;
        nativeTrackingStarted = false;
        nativeStoppedForSearch = false;
        currentTrackedClassId = -1;
        currentTrackedLabel = null;
        lastSeedStatus = null;
        manualTriggerArmed = false;
        probationFramesElapsed = 0;
        probationMaxConfidence = 0f;
        probationCleared = false;
        hasAcceptedSeedPose = false;
        currentSeedConfirmed = false;
        forceYoloScanNextFrame = false;
        postSeedDebugFramesRemaining = 0;
        postSeedDebugFrameIndex = 0;
        poseSeedRetryStrategy?.ResetSeedRetryStrategy();
    }

    private void OnDisable()
    {
        StopDetection();
    }

    public void ConfigureDependencies(MonoBehaviour sourceBehaviour, MonoBehaviour bridgeBehaviour, MonoBehaviour seedProviderBehaviour = null)
    {
        frameSourceBehaviour = sourceBehaviour;
        nativeBridgeBehaviour = bridgeBehaviour;
        if (seedProviderBehaviour != null)
            poseSeedProviderBehaviour = seedProviderBehaviour;
        ResolveDependencies();
    }

    private void ResolveDependencies()
    {
        frameSource = frameSourceBehaviour as ICameraFrameSource;
        nativeBridge = nativeBridgeBehaviour as ITrackerNativeBridge;
        poseSeedProvider = poseSeedProviderBehaviour as ITrackingPoseSeedProvider;
        poseSeedRetryStrategy = poseSeedProviderBehaviour as ITrackingPoseSeedRetryStrategy;
        if (frameSource == null)
            Debug.LogError("[TrackingOrchestrator] frameSourceBehaviour must implement ICameraFrameSource.");
        if (nativeBridge == null)
            Debug.LogError("[TrackingOrchestrator] nativeBridgeBehaviour must implement ITrackerNativeBridge.");
        if (useYoloPoseSeeding && poseSeedProvider == null)
            Debug.LogError("[TrackingOrchestrator] poseSeedProviderBehaviour must implement ITrackingPoseSeedProvider.");
    }
}
