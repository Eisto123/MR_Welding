using System.Collections.Generic;
using UI.Statistics;
using UnityEngine;
using UnityEngine.Serialization;

public enum WeldingStepType
{
    PlacePlate,
    AutoPlacement,
    Alignment,
    Tacking,
    Completed
}

[System.Serializable]
public class WeldingStep
{
    public WeldingStepType stepType;
    public bool isCompleted;

    public WeldingStep(WeldingStepType type)
    {
        stepType = type;
        isCompleted = false;
    }
}
public class WeldingStepManager : MonoBehaviour
{
    [System.Serializable]
    private class AutoPlacementPrefabBinding
    {
        public int classId = -1;
        public string label = "";
        public GameObject prefab = null;
    }

    public List<WeldingStep> weldingSteps;
    public WeldingStep currentStep;
    [FormerlySerializedAs("Scanner")]
    public TrackingOrchestrator trackingOrchestrator;
    public ObjectEventSO onProgressToNextStep;
    public DataProcessor dataProcessor;
    public LineGraph lineGraph;
    public BeadPaint beadPaint;
    public InstructionPanelManager instructionPanelManager; // <-- add this

    [Header("Auto Placement Prefabs")]
    [SerializeField] private List<AutoPlacementPrefabBinding> autoPlacementPrefabBindings = new List<AutoPlacementPrefabBinding>();
    [SerializeField] private GameObject defaultAutoPlacementPrefab;
    [SerializeField] private Transform autoPlacementPrefabParent;
    [SerializeField] private bool destroyPreviousAutoPlacementPrefab = true;
    [SerializeField, Range(0f, 1f)] private float minimumAutoPlacementConfidence = 0.999f;

    [Header("Tracking Pose Conversion")]
    [SerializeField] private TrackingWireframeOverlay trackingWireframeOverlay;
    [SerializeField] private TrackingSettings trackingSettings;
    [SerializeField] private Transform trackingPoseReferenceTransform;
    [SerializeField] private bool useFramePoseReferenceWhenAvailable = true;
    [SerializeField] private bool poseIsInCameraSpace = true;
    [SerializeField] private bool flipCvYToUnity = false;
    [SerializeField] private float translationScale = 1f;

    private bool autoPlacementDetectionHandled;
    private bool isSubscribedToTrackingResults;
    private GameObject spawnedAutoPlacementPrefab;

    void Start()
    {
        ApplyTrackingPoseSettings();
        SubscribeToTrackingResults();

        SetCurrentStep(0);
    }

    private void OnDestroy()
    {
        if (trackingOrchestrator != null && isSubscribedToTrackingResults)
        {
            trackingOrchestrator.OnTrackingResultUpdated -= HandleTrackingResultUpdated;
        }

        ClearSpawnedAutoPlacementPrefab();
    }

    private void HandleTrackingResultUpdated(TrackingResult result)
    {
        if (autoPlacementDetectionHandled ||
            currentStep == null ||
            currentStep.stepType != WeldingStepType.AutoPlacement)
            return;

        if (!IsAutoPlacementDetectionSuccessful(result))
            return;

        autoPlacementDetectionHandled = true;
        GameObject spawnedPrefab = SpawnAutoPlacementPrefab(result);
        if (spawnedPrefab != null)
        {
            AlignBeadPaintToSpawnedPrefab(spawnedPrefab);
        }
        else
        {
            AlignBeadPaintToTrackingResult(result);
        }

        Debug.Log("AutoPlacement: tracking confirmed. Progressing to next step.");
        progressToNextStep();
    }

    public void SetCurrentStep(int index)
    {
        if (index >= 0 && index < weldingSteps.Count)
        {
            currentStep = weldingSteps[index];
            Debug.Log($"Current welding step set to: {currentStep.stepType}");

            ProcessCurrentStep();

            if (instructionPanelManager != null)
            {
                instructionPanelManager.SetInstruction(currentStep.stepType);
            }
        }
        else
        {
            Debug.LogWarning("Invalid welding step index");
        }

    }

    public void progressToNextStep()
    {
        int currentIndex = weldingSteps.IndexOf(currentStep);
        currentStep.isCompleted = true;
        if (currentIndex + 1 < weldingSteps.Count)
        {
            SetCurrentStep(currentIndex + 1);
        }
        else
        {
            Debug.Log("All welding steps completed.");
        }
        onProgressToNextStep.RaiseEvent(currentStep.stepType, this);
    }
    public void ReloadCurrentScene()
    {
        SceneLoader.Instance.LoadSceneByName(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
    }

    private void ProcessCurrentStep()
    {
        switch (currentStep.stepType)
        {
            case WeldingStepType.PlacePlate:
                Debug.Log("Processing Plate Placement Step");
                StopDetection();
                ClearSpawnedAutoPlacementPrefab();
                //if (beadPaint != null) beadPaint.ResetToDefaultAndClear();
                if (dataProcessor != null) dataProcessor.ClearProcessedData();
                lineGraph?.Clear();    
                break;

            case WeldingStepType.AutoPlacement:
                Debug.Log("Processing Auto Placement Step");
                autoPlacementDetectionHandled = false;
                ClearSpawnedAutoPlacementPrefab();
                if (TryResolveTrackingOrchestrator())
                {
                    SubscribeToTrackingResults();
                    if (!trackingOrchestrator.StartDetection())
                    {
                        Debug.LogWarning("TrackingOrchestrator failed to start detection for AutoPlacement.");
                    }
                }
                else
                {
                    Debug.LogWarning("TrackingOrchestrator reference is missing. Cannot run AutoPlacement detection.");
                }
                break;

            case WeldingStepType.Alignment:
                Debug.Log("Processing Alignment Step");
                StopDetection();
                break;

            case WeldingStepType.Tacking:
                Debug.Log("Processing Tacking Step");
                StopDetection();
                
                break;
            case WeldingStepType.Completed:
                StopDetection();
                Debug.Log("Welding Completed!");
                dataProcessor.ProcessData();
                var graphData = dataProcessor.GetProcessedData();

                // Convert ProcessedWeldingData to Vector2 (X: travelTime rounded to nearest 0.5, Y: alignmentError rounded to nearest integer)
                List<Vector2> points = new List<Vector2>();
                foreach (var point in graphData)
                {
                    float roundedTime = Mathf.Round(point.travelTime * 2f) / 2f;  // Round to nearest 0.5
                    float roundedError = Mathf.Round(point.alignmentError * 1000f);  // Round to nearest integer (mm)
                    points.Add(new Vector2(roundedTime, roundedError));
                    Debug.Log($"Graph Point - Time: {roundedTime}, Error: {roundedError}mm");
                }

                // Create margin line: flat at Y=4 with same X values
                List<Vector2> marginPoints = new List<Vector2>();
                foreach (var p in points)
                {
                    marginPoints.Add(new Vector2(p.x, 4f));  // Flat line at 4
                }

                // Create the line graph data (two lines: data and margin)
                ILineGraphData lineGraphData = new SimpleLineGraphData(points, marginPoints);

                // Plot the graph on the LineGraph component
                if (lineGraph != null)
                {
                    lineGraph.PlotGraph(lineGraphData);
                    Debug.Log("Line graph plotted with " + points.Count + " points (data) and margin line.");
                }
                else
                {
                    Debug.LogError("LineGraph reference is missing!");
                }
                break;
            
            default:
                Debug.LogWarning("Unknown welding step type");
                break;
        }
    }

    private bool TryResolveTrackingOrchestrator()
    {
        if (trackingOrchestrator == null)
        {
            trackingOrchestrator = FindAnyObjectByType<TrackingOrchestrator>();
        }

        return trackingOrchestrator != null;
    }

    private bool TryResolveTrackingWireframeOverlay()
    {
        if (trackingWireframeOverlay == null && trackingOrchestrator != null)
        {
            trackingWireframeOverlay = trackingOrchestrator.GetComponentInChildren<TrackingWireframeOverlay>();
        }

        if (trackingWireframeOverlay == null)
        {
            trackingWireframeOverlay = FindAnyObjectByType<TrackingWireframeOverlay>();
        }

        return trackingWireframeOverlay != null;
    }

    private void SubscribeToTrackingResults()
    {
        if (!TryResolveTrackingOrchestrator() || isSubscribedToTrackingResults)
            return;

        trackingOrchestrator.OnTrackingResultUpdated += HandleTrackingResultUpdated;
        isSubscribedToTrackingResults = true;
    }

    private void StopDetection()
    {
        if (TryResolveTrackingOrchestrator())
        {
            trackingOrchestrator.StopDetection();
        }
    }

    private bool IsAutoPlacementDetectionSuccessful(TrackingResult result)
    {
        if (result.State != TrackingState.Tracking || !result.PoseValid || !result.IsConfirmed)
            return false;

        if (minimumAutoPlacementConfidence <= 0f)
            return true;

        return result.HasConfidence && result.Confidence >= minimumAutoPlacementConfidence;
    }

    private GameObject SpawnAutoPlacementPrefab(TrackingResult result)
    {
        GameObject prefab = GetAutoPlacementPrefab(result.TrackedClassId);
        if (prefab == null)
        {
            Debug.LogWarning($"AutoPlacement: no prefab mapped for class {result.TrackedClassId} ({result.TrackedLabel}).");
            return null;
        }

        if (!TryBuildAutoPlacementWorldPose(result, out Vector3 worldPosition, out Quaternion worldRotation))
        {
            Debug.LogWarning("AutoPlacement: failed to build wireframe pose for prefab placement.");
            return null;
        }

        if (destroyPreviousAutoPlacementPrefab)
        {
            ClearSpawnedAutoPlacementPrefab();
        }

        spawnedAutoPlacementPrefab = Instantiate(prefab, worldPosition, worldRotation, autoPlacementPrefabParent);
        string label = string.IsNullOrWhiteSpace(result.TrackedLabel) ? $"Class{result.TrackedClassId}" : result.TrackedLabel;
        spawnedAutoPlacementPrefab.name = $"AutoPlacement_{label}";

        string confidenceText = result.HasConfidence ? result.Confidence.ToString("F3") : "N/A";
        Debug.Log(
            $"AutoPlacement: spawned prefab '{prefab.name}' for class {result.TrackedClassId} ({label}) " +
            $"at wireframe pose. conf={confidenceText}");

        return spawnedAutoPlacementPrefab;
    }

    private GameObject GetAutoPlacementPrefab(int classId)
    {
        if (autoPlacementPrefabBindings != null)
        {
            for (int i = 0; i < autoPlacementPrefabBindings.Count; i++)
            {
                AutoPlacementPrefabBinding binding = autoPlacementPrefabBindings[i];
                if (binding != null && binding.classId == classId && binding.prefab != null)
                    return binding.prefab;
            }
        }

        return defaultAutoPlacementPrefab;
    }

    private void ClearSpawnedAutoPlacementPrefab()
    {
        if (spawnedAutoPlacementPrefab == null)
            return;

        Destroy(spawnedAutoPlacementPrefab);
        spawnedAutoPlacementPrefab = null;
    }

    private void ApplyTrackingPoseSettings()
    {
        if (trackingSettings == null)
            return;

        poseIsInCameraSpace = trackingSettings.PoseIsInCameraSpace;
        flipCvYToUnity = trackingSettings.FlipCvYToUnity;
        translationScale = trackingSettings.TranslationScale;
    }

    private void AlignBeadPaintToSpawnedPrefab(GameObject spawnedPrefab)
    {
        if (beadPaint == null || spawnedPrefab == null)
            return;

        beadPaint.AlignToTrackedObject(spawnedPrefab.transform);
    }

    private void AlignBeadPaintToTrackingResult(TrackingResult result)
    {
        if (beadPaint == null)
            return;

        if (!TryBuildTrackingWorldPose(result, out Vector3 worldPosition, out Quaternion worldRotation))
        {
            Debug.LogWarning("AutoPlacement: failed to convert TrackingResult pose for BeadPaint alignment.");
            return;
        }

        beadPaint.transform.SetPositionAndRotation(worldPosition, worldRotation);
        Debug.Log("BeadPaint: aligned to TrackingOrchestrator pose.");
    }

    private bool TryBuildAutoPlacementWorldPose(
        TrackingResult result,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        if (TryResolveTrackingWireframeOverlay() &&
            trackingWireframeOverlay.TryBuildWireframeWorldPose(result, out worldPosition, out worldRotation))
        {
            return true;
        }

        return TryBuildTrackingWorldPose(result, out worldPosition, out worldRotation);
    }

    private bool TryBuildTrackingWorldPose(
        TrackingResult result,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        if (useFramePoseReferenceWhenAvailable &&
            poseIsInCameraSpace &&
            result.HasPoseReference)
        {
            return PoseConverter.TryBuildUnityPose(
                result.RowMajorPose16,
                flipCvYToUnity,
                translationScale,
                true,
                result.PoseReferencePosition,
                result.PoseReferenceRotation,
                out worldPosition,
                out worldRotation);
        }

        if (trackingPoseReferenceTransform == null && Camera.main != null)
        {
            trackingPoseReferenceTransform = Camera.main.transform;
        }

        return PoseConverter.TryBuildUnityPose(
            result.RowMajorPose16,
            flipCvYToUnity,
            translationScale,
            poseIsInCameraSpace,
            poseIsInCameraSpace ? trackingPoseReferenceTransform : null,
            out worldPosition,
            out worldRotation);
    }
}
