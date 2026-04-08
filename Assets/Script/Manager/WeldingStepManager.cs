using System;
using System.Collections.Generic;
using UI.Statistics;
using UnityEngine;
using UnityEngine.Profiling;

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
    public List<WeldingStep> weldingSteps;
    public WeldingStep currentStep;
    public QuestCamera Scanner;
    public ObjectEventSO onProgressToNextStep;
    public DataProcessor dataProcessor;
    public LineGraph lineGraph;
    public BeadPaint beadPaint;
    public InstructionPanelManager instructionPanelManager; // <-- add this

    void Start()
    {
        if (Scanner != null)
        {
            Scanner.OnAutoPlacementObjectFound += HandleAutoPlacementObjectFound;
        }

        SetCurrentStep(0);
    }

    private void OnDestroy()
    {
        if (Scanner != null)
        {
            Scanner.OnAutoPlacementObjectFound -= HandleAutoPlacementObjectFound;
        }
    }

    private void HandleAutoPlacementObjectFound()
    {
        if (currentStep != null && currentStep.stepType == WeldingStepType.AutoPlacement)
        {
            if (beadPaint != null && Scanner != null && Scanner.LastAutoPlacementTarget != null)
            {
                beadPaint.AlignToTrackedObject(Scanner.LastAutoPlacementTarget);
            }

            Debug.Log("AutoPlacement: object found. Progressing to next step.");
            progressToNextStep();
        }
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
                if (Scanner != null) Scanner.StopAutoPlacementScan();
                //if (beadPaint != null) beadPaint.ResetToDefaultAndClear();
                if (dataProcessor != null) dataProcessor.ClearProcessedData();
                lineGraph?.Clear();    
                break;

            case WeldingStepType.AutoPlacement:
                Debug.Log("Processing Auto Placement Step");
                if (Scanner != null)
                {
                    Scanner.StartAutoPlacementScan();
                }
                else
                {
                    Debug.LogWarning("Scanner reference is missing. Cannot run AutoPlacement scan.");
                }
                break;

            case WeldingStepType.Alignment:
                Debug.Log("Processing Alignment Step");
                if (Scanner != null) Scanner.StopAutoPlacementScan();
                break;

            case WeldingStepType.Tacking:
                Debug.Log("Processing Tacking Step");
                if (Scanner != null) Scanner.StopAutoPlacementScan();
                
                break;
            case WeldingStepType.Completed:
                if (Scanner != null) Scanner.StopAutoPlacementScan();
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
}
