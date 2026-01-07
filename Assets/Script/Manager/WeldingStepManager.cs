using System;
using System.Collections.Generic;
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
                // visualize data for review.
                break;
            
            default:
                Debug.LogWarning("Unknown welding step type");
                break;
        }
    }
}
