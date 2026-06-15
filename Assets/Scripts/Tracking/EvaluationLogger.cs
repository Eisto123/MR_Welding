using System;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class EvaluationLogger : MonoBehaviour
{
    public static bool IsAccuracyTestModeActive { get; private set; }

    public enum InitializationTestCondition
    {
        Distance30cm_Horizontal,
        Distance30cm_Clockwise45,
        Distance30cm_Vertical
    }

    [Header("Dependencies")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;
    [SerializeField] private TrackingWireframeOverlay wireframeOverlay;

    [Header("Trial Setup")]
    [SerializeField] private bool writeCsvLogs = true;
    [SerializeField] private bool autoStartOneTrialPerDetectionSession = true;
    [SerializeField] private InitializationTestCondition testCondition = InitializationTestCondition.Distance30cm_Horizontal;
    [SerializeField] private string mode = "ContinuousTracking";
    [SerializeField, Range(1f, 60f)] private float initializationTimeoutSeconds = 15f;

    [Header("Experiment Switch")]
    [Tooltip("OFF restores the normal welding flow. ON holds AutoPlacement after a stable pose and enables condition/restart controls for initialization accuracy trials.")]
    [SerializeField] private bool enableAccuracyTestMode = false;
    [SerializeField] private OVRInput.RawButton restartTrialButton = OVRInput.RawButton.A;
    [SerializeField, Range(0f, 10f)] private float restartAfterCompletedTrialDelaySeconds = 3f;

    [Header("Condition UI")]
    [SerializeField] private bool allowControllerConditionCycling = true;
    [SerializeField] private OVRInput.RawButton cycleConditionButton = OVRInput.RawButton.X;
    [SerializeField] private Text conditionText;
    [SerializeField] private TMP_Text conditionTmpText;
    [SerializeField] private string conditionTextPrefix = "Condition: ";
    [SerializeField] private bool logConditionChanges = true;

    [Header("Output")]
    [SerializeField] private string rootFolderName = "EvaluationLogs";
    [Tooltip("When OFF, all app runs append to Application.persistentDataPath/EvaluationLogs/TrialSummaryLog.csv. Turn ON only when you want a separate timestamped folder per run.")]
    [SerializeField] private bool useTimestampedSessionFolder = false;
    [SerializeField] private string sessionFolderOverride;
    [SerializeField] private bool logFolderPathOnStart = true;

    private const string TrialSummaryFileName = "TrialSummaryLog.csv";

    private string sessionFolderPath;
    private string trialSummaryPath;
    private bool subscribed;
    private bool lastDetectionEnabled;
    private bool autoTrialUsedThisDetectionSession;
    private bool hasDisplayedCondition;
    private InitializationTestCondition lastDisplayedCondition;
    private bool waitingForRestart;
    private float restartReadyTime;
    private bool countersSeededFromLog;
    private string lastDisplayedStatus;
    private bool conditionTextClearedWhileDisabled;
    private int buttCounter;
    private int tCounter;
    private int unknownCounter;
    private Trial currentTrial;

    private static readonly string[] TrialSummaryHeader =
    {
        "trial_id",
        "object_type",
        "test_condition",
        "mode",
        "start_time",
        "end_time",
        "trial_duration",
        "first_yolo_detection_time",
        "time_to_first_yolo_detection",
        "first_yolo_confidence",
        "first_yolo_bbox_x",
        "first_yolo_bbox_y",
        "first_yolo_bbox_w",
        "first_yolo_bbox_h",
        "depth_query_success",
        "initial_depth_value",
        "initial_position_x",
        "initial_position_y",
        "initial_position_z",
        "initial_rotation_x",
        "initial_rotation_y",
        "initial_rotation_z",
        "srt3d_init_start_time",
        "srt3d_first_valid_pose_time",
        "srt3d_confidence_threshold",
        "srt3d_convergence_time",
        "max_srt3d_confidence",
        "avg_srt3d_confidence_after_convergence",
        "num_initialization_attempts",
        "initialization_success",
        "failure_reason",
        "pose_lock_enabled",
        "pose_lock_time",
        "time_to_pose_lock",
        "final_position_x",
        "final_position_y",
        "final_position_z",
        "final_rotation_x",
        "final_rotation_y",
        "final_rotation_z",
        "final_rotation_w",
        "time_to_stable_pose"
    };

    private void Awake()
    {
        ResolveDependencies();
    }

    private void OnEnable()
    {
        UpdateGlobalAccuracyModeFlag();
        if (IsAccuracyTestModeActive)
            Subscribe();
    }

    private void Start()
    {
        ResolveDependencies();
        UpdateGlobalAccuracyModeFlag();
        if (IsAccuracyTestModeActive)
            Subscribe();
    }

    private void Update()
    {
        UpdateGlobalAccuracyModeFlag();
        if (!IsAccuracyTestModeActive)
        {
            ResetAccuracyTestRuntimeState();
            Unsubscribe();
            ClearConditionTextIfNeeded();
            return;
        }

        Subscribe();
        HandleConditionInput();
        HandleRestartInput();
        UpdateConditionTextIfNeeded();

        if (!writeCsvLogs)
            return;

        if (trackingOrchestrator == null)
        {
            ResolveDependencies();
            Subscribe();
            return;
        }

        bool detectionEnabled = trackingOrchestrator.IsDetectionEnabled;
        if (!detectionEnabled && lastDetectionEnabled)
            autoTrialUsedThisDetectionSession = false;
        lastDetectionEnabled = detectionEnabled;

        if (autoStartOneTrialPerDetectionSession &&
            detectionEnabled &&
            currentTrial == null &&
            !autoTrialUsedThisDetectionSession)
        {
            StartTrial();
            autoTrialUsedThisDetectionSession = true;
        }

        if (currentTrial != null &&
            Time.realtimeSinceStartup - currentTrial.StartTime >= initializationTimeoutSeconds)
        {
            string reason = currentTrial.HasFirstYoloDetection
                ? NormalizeFailureReason(currentTrial.LastFailureReason)
                : "NoYOLODetection";
            EndTrial(false, reason, trackingOrchestrator.LastResult);
        }
    }

    private void OnDisable()
    {
        Unsubscribe();
        ResetAccuracyTestRuntimeState();
        if (IsAccuracyTestModeActive && enableAccuracyTestMode)
            IsAccuracyTestModeActive = false;
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause && currentTrial != null)
            EndTrial(false, "ManualStop", trackingOrchestrator != null ? trackingOrchestrator.LastResult : default);
    }

    private void OnApplicationQuit()
    {
        if (currentTrial != null)
            EndTrial(false, "ManualStop", trackingOrchestrator != null ? trackingOrchestrator.LastResult : default);
    }

    public void SetConditionHorizontal30cm()
    {
        testCondition = InitializationTestCondition.Distance30cm_Horizontal;
        UpdateConditionText(true);
    }

    public void SetConditionClockwise45At30cm()
    {
        testCondition = InitializationTestCondition.Distance30cm_Clockwise45;
        UpdateConditionText(true);
    }

    public void SetConditionVerticalAt30cm()
    {
        testCondition = InitializationTestCondition.Distance30cm_Vertical;
        UpdateConditionText(true);
    }

    public void CycleTestCondition()
    {
        int count = Enum.GetValues(typeof(InitializationTestCondition)).Length;
        int next = ((int)testCondition + 1) % Mathf.Max(1, count);
        testCondition = (InitializationTestCondition)next;
        UpdateConditionText(true);

        if (logConditionChanges)
            Debug.Log("[EvaluationLogger] Test condition -> " + GetConditionName(testCondition));
    }

    public void StartTrial()
    {
        if (!IsAccuracyTestModeActive || !writeCsvLogs)
            return;

        waitingForRestart = false;

        if (currentTrial != null)
            EndTrial(false, "ManualStop", trackingOrchestrator != null ? trackingOrchestrator.LastResult : default);

        currentTrial = new Trial
        {
            StartTime = Time.realtimeSinceStartup,
            TestCondition = GetConditionName(testCondition),
            Mode = string.IsNullOrWhiteSpace(mode) ? "ContinuousTracking" : mode,
            ConfidenceThreshold = trackingOrchestrator != null ? trackingOrchestrator.StartConfidenceThreshold : -1f,
            FailureReason = "None"
        };
    }

    public void StopTrialAsManualFailure()
    {
        if (currentTrial != null)
            EndTrial(false, "ManualStop", trackingOrchestrator != null ? trackingOrchestrator.LastResult : default);
    }

    private void HandleSeedAccepted(TrackingPoseSeed seed)
    {
        if (!IsAccuracyTestModeActive || !writeCsvLogs)
            return;

        if (currentTrial == null)
            StartTrial();

        currentTrial.NumInitializationAttempts++;
        currentTrial.LastFailureReason = null;

        if (currentTrial.TrialId == null)
        {
            currentTrial.ObjectType = NormalizeObjectType(seed.ClassId, seed.Label);
            currentTrial.TrialId = AllocateTrialId(currentTrial.ObjectType);
        }

        if (!currentTrial.HasFirstYoloDetection)
        {
            float now = Time.realtimeSinceStartup;
            currentTrial.HasFirstYoloDetection = true;
            currentTrial.FirstYoloDetectionTime = now;
            currentTrial.FirstYoloConfidence = seed.YoloConfidence;
            currentTrial.FirstYoloBox = new Rect(
                seed.YoloBoxX,
                seed.YoloBoxY,
                seed.YoloBoxWidth,
                seed.YoloBoxHeight);
            currentTrial.DepthQuerySuccess = seed.DepthQuerySuccess;
            currentTrial.InitialDepthValue = seed.InitialDepthMeters;
            currentTrial.InitialPosition = seed.InitialWorldPosition;
            currentTrial.InitialEulerRotation = seed.InitialWorldRotation.eulerAngles;
            currentTrial.Srt3dInitStartTime = now;
        }
    }

    private void HandleSeedRejected(string reason)
    {
        if (currentTrial == null)
            return;

        currentTrial.LastFailureReason = reason;
        currentTrial.FailureReason = NormalizeFailureReason(reason);
    }

    private void HandleSeedConfirmed(TrackingResult result, string reason)
    {
        if (currentTrial == null)
            return;

        if (result.HasConfidence)
            currentTrial.MaxSrt3dConfidence = Mathf.Max(currentTrial.MaxSrt3dConfidence, result.Confidence);
    }

    private void HandleTrackingResult(TrackingResult result)
    {
        if (currentTrial == null)
            return;

        float now = Time.realtimeSinceStartup;

        if (result.HasConfidence)
            currentTrial.MaxSrt3dConfidence = Mathf.Max(currentTrial.MaxSrt3dConfidence, result.Confidence);

        if (result.PoseValid && !currentTrial.HasFirstValidSrt3dPose)
        {
            currentTrial.HasFirstValidSrt3dPose = true;
            currentTrial.Srt3dFirstValidPoseTime = now;
        }

        bool stablePose =
            result.PoseValid &&
            result.IsConfirmed &&
            result.State == TrackingState.Tracking;

        if (stablePose)
            EndTrial(true, "None", result);
    }

    private void EndTrial(bool success, string failureReason, TrackingResult result)
    {
        if (currentTrial == null)
            return;

        float now = Time.realtimeSinceStartup;
        currentTrial.EndTime = now;
        currentTrial.Success = success;
        currentTrial.FailureReason = success ? "None" : NormalizeFailureReason(failureReason);

        if (currentTrial.TrialId == null)
        {
            currentTrial.ObjectType = "Unknown";
            currentTrial.TrialId = AllocateTrialId(currentTrial.ObjectType);
        }

        if (success)
        {
            currentTrial.StablePoseTime = now;
            currentTrial.Srt3dConvergenceTime = currentTrial.Srt3dInitStartTime >= 0f
                ? now - currentTrial.Srt3dInitStartTime
                : -1f;

            if (result.HasConfidence)
                currentTrial.AvgConfidenceAfterConvergence = result.Confidence;

            if (TryGetFinalWorldPose(result, out Vector3 finalPosition, out Quaternion finalRotation))
            {
                currentTrial.FinalPosition = finalPosition;
                currentTrial.FinalRotation = finalRotation;
                currentTrial.HasFinalPose = true;
            }
        }

        WriteTrialSummaryRow(currentTrial);
        Debug.Log(
            $"[EvaluationLogger] Trial {currentTrial.TrialId} ended: success={success}, " +
            $"condition={currentTrial.TestCondition}, file={trialSummaryPath}");

        currentTrial = null;
        if (enableAccuracyTestMode)
        {
            waitingForRestart = true;
            restartReadyTime = now + (success ? restartAfterCompletedTrialDelaySeconds : 0f);
            UpdateConditionText(true);
        }
    }

    private bool TryGetFinalWorldPose(TrackingResult result, out Vector3 position, out Quaternion rotation)
    {
        if (wireframeOverlay != null &&
            wireframeOverlay.TryBuildWireframeWorldPose(result, out position, out rotation))
        {
            return true;
        }

        position = result.TranslationRowMajor;
        rotation = Quaternion.identity;
        return result.PoseValid;
    }

    private void WriteTrialSummaryRow(Trial trial)
    {
        EnsureLogFile();

        string[] row =
        {
            trial.TrialId,
            trial.ObjectType,
            trial.TestCondition,
            trial.Mode,
            FormatFloat(trial.StartTime),
            FormatFloat(trial.EndTime),
            FormatFloat(trial.EndTime - trial.StartTime),
            FormatOptionalTime(trial.HasFirstYoloDetection, trial.FirstYoloDetectionTime),
            FormatOptionalDuration(trial.HasFirstYoloDetection, trial.FirstYoloDetectionTime - trial.StartTime),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.FirstYoloConfidence),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.FirstYoloBox.x),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.FirstYoloBox.y),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.FirstYoloBox.width),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.FirstYoloBox.height),
            trial.HasFirstYoloDetection ? FormatBool(trial.DepthQuerySuccess) : "",
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialDepthValue),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialPosition.x),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialPosition.y),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialPosition.z),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialEulerRotation.x),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialEulerRotation.y),
            FormatOptionalFloat(trial.HasFirstYoloDetection, trial.InitialEulerRotation.z),
            FormatOptionalTime(trial.Srt3dInitStartTime >= 0f, trial.Srt3dInitStartTime),
            FormatOptionalTime(trial.HasFirstValidSrt3dPose, trial.Srt3dFirstValidPoseTime),
            FormatOptionalFloat(trial.ConfidenceThreshold >= 0f, trial.ConfidenceThreshold),
            FormatOptionalFloat(trial.Srt3dConvergenceTime >= 0f, trial.Srt3dConvergenceTime),
            FormatOptionalFloat(trial.MaxSrt3dConfidence >= 0f, trial.MaxSrt3dConfidence),
            FormatOptionalFloat(trial.AvgConfidenceAfterConvergence >= 0f, trial.AvgConfidenceAfterConvergence),
            trial.NumInitializationAttempts.ToString(CultureInfo.InvariantCulture),
            FormatBool(trial.Success),
            trial.FailureReason,
            "false",
            "",
            "",
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalPosition.x),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalPosition.y),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalPosition.z),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalRotation.x),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalRotation.y),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalRotation.z),
            FormatOptionalFloat(trial.HasFinalPose, trial.FinalRotation.w),
            FormatOptionalFloat(trial.StablePoseTime >= 0f, trial.StablePoseTime - trial.StartTime)
        };

        File.AppendAllText(trialSummaryPath, ToCsvLine(row) + Environment.NewLine, Encoding.UTF8);
    }

    private void EnsureLogFile()
    {
        if (!string.IsNullOrEmpty(trialSummaryPath))
            return;

        if (useTimestampedSessionFolder || !string.IsNullOrWhiteSpace(sessionFolderOverride))
        {
            string sessionName = string.IsNullOrWhiteSpace(sessionFolderOverride)
                ? DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)
                : sessionFolderOverride.Trim();
            sessionFolderPath = Path.Combine(Application.persistentDataPath, rootFolderName, sessionName);
        }
        else
        {
            sessionFolderPath = Path.Combine(Application.persistentDataPath, rootFolderName);
        }

        Directory.CreateDirectory(sessionFolderPath);

        trialSummaryPath = Path.Combine(sessionFolderPath, TrialSummaryFileName);
        if (!File.Exists(trialSummaryPath) || new FileInfo(trialSummaryPath).Length == 0)
        {
            File.WriteAllText(trialSummaryPath, ToCsvLine(TrialSummaryHeader) + Environment.NewLine, Encoding.UTF8);
        }
        else
        {
            SeedTrialCountersFromExistingLog(trialSummaryPath);
        }

        if (logFolderPathOnStart)
            Debug.Log("[EvaluationLogger] Writing evaluation logs to: " + sessionFolderPath);
    }

    private void SeedTrialCountersFromExistingLog(string path)
    {
        if (countersSeededFromLog)
            return;

        countersSeededFromLog = true;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string trialId = ReadFirstCsvCell(line);
                if (trialId == "trial_id")
                    continue;

                UpdateCounterFromTrialId(trialId, "butt_", ref buttCounter);
                UpdateCounterFromTrialId(trialId, "t_", ref tCounter);
                UpdateCounterFromTrialId(trialId, "unknown_", ref unknownCounter);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[EvaluationLogger] Failed to seed trial counters from existing CSV: " + e.Message);
        }
    }

    private static void UpdateCounterFromTrialId(string trialId, string prefix, ref int counter)
    {
        if (string.IsNullOrEmpty(trialId) ||
            !trialId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string suffix = trialId.Substring(prefix.Length);
        if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            counter = Mathf.Max(counter, value);
    }

    private static string ReadFirstCsvCell(string line)
    {
        if (string.IsNullOrEmpty(line))
            return "";

        if (line[0] != '"')
        {
            int comma = line.IndexOf(',');
            return comma >= 0 ? line.Substring(0, comma) : line;
        }

        var sb = new StringBuilder();
        for (int i = 1; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private void HandleConditionInput()
    {
        if (!allowControllerConditionCycling)
            return;

        if (OVRInput.GetDown(cycleConditionButton))
            CycleTestCondition();
    }

    private void HandleRestartInput()
    {
        if (!IsAccuracyTestModeActive || !waitingForRestart)
            return;

        if (Time.realtimeSinceStartup < restartReadyTime)
            return;

        if (OVRInput.GetDown(restartTrialButton))
            RestartTrackingForNextTrial();
    }

    private void RestartTrackingForNextTrial()
    {
        if (trackingOrchestrator == null)
        {
            ResolveDependencies();
            Subscribe();
        }

        if (trackingOrchestrator == null)
        {
            Debug.LogWarning("[EvaluationLogger] Cannot restart accuracy trial: TrackingOrchestrator is missing.");
            return;
        }

        trackingOrchestrator.StopDetection();
        autoTrialUsedThisDetectionSession = false;
        lastDetectionEnabled = false;

        bool started = trackingOrchestrator.StartDetection();
        if (!started)
        {
            Debug.LogWarning("[EvaluationLogger] TrackingOrchestrator failed to restart for next accuracy trial.");
            waitingForRestart = false;
            return;
        }

        StartTrial();
        autoTrialUsedThisDetectionSession = true;
        waitingForRestart = false;
        UpdateConditionText(true);
        Debug.Log("[EvaluationLogger] Started next accuracy trial.");
    }

    private void UpdateGlobalAccuracyModeFlag()
    {
        IsAccuracyTestModeActive = isActiveAndEnabled && enableAccuracyTestMode && writeCsvLogs;
    }

    public void SetAccuracyTestModeEnabled(bool enabled)
    {
        enableAccuracyTestMode = enabled;
        UpdateGlobalAccuracyModeFlag();

        if (IsAccuracyTestModeActive)
        {
            conditionTextClearedWhileDisabled = false;
            Subscribe();
            UpdateConditionText(true);
        }
        else
        {
            ResetAccuracyTestRuntimeState();
            Unsubscribe();
            ClearConditionTextIfNeeded();
        }
    }

    private void UpdateConditionTextIfNeeded()
    {
        string status = GetConditionStatusText();
        if (hasDisplayedCondition &&
            lastDisplayedCondition == testCondition &&
            lastDisplayedStatus == status)
        {
            return;
        }

        UpdateConditionText(false);
    }

    private void UpdateConditionText(bool force)
    {
        if (!force && conditionText == null && conditionTmpText == null)
            return;

        hasDisplayedCondition = true;
        lastDisplayedCondition = testCondition;
        lastDisplayedStatus = GetConditionStatusText();

        if (conditionText != null)
            conditionText.text = BuildConditionDisplayText(lastDisplayedStatus);
        if (conditionTmpText != null)
            conditionTmpText.text = BuildConditionDisplayText(lastDisplayedStatus);
    }

    private string BuildConditionDisplayText(string status)
    {
        string text = conditionTextPrefix + GetConditionName(testCondition);
        if (!string.IsNullOrEmpty(status))
            text += "\n" + status;

        return text;
    }

    private string GetConditionStatusText()
    {
        if (!IsAccuracyTestModeActive)
            return "";

        if (currentTrial != null)
            return "Trial running";

        if (!waitingForRestart)
            return "Accuracy test mode";

        float remaining = restartReadyTime - Time.realtimeSinceStartup;
        if (remaining > 0f)
            return $"Saved. Next trial in {remaining:F1}s";

        return $"Saved. Press {restartTrialButton} for next trial";
    }

    private void ResolveDependencies()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = FindFirstObjectByType<TrackingOrchestrator>();
        if (wireframeOverlay == null)
            wireframeOverlay = FindFirstObjectByType<TrackingWireframeOverlay>();
    }

    private void ResetAccuracyTestRuntimeState()
    {
        currentTrial = null;
        waitingForRestart = false;
        autoTrialUsedThisDetectionSession = false;
        lastDetectionEnabled = false;
    }

    private void ClearConditionTextIfNeeded()
    {
        if (conditionTextClearedWhileDisabled)
            return;

        conditionTextClearedWhileDisabled = true;
        hasDisplayedCondition = false;
        lastDisplayedStatus = null;

        if (conditionText != null)
            conditionText.text = "";
        if (conditionTmpText != null)
            conditionTmpText.text = "";
    }

    private void Subscribe()
    {
        if (subscribed || trackingOrchestrator == null)
            return;

        trackingOrchestrator.OnSeedAccepted += HandleSeedAccepted;
        trackingOrchestrator.OnSeedRejected += HandleSeedRejected;
        trackingOrchestrator.OnSeedConfirmed += HandleSeedConfirmed;
        trackingOrchestrator.OnTrackingResultUpdated += HandleTrackingResult;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || trackingOrchestrator == null)
            return;

        trackingOrchestrator.OnSeedAccepted -= HandleSeedAccepted;
        trackingOrchestrator.OnSeedRejected -= HandleSeedRejected;
        trackingOrchestrator.OnSeedConfirmed -= HandleSeedConfirmed;
        trackingOrchestrator.OnTrackingResultUpdated -= HandleTrackingResult;
        subscribed = false;
    }

    private string AllocateTrialId(string objectType)
    {
        string normalized = NormalizeObjectType(-1, objectType);
        if (normalized == "ButtShape")
            return $"butt_{++buttCounter:000}";
        if (normalized == "TShape")
            return $"t_{++tCounter:000}";

        return $"unknown_{++unknownCounter:000}";
    }

    private static string NormalizeObjectType(int classId, string label)
    {
        if (classId == 0)
            return "ButtShape";
        if (classId == 1)
            return "TShape";

        string value = label ?? "";
        string lower = value.ToLowerInvariant();
        if (lower.Contains("butt"))
            return "ButtShape";
        if (lower.Contains("tshape") || lower == "t" || lower.Contains("t-shape"))
            return "TShape";

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static string NormalizeFailureReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Unknown";

        string lower = reason.ToLowerInvariant();
        if (lower.Contains("yolo") || lower.Contains("detection"))
            return "NoYOLODetection";
        if (lower.Contains("depth") || lower.Contains("raycast") || lower.Contains("environment"))
            return "DepthQueryFailed";
        if (lower.Contains("starttrackingfrompose") || lower.Contains("switchobject"))
            return "SRT3DInitFailed";
        if (lower.Contains("confidence") || lower.Contains("conf"))
            return "LowTrackingConfidence";
        if (lower.Contains("timeout"))
            return "Timeout";
        if (lower.Contains("manual"))
            return "ManualStop";

        return reason.Trim();
    }

    private static string GetConditionName(InitializationTestCondition condition)
    {
        switch (condition)
        {
            case InitializationTestCondition.Distance30cm_Horizontal:
                return "30cm_horizontal";
            case InitializationTestCondition.Distance30cm_Clockwise45:
                return "30cm_clockwise45";
            case InitializationTestCondition.Distance30cm_Vertical:
                return "30cm_vertical";
            default:
                return condition.ToString();
        }
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatOptionalTime(bool hasValue, float value)
    {
        return hasValue ? FormatFloat(value) : "";
    }

    private static string FormatOptionalDuration(bool hasValue, float value)
    {
        return hasValue ? FormatFloat(Mathf.Max(0f, value)) : "";
    }

    private static string FormatOptionalFloat(bool hasValue, float value)
    {
        return hasValue ? FormatFloat(value) : "";
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static string ToCsvLine(string[] values)
    {
        for (int i = 0; i < values.Length; i++)
            values[i] = EscapeCsv(values[i]);

        return string.Join(",", values);
    }

    private static string EscapeCsv(string value)
    {
        if (value == null)
            return "";

        bool quote = value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r");
        if (!quote)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed class Trial
    {
        public string TrialId;
        public string ObjectType = "Unknown";
        public string TestCondition;
        public string Mode;
        public float StartTime;
        public float EndTime;
        public bool HasFirstYoloDetection;
        public float FirstYoloDetectionTime;
        public float FirstYoloConfidence;
        public Rect FirstYoloBox;
        public bool DepthQuerySuccess;
        public float InitialDepthValue;
        public Vector3 InitialPosition;
        public Vector3 InitialEulerRotation;
        public float Srt3dInitStartTime = -1f;
        public bool HasFirstValidSrt3dPose;
        public float Srt3dFirstValidPoseTime;
        public float ConfidenceThreshold = -1f;
        public float Srt3dConvergenceTime = -1f;
        public float MaxSrt3dConfidence = -1f;
        public float AvgConfidenceAfterConvergence = -1f;
        public int NumInitializationAttempts;
        public bool Success;
        public string FailureReason = "None";
        public string LastFailureReason;
        public float StablePoseTime = -1f;
        public bool HasFinalPose;
        public Vector3 FinalPosition;
        public Quaternion FinalRotation;
    }
}
