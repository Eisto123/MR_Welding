using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class RuntimePerformanceLogger : MonoBehaviour
{
    public static bool IsRuntimeFpsTestModeActive { get; private set; }

    private const int UnknownStepKey = -1;
    private const int NoActiveStepKey = int.MinValue;

    [Header("Dependencies")]
    [SerializeField] private WeldingStepManager weldingStepManager;

    [Header("Experiment Switch")]
    [Tooltip("OFF restores the normal welding flow and ignores the FPS record button. ON enables runtime FPS capture by welding step.")]
    [SerializeField] private bool enableRuntimeFpsTestMode = false;

    [Header("Capture")]
    [SerializeField] private bool writeCsvLogs = true;
    [Tooltip("Automatically stop and flush this capture after the specified duration. Use 0 for manual stop.")]
    [SerializeField, Range(0f, 600f)] private float captureDurationSeconds = 0f;
    [SerializeField, Range(1, 30)] private int sampleEveryNFrames = 1;
    [SerializeField] private bool captureOnStart = false;

    [Header("Controller")]
    [SerializeField] private bool allowControllerCaptureToggle = true;
    [SerializeField] private OVRInput.RawButton toggleCaptureButton = OVRInput.RawButton.Y;

    [Header("UI")]
    [SerializeField] private Text statusText;
    [SerializeField] private TMP_Text statusTmpText;
    [SerializeField, Range(0.05f, 2f)] private float uiUpdateIntervalSeconds = 0.25f;

    [Header("Output")]
    [SerializeField] private string rootFolderName = "EvaluationLogs";
    [SerializeField] private string stepFpsLogFileName = "RuntimeStepFpsLog.csv";
    [SerializeField] private string stepMarkerLogFileName = "RuntimeStepMarkers.csv";
    [SerializeField] private bool logFolderPathOnStart = true;

    private static readonly string[] StepFpsHeader =
    {
        "capture_id",
        "capture_started_at",
        "step_name",
        "first_frame",
        "last_frame",
        "start_elapsed_time",
        "end_elapsed_time",
        "duration_seconds",
        "sample_count",
        "avg_fps",
        "avg_frame_time_ms",
        "min_fps",
        "max_fps"
    };

    private static readonly string[] StepMarkerHeader =
    {
        "capture_id",
        "capture_started_at",
        "event",
        "step_name",
        "frame_id",
        "elapsed_time",
        "timestamp"
    };

    private readonly Dictionary<int, StepFpsStats> stepStats = new Dictionary<int, StepFpsStats>();
    private readonly List<StepMarker> stepMarkers = new List<StepMarker>(16);

    private string outputFolderPath;
    private string stepFpsLogPath;
    private string stepMarkerLogPath;
    private bool isCapturing;
    private int captureCounter;
    private string currentCaptureId;
    private string captureStartedAt;
    private float captureStartTime;
    private float nextUiUpdateTime;
    private int activeStepKey = NoActiveStepKey;
    private StepFpsStats activeStepStats;
    private bool statusTextClearedWhileDisabled;

    private void Awake()
    {
        ResolveDependencies();
    }

    private void OnEnable()
    {
        UpdateGlobalRuntimeModeFlag();
    }

    private void Start()
    {
        ResolveDependencies();
        UpdateGlobalRuntimeModeFlag();
        if (IsRuntimeFpsTestModeActive)
            UpdateStatusText(true);
        else
            ClearStatusTextIfNeeded();

        if (IsRuntimeFpsTestModeActive && captureOnStart)
            StartCapture();
    }

    private void Update()
    {
        UpdateGlobalRuntimeModeFlag();
        if (!IsRuntimeFpsTestModeActive)
        {
            if (isCapturing)
                StopCapture();

            ClearStatusTextIfNeeded();
            return;
        }

        if (allowControllerCaptureToggle && OVRInput.GetDown(toggleCaptureButton))
        {
            if (isCapturing)
                StopCapture();
            else
                StartCapture();
        }

        if (!isCapturing)
        {
            UpdateStatusText(false);
            return;
        }

        if (captureDurationSeconds > 0f &&
            Time.realtimeSinceStartup - captureStartTime >= captureDurationSeconds)
        {
            StopCapture();
            return;
        }

        if (sampleEveryNFrames <= 1 || Time.frameCount % sampleEveryNFrames == 0)
            RecordFpsSample();

        UpdateStatusText(false);
    }

    private void OnDisable()
    {
        if (isCapturing)
            StopCapture();

        IsRuntimeFpsTestModeActive = false;
        ClearStatusTextIfNeeded();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause && isCapturing)
            StopCapture();
    }

    private void OnApplicationQuit()
    {
        if (isCapturing)
            StopCapture();
    }

    public void StartCapture()
    {
        UpdateGlobalRuntimeModeFlag();
        if (!IsRuntimeFpsTestModeActive || !writeCsvLogs || isCapturing)
            return;

        ResolveDependencies();
        EnsureLogFiles();

        stepStats.Clear();
        stepMarkers.Clear();
        activeStepKey = NoActiveStepKey;
        activeStepStats = null;

        captureCounter++;
        captureStartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        currentCaptureId = $"runtime_fps_{DateTime.Now:yyyyMMdd_HHmmss}_{captureCounter:000}";
        captureStartTime = Time.realtimeSinceStartup;
        isCapturing = true;

        Debug.Log("[RuntimePerformanceLogger] FPS capture started: " + currentCaptureId);
        UpdateStatusText(true);
    }

    public void StopCapture()
    {
        if (!isCapturing)
            return;

        float endTime = Time.realtimeSinceStartup;
        AddStepMarker("exit", activeStepStats, endTime - captureStartTime);
        isCapturing = false;
        FlushCapture();

        Debug.Log("[RuntimePerformanceLogger] FPS capture stopped: " + currentCaptureId);
        UpdateStatusText(true);
    }

    public void SetRuntimeFpsTestModeEnabled(bool enabled)
    {
        enableRuntimeFpsTestMode = enabled;
        UpdateGlobalRuntimeModeFlag();

        if (IsRuntimeFpsTestModeActive)
        {
            statusTextClearedWhileDisabled = false;
            UpdateStatusText(true);
        }
        else
        {
            if (isCapturing)
                StopCapture();
            ClearStatusTextIfNeeded();
        }
    }

    [Obsolete("RuntimePerformanceLogger now groups FPS by current WeldingStep.")]
    public void SetStageDetectionAndInitialization()
    {
        UpdateStatusText(true);
    }

    [Obsolete("RuntimePerformanceLogger now groups FPS by current WeldingStep.")]
    public void SetStageSrt3DTracking()
    {
        UpdateStatusText(true);
    }

    [Obsolete("RuntimePerformanceLogger now groups FPS by current WeldingStep.")]
    public void SetStageWeldingInteraction()
    {
        UpdateStatusText(true);
    }

    [Obsolete("RuntimePerformanceLogger now groups FPS by current WeldingStep.")]
    public void SetStageFullPipeline()
    {
        UpdateStatusText(true);
    }

    [Obsolete("RuntimePerformanceLogger now groups FPS by current WeldingStep.")]
    public void CycleStage()
    {
        UpdateStatusText(true);
    }

    private void RecordFpsSample()
    {
        int stepKey = GetCurrentStepKey();
        float elapsedTime = Time.realtimeSinceStartup - captureStartTime;

        if (stepKey != activeStepKey)
            SwitchActiveStep(stepKey, elapsedTime);

        float frameTimeMs = Mathf.Max(0.0001f, Time.unscaledDeltaTime * 1000f);
        float fps = 1000f / frameTimeMs;
        activeStepStats.AddSample(Time.frameCount, elapsedTime, frameTimeMs, fps);
    }

    private void SwitchActiveStep(int stepKey, float elapsedTime)
    {
        AddStepMarker("exit", activeStepStats, elapsedTime);

        activeStepKey = stepKey;
        activeStepStats = GetOrCreateStepStats(stepKey);
        AddStepMarker("enter", activeStepStats, elapsedTime);
    }

    private StepFpsStats GetOrCreateStepStats(int stepKey)
    {
        if (stepStats.TryGetValue(stepKey, out StepFpsStats stats))
            return stats;

        stats = new StepFpsStats(GetStepName(stepKey));
        stepStats.Add(stepKey, stats);
        return stats;
    }

    private void AddStepMarker(string eventName, StepFpsStats stats, float elapsedTime)
    {
        if (stats == null)
            return;

        stepMarkers.Add(new StepMarker
        {
            CaptureId = currentCaptureId,
            CaptureStartedAt = captureStartedAt,
            EventName = eventName,
            StepName = stats.StepName,
            FrameId = Time.frameCount,
            ElapsedTime = elapsedTime,
            Timestamp = Time.realtimeSinceStartup
        });
    }

    private void FlushCapture()
    {
        EnsureLogFiles();

        if (stepStats.Count > 0)
        {
            var orderedStats = new List<StepFpsStats>(stepStats.Values);
            orderedStats.Sort((a, b) => a.StartElapsedTime.CompareTo(b.StartElapsedTime));

            var builder = new StringBuilder(orderedStats.Count * 160);
            for (int i = 0; i < orderedStats.Count; i++)
            {
                StepFpsStats stats = orderedStats[i];
                if (stats.SampleCount > 0)
                    builder.AppendLine(ToCsvLine(BuildStepFpsRow(stats)));
            }

            if (builder.Length > 0)
                File.AppendAllText(stepFpsLogPath, builder.ToString(), Encoding.UTF8);
        }

        if (stepMarkers.Count > 0)
        {
            var builder = new StringBuilder(stepMarkers.Count * 120);
            for (int i = 0; i < stepMarkers.Count; i++)
                builder.AppendLine(ToCsvLine(BuildStepMarkerRow(stepMarkers[i])));

            File.AppendAllText(stepMarkerLogPath, builder.ToString(), Encoding.UTF8);
        }
    }

    private void ResolveDependencies()
    {
        if (weldingStepManager == null)
            weldingStepManager = FindFirstObjectByType<WeldingStepManager>();
    }

    private void UpdateGlobalRuntimeModeFlag()
    {
        IsRuntimeFpsTestModeActive = isActiveAndEnabled && enableRuntimeFpsTestMode && writeCsvLogs;
    }

    private int GetCurrentStepKey()
    {
        if (weldingStepManager == null || weldingStepManager.currentStep == null)
            return UnknownStepKey;

        return (int)weldingStepManager.currentStep.stepType;
    }

    private static string GetStepName(int stepKey)
    {
        if (stepKey == UnknownStepKey)
            return "Unknown";

        return ((WeldingStepType)stepKey).ToString();
    }

    private void EnsureLogFiles()
    {
        if (!string.IsNullOrEmpty(stepFpsLogPath) && !string.IsNullOrEmpty(stepMarkerLogPath))
            return;

        outputFolderPath = Path.Combine(Application.persistentDataPath, rootFolderName);
        Directory.CreateDirectory(outputFolderPath);

        stepFpsLogPath = Path.Combine(outputFolderPath, stepFpsLogFileName);
        stepMarkerLogPath = Path.Combine(outputFolderPath, stepMarkerLogFileName);
        EnsureHeader(stepFpsLogPath, StepFpsHeader);
        EnsureHeader(stepMarkerLogPath, StepMarkerHeader);

        if (logFolderPathOnStart)
            Debug.Log("[RuntimePerformanceLogger] Writing FPS logs to: " + outputFolderPath);
    }

    private static void EnsureHeader(string path, string[] header)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            File.WriteAllText(path, ToCsvLine(header) + Environment.NewLine, Encoding.UTF8);
    }

    private void UpdateStatusText(bool force)
    {
        statusTextClearedWhileDisabled = false;

        if (!force && Time.realtimeSinceStartup < nextUiUpdateTime)
            return;

        nextUiUpdateTime = Time.realtimeSinceStartup + uiUpdateIntervalSeconds;

        string stepName = activeStepStats != null ? activeStepStats.StepName : GetStepName(GetCurrentStepKey());
        string text;
        if (isCapturing)
        {
            float elapsed = Time.realtimeSinceStartup - captureStartTime;
            string avgFps = activeStepStats != null && activeStepStats.SampleCount > 0
                ? activeStepStats.AvgFps.ToString("F1", CultureInfo.InvariantCulture)
                : "--";
            text = $"Runtime FPS\nRecording {elapsed:0.0}s\nStep: {stepName}\nAvg FPS: {avgFps}";
        }
        else
        {
            text = $"Runtime FPS\nStep: {stepName}\n{toggleCaptureButton}: record";
        }

        if (statusText != null)
            statusText.text = text;
        if (statusTmpText != null)
            statusTmpText.text = text;
    }

    private void ClearStatusTextIfNeeded()
    {
        if (statusTextClearedWhileDisabled)
            return;

        statusTextClearedWhileDisabled = true;
        if (statusText != null)
            statusText.text = "";
        if (statusTmpText != null)
            statusTmpText.text = "";
    }

    private string[] BuildStepFpsRow(StepFpsStats stats)
    {
        return new[]
        {
            currentCaptureId,
            captureStartedAt,
            stats.StepName,
            stats.FirstFrame.ToString(CultureInfo.InvariantCulture),
            stats.LastFrame.ToString(CultureInfo.InvariantCulture),
            FormatFloat(stats.StartElapsedTime),
            FormatFloat(stats.EndElapsedTime),
            FormatFloat(stats.DurationSeconds),
            stats.SampleCount.ToString(CultureInfo.InvariantCulture),
            FormatFloat(stats.AvgFps),
            FormatFloat(stats.AvgFrameTimeMs),
            FormatFloat(stats.MinFps),
            FormatFloat(stats.MaxFps)
        };
    }

    private static string[] BuildStepMarkerRow(StepMarker marker)
    {
        return new[]
        {
            marker.CaptureId,
            marker.CaptureStartedAt,
            marker.EventName,
            marker.StepName,
            marker.FrameId.ToString(CultureInfo.InvariantCulture),
            FormatFloat(marker.ElapsedTime),
            FormatFloat(marker.Timestamp)
        };
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

    private sealed class StepFpsStats
    {
        public readonly string StepName;
        public int FirstFrame = -1;
        public int LastFrame = -1;
        public float StartElapsedTime;
        public float EndElapsedTime;
        public float DurationSeconds;
        public int SampleCount;
        public float MinFps;
        public float MaxFps;
        private double frameTimeSumMs;

        public StepFpsStats(string stepName)
        {
            StepName = stepName;
        }

        public float AvgFrameTimeMs => SampleCount > 0 ? (float)(frameTimeSumMs / SampleCount) : 0f;
        public float AvgFps => SampleCount > 0 ? 1000f / Mathf.Max(0.0001f, AvgFrameTimeMs) : 0f;

        public void AddSample(int frameId, float elapsedTime, float frameTimeMs, float fps)
        {
            if (SampleCount == 0)
            {
                FirstFrame = frameId;
                StartElapsedTime = elapsedTime;
                MinFps = fps;
                MaxFps = fps;
            }
            else
            {
                MinFps = Mathf.Min(MinFps, fps);
                MaxFps = Mathf.Max(MaxFps, fps);
            }

            LastFrame = frameId;
            EndElapsedTime = elapsedTime;
            DurationSeconds += frameTimeMs / 1000f;
            frameTimeSumMs += frameTimeMs;
            SampleCount++;
        }
    }

    private struct StepMarker
    {
        public string CaptureId;
        public string CaptureStartedAt;
        public string EventName;
        public string StepName;
        public int FrameId;
        public float ElapsedTime;
        public float Timestamp;
    }
}
