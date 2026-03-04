using System.Collections.Generic;
using UnityEngine;

public class ReferenceLineManager : MonoBehaviour
{
    [Header("Predefined line points (in order)")]
    [SerializeField] private List<Transform> predefinedPointTransforms = new List<Transform>();

    [Header("Line Renderer")]
    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private bool lineFollowsParent = true;     // local-space rendering
    [SerializeField] private bool refreshPointsEveryFrame = true;

    [Header("Optional Debug Visualization")]
    [SerializeField] private bool drawGizmos = true;

    // Always kept in WORLD space for distance/progress calculations
    private readonly List<Vector3> _referenceLinePoints = new List<Vector3>();

    private void Awake()
    {
        RebuildReferenceLinePoints();
    }

    private void LateUpdate()
    {
        if (refreshPointsEveryFrame)
        {
            RebuildReferenceLinePoints();
        }
    }

    private void OnValidate()
    {
        RebuildReferenceLinePoints();
    }

    public void RebuildReferenceLinePoints()
    {
        _referenceLinePoints.Clear();

        for (int i = 0; i < predefinedPointTransforms.Count; i++)
        {
            if (predefinedPointTransforms[i] != null)
            {
                _referenceLinePoints.Add(predefinedPointTransforms[i].position); // world
            }
        }

        UpdateLineRenderer();
    }

    private void UpdateLineRenderer()
    {
        if (lineRenderer == null) return;

        lineRenderer.positionCount = _referenceLinePoints.Count;

        if (lineFollowsParent)
        {
            lineRenderer.useWorldSpace = false;

            Vector3[] localPoints = new Vector3[_referenceLinePoints.Count];
            for (int i = 0; i < _referenceLinePoints.Count; i++)
            {
                localPoints[i] = transform.InverseTransformPoint(_referenceLinePoints[i]);
            }
            lineRenderer.SetPositions(localPoints);
        }
        else
        {
            lineRenderer.useWorldSpace = true;
            lineRenderer.SetPositions(_referenceLinePoints.ToArray());
        }
    }

    public void SetLineRendererVisible(bool isVisible)
    {
        if (lineRenderer == null) return;
        lineRenderer.enabled = isVisible;
    }

    public void ToggleLineRenderer()
    {
        if (lineRenderer == null) return;
        lineRenderer.enabled = !lineRenderer.enabled;
    }

    public List<Vector3> GetReferenceLinePoints()
    {
        return new List<Vector3>(_referenceLinePoints);
    }

    public Vector3 GetClosestPointOnLine(Vector3 position, out float distance, out int segmentIndex)
    {
        if (_referenceLinePoints.Count == 0)
        {
            distance = 0f;
            segmentIndex = 0;
            return position;
        }

        if (_referenceLinePoints.Count == 1)
        {
            distance = Vector3.Distance(position, _referenceLinePoints[0]);
            segmentIndex = 0;
            return _referenceLinePoints[0];
        }

        Vector3 closestPoint = _referenceLinePoints[0];
        distance = float.MaxValue;
        segmentIndex = 0;

        for (int i = 0; i < _referenceLinePoints.Count - 1; i++)
        {
            Vector3 a = _referenceLinePoints[i];
            Vector3 b = _referenceLinePoints[i + 1];

            Vector3 ab = b - a;
            float abLengthSq = Vector3.Dot(ab, ab);
            if (abLengthSq <= Mathf.Epsilon) continue;

            float t = Vector3.Dot(position - a, ab) / abLengthSq;
            t = Mathf.Clamp01(t);

            Vector3 candidatePoint = a + t * ab;
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

    public float GetProgressAlongLine(Vector3 position)
    {
        if (_referenceLinePoints.Count < 2) return 0f;

        GetClosestPointOnLine(position, out _, out int segmentIndex);
        return (float)segmentIndex / (_referenceLinePoints.Count - 1);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos || _referenceLinePoints.Count == 0) return;

        Gizmos.color = Color.green;
        for (int i = 0; i < _referenceLinePoints.Count; i++)
        {
            Gizmos.DrawSphere(_referenceLinePoints[i], 0.005f);

            if (i < _referenceLinePoints.Count - 1)
            {
                Gizmos.DrawLine(_referenceLinePoints[i], _referenceLinePoints[i + 1]);
            }
        }
    }
}
