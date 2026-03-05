using System.Collections.Generic;
using UnityEngine;

public class ProcessedWeldingData
{
    public float travelTime;
    public float alignmentError;
}

public class DataProcessor : MonoBehaviour
{
    public DataRecorder dataRecorder;
    private ReferenceLineManager referenceLineManager;
    private List<ProcessedWeldingData> processedData = new List<ProcessedWeldingData>();

    private void Start()
    {
        if (dataRecorder == null)
        {
            Debug.LogError("DataRecorder reference is missing!");
        }
    }

    public void ProcessData()
    {
        if (dataRecorder != null)
        {
            List<WeldingData> rawData = dataRecorder.GetWeldingData();
            processedData.Clear();

            if (referenceLineManager == null)
            {
                referenceLineManager = FindAnyObjectByType<ReferenceLineManager>();
            }

            if (referenceLineManager == null)
            {
                Debug.LogError("ReferenceLineManager not found!");
                return;
            }

            foreach (WeldingData data in rawData)
            {
                // Get closest point on the reference line
                Vector3 closestPoint = referenceLineManager.GetClosestPointOnLine(data.tipPosition, out float distance, out int segmentIndex);
                
                
                // Create processed data entry
                ProcessedWeldingData processed = new ProcessedWeldingData
                {
                    travelTime = data.currentTravelTime,
                    alignmentError = distance
                };
                
                processedData.Add(processed);

                // Optional: Log for debugging
                Debug.Log($"Welding position: {data.tipPosition}, Closest on line: {closestPoint}, Error: {distance:F3}m, Segment: {segmentIndex}");
            }
            referenceLineManager.SetLineRendererVisible(false);

            Debug.Log($"Processed {processedData.Count} data points. Average alignment error: {CalculateAverageError():F3}m");
        }
        else
        {
            Debug.LogError("Cannot process data. DataRecorder reference is missing.");
        }
    }

    private float CalculateAverageError()
    {
        if (processedData.Count == 0) return 0f;
        float sum = 0f;
        foreach (var data in processedData)
        {
            sum += data.alignmentError;
        }
        return sum / processedData.Count;
    }
    public void ClearProcessedData()
    {
        processedData.Clear();
    }

    // Optional: Public getter for processed data
    public List<ProcessedWeldingData> GetProcessedData()
    {
        return new List<ProcessedWeldingData>(processedData);
    }
    
}
