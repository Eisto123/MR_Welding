using UnityEngine;

public class PoseSmoother
{
    private bool hasPose;
    private Vector3 smoothedPosition;
    private Quaternion smoothedRotation;

    public void Reset()
    {
        hasPose = false;
        smoothedPosition = Vector3.zero;
        smoothedRotation = Quaternion.identity;
    }

    public void Evaluate(Vector3 targetPosition, Quaternion targetRotation, float smoothing, out Vector3 position, out Quaternion rotation)
    {
        if (!hasPose || smoothing <= 0f || smoothing >= 1f)
        {
            smoothedPosition = targetPosition;
            smoothedRotation = targetRotation;
            hasPose = true;
            position = smoothedPosition;
            rotation = smoothedRotation;
            return;
        }

        smoothedPosition = Vector3.Lerp(smoothedPosition, targetPosition, smoothing);
        smoothedRotation = Quaternion.Slerp(smoothedRotation, targetRotation, smoothing);
        position = smoothedPosition;
        rotation = smoothedRotation;
    }
}
