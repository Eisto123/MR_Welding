using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class ReferenceLineManager : MonoBehaviour
{
    public List<Vector3> _keyPoints = new List<Vector3>();
    public int _resolution = 10; // Points per unit length
    public LineRenderer _lineRenderer;
    private List<Vector3> _referenceLinePoints = new List<Vector3>();

    void OnEnable()
    {
        SetupLineRenderer();
        GenerateReferenceLinePoints();
    }

    private void SetupLineRenderer()
    {
        if(_lineRenderer != null)
        {
            _lineRenderer.positionCount = _keyPoints.Count;
            _lineRenderer.SetPositions(_keyPoints.ToArray());
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

        Debug.Log($"Total path length: {totalLength:F3}m, Generating {totalPoints} points");

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

        Debug.Log($"Generated {_referenceLinePoints.Count} reference line points");
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

    // Get the closest point on the reference line to a given position
    public Vector3 GetClosestPointOnLine(Vector3 position, out float distance, out int nearestIndex)
    {
        Vector3 closestPoint = _referenceLinePoints[0];
        distance = float.MaxValue;
        nearestIndex = 0;

        for (int i = 0; i < _referenceLinePoints.Count; i++)
        {
            float dist = Vector3.Distance(position, _referenceLinePoints[i]);
            if (dist < distance)
            {
                distance = dist;
                closestPoint = _referenceLinePoints[i];
                nearestIndex = i;
            }
        }

        return closestPoint;
    }

    // Get progress along the line (0 to 1) for a given position
    public float GetProgressAlongLine(Vector3 position)
    {
        if (_referenceLinePoints.Count == 0) return 0f;

        GetClosestPointOnLine(position, out float distance, out int nearestIndex);
        return (float)nearestIndex / (_referenceLinePoints.Count - 1);
    }
}
