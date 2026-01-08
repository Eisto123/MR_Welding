using System;
using System.Collections.Generic;
using UI.Statistics;
using UnityEngine;

public enum WeldingStepType
{
    PlacePlate,
    PlaceIron,
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
    public PassthroughCameraRenderer passthroughCameraRenderer;
    public ObjectEventSO onProgressToNextStep;
    public DataProcessor dataProcessor;
    public LineGraph lineGraph;
    

    void Start()
    {
        SetCurrentStep(0);
        
    }

    public void SetCurrentStep(int index)
    {
        if (index >= 0 && index < weldingSteps.Count)
        {
            currentStep = weldingSteps[index];
            Debug.Log($"Current welding step set to: {currentStep.stepType}");
            ProcessCurrentStep();
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

    private void ProcessCurrentStep()
    {
        switch (currentStep.stepType)
        {
            case WeldingStepType.PlacePlate:
                Debug.Log("Processing Plate Placement Step");
                passthroughCameraRenderer.enabled = true;
                break;

            case WeldingStepType.PlaceIron:
                Debug.Log("Processing Iron Placement Step");
                passthroughCameraRenderer.enabled = false;

                break;

            case WeldingStepType.Tacking:
                Debug.Log("Processing Tacking Step");
                // enable tracking data from the welding gun
                break;
            case WeldingStepType.Completed:
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
