using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class ReferenceLineManager : MonoBehaviour
{
    // Public fields for the two objects defining the reference line
    public Transform startObject;  // The object at the "negative length" end
    public Transform endObject;    // The object at the "positive length" end

    public int _resolution = 10; // Total number of points (not per unit; adjust as needed for density)
    public LineRenderer _lineRenderer;
    private DataRecorder dataRecorder; // Found at runtime
    private List<Vector3> _referenceLinePoints = new List<Vector3>();
    private List<Vector3> _keyPoints = new List<Vector3>();  // Switched back to Vector3 for dynamic calculation
    private bool isLineFrozen = false; // Track if line has been frozen
    
    // Store the local offsets when freezing
    private Vector3 startPointLocalOffset;
    private Vector3 endPointLocalOffset;

    void Start()
    {
        // Find DataRecorder in the scene
        dataRecorder = FindAnyObjectByType<DataRecorder>();
    }

    void Update()
    {
        // Check if we should freeze the line (once recorder has data)
        if (dataRecorder != null && !isLineFrozen)
        {
            if (dataRecorder.GetWeldingData().Count > 0)
            {
                CaptureLineOffsets();
                isLineFrozen = true;
            }
        }

        // Update the reference line every frame
        if (!isLineFrozen)
        {
            UpdateReferenceLine();
        }
        else
        {
            UpdateFrozenReferenceLine();
        }
    }

    private void CaptureLineOffsets()
    {
        if (startObject == null || _keyPoints.Count < 2) return;

        // Calculate local offsets from startObject to the start and end points
        startPointLocalOffset = startObject.InverseTransformPoint(_keyPoints[0]);
        endPointLocalOffset = startObject.InverseTransformPoint(_keyPoints[1]);
    }

    private void UpdateFrozenReferenceLine()
    {
        if (startObject == null) return;

        // Transform the stored local offsets back to world space using startObject's current transform
        Vector3 startPoint = startObject.TransformPoint(startPointLocalOffset);
        Vector3 endPoint = startObject.TransformPoint(endPointLocalOffset);

        // Update _keyPoints with the transformed points
        _keyPoints.Clear();
        _keyPoints.Add(startPoint);
        _keyPoints.Add(endPoint);

        // Regenerate the line
        SetupLineRenderer();
        GenerateReferenceLinePoints();
    }

    private void UpdateReferenceLine()
    {
        if (startObject == null || endObject == null)
        {
            Debug.LogWarning("StartObject or EndObject is not assigned!");
            return;
        }

        // Get bounds for corner calculations (assumes Renderer component exists)
        Renderer startRenderer = startObject.GetComponent<Renderer>();
        Renderer endRenderer = endObject.GetComponent<Renderer>();
        if (startRenderer == null || endRenderer == null)
        {
            Debug.LogWarning("StartObject or EndObject must have a Renderer component!");
            return;
        }

        Bounds startBounds = startRenderer.bounds;
        Bounds endBounds = endRenderer.bounds;

        // Calculate corners on the top face (max Y)
        // StartObject bottom-left: min X, max Y, min Z
        Vector3 startBottomLeft = new Vector3(startBounds.min.x, startBounds.max.y, startBounds.min.z);
        // StartObject bottom-right: max X, max Y, min Z
        Vector3 startBottomRight = new Vector3(startBounds.max.x, startBounds.max.y, startBounds.min.z);
        // EndObject top-left: min X, max Y, max Z
        Vector3 endTopLeft = new Vector3(endBounds.min.x, endBounds.max.y, endBounds.max.z);
        // EndObject top-right: max X, max Y, max Z
        Vector3 endTopRight = new Vector3(endBounds.max.x, endBounds.max.y, endBounds.max.z);

        // Start point: midpoint between startObject's bottom-left and endObject's top-left
        Vector3 startPoint = (startBottomLeft + endTopLeft) / 2f;

        // End point: midpoint between startObject's bottom-right and endObject's top-right
        Vector3 endPoint = (startBottomRight + endTopRight) / 2f;

        // Update _keyPoints with the new start and end points
        _keyPoints.Clear();
        _keyPoints.Add(startPoint);
        _keyPoints.Add(endPoint);

        // Regenerate the line
        SetupLineRenderer();
        GenerateReferenceLinePoints();
    }

    private void SetupLineRenderer()
    {
        if (_lineRenderer != null)
        {
            // Ensure the LineRenderer uses local space (relative to its transform)
            _lineRenderer.useWorldSpace = false;

            _lineRenderer.positionCount = _keyPoints.Count;

            // Transform world-space _keyPoints to local positions relative to the LineRenderer's transform
            Vector3[] localPositions = new Vector3[_keyPoints.Count];
            for (int i = 0; i < _keyPoints.Count; i++)
            {
                localPositions[i] = _lineRenderer.transform.InverseTransformPoint(_keyPoints[i]);
            }

            _lineRenderer.SetPositions(localPositions);
        }
    }

    private void GenerateReferenceLinePoints()
    {
        _referenceLinePoints.Clear();

        if (_keyPoints.Count < 2)
        {
            Debug.LogWarning("Need at least 2 key points to generate reference line");
            return;
        }

        // Calculate total path length
        float totalLength = 0f;
        for (int i = 0; i < _keyPoints.Count - 1; i++)
        {
            totalLength += Vector3.Distance(_keyPoints[i], _keyPoints[i + 1]);
        }

        // Calculate total number of points based on resolution
        int totalPoints = _resolution;
        float segmentLength = totalLength / (totalPoints - 1);

        // Generate points along the path
        _referenceLinePoints.Add(_keyPoints[0]); // Start point

        float accumulatedLength = 0f;
        int currentSegment = 0;
        
        for (int i = 1; i < totalPoints - 1; i++)
        {
            float targetLength = i * segmentLength;
            
            // Find which segment this point should be on
            while (currentSegment < _keyPoints.Count - 1)
            {
                float segmentStart = accumulatedLength;
                float segmentDistance = Vector3.Distance(_keyPoints[currentSegment], _keyPoints[currentSegment + 1]);
                float segmentEnd = accumulatedLength + segmentDistance;

                if (targetLength <= segmentEnd)
                {
                    // Interpolate within this segment
                    float t = (targetLength - segmentStart) / segmentDistance;
                    Vector3 point = Vector3.Lerp(_keyPoints[currentSegment], _keyPoints[currentSegment + 1], t);
                    _referenceLinePoints.Add(point);
                    break;
                }
                else
                {
                    // Move to next segment
                    accumulatedLength = segmentEnd;
                    currentSegment++;
                }
            }
        }

        _referenceLinePoints.Add(_keyPoints[^1]); // End point
    }

    // Optional: Visualize the reference points in Scene view
    private void OnDrawGizmos()
    {
        if (_referenceLinePoints.Count == 0) return;

        Gizmos.color = Color.green;
        foreach (var point in _referenceLinePoints)
        {
            Gizmos.DrawSphere(point, 0.005f);
        }

        // Draw key points in different color
        Gizmos.color = Color.red;
        foreach (var keyPoint in _keyPoints)
        {
            Gizmos.DrawSphere(keyPoint, 0.01f);
        }
    }

    // Public getter for other systems to access reference points
    public List<Vector3> GetReferenceLinePoints()
    {
        return new List<Vector3>(_referenceLinePoints);
    }

    // Get the closest point on the reference line (continuous polyline) to a given position
    // Returns the closest point, distance to it, and the index of the segment it belongs to
    public Vector3 GetClosestPointOnLine(Vector3 position, out float distance, out int segmentIndex)
    {
        if (_referenceLinePoints.Count < 2)
        {
            // Fallback: if not enough points, return the first point
            distance = Vector3.Distance(position, _referenceLinePoints[0]);
            segmentIndex = 0;
            return _referenceLinePoints[0];
        }

        Vector3 closestPoint = _referenceLinePoints[0];
        distance = float.MaxValue;
        segmentIndex = 0;

        // Iterate through each segment (pair of consecutive points)
        for (int i = 0; i < _referenceLinePoints.Count - 1; i++)
        {
            Vector3 a = _referenceLinePoints[i];
            Vector3 b = _referenceLinePoints[i + 1];
            
            // Compute closest point on the finite line segment [a, b]
            Vector3 ab = b - a;
            float abLengthSq = Vector3.Dot(ab, ab);
            if (abLengthSq == 0f)
            {
                // Degenerate segment (a == b), skip or treat as point
                continue;
            }
            
            // Project position onto the infinite line, then clamp to segment
            float t = Vector3.Dot(position - a, ab) / abLengthSq;
            t = Mathf.Clamp01(t); // Clamp to [0, 1] for finite segment
            Vector3 candidatePoint = a + t * ab;
            
            // Check distance to this candidate
            float candidateDistance = Vector3.Distance(position, candidatePoint);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                closestPoint = candidatePoint;
                segmentIndex = i;
            }
        }

        return closestPoint;
    }

    // Get progress along the line (0 to 1) for a given position
    public float GetProgressAlongLine(Vector3 position)
    {
        if (_referenceLinePoints.Count == 0) return 0f;

        GetClosestPointOnLine(position, out float distance, out int segmentIndex);
        // Approximate progress: segmentIndex / totalSegments, but could refine with exact position along segment
        return (float)segmentIndex / (_referenceLinePoints.Count - 1);
    }
}
