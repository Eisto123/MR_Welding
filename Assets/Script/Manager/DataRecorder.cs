using System.Collections.Generic;
using UnityEngine;

public struct WeldingMotionSample
{
    public Vector3 tipPosition;
    public Vector3 tipLocalRotation;
    public Vector3 travelDirection;
    public float currentTravelTime;
    public float travelDistance;
    public float speed;
    public bool isValid;
}

public class WeldingData
{
    public Vector3 tipPosition;
    public Vector3 tipLocalRotation;
    public float currentTravelTime;
    public Vector3 travelDirection;
    public float travelDistance;
    public float speed;
}

public class DataRecorder : MonoBehaviour
{
    public List<WeldingData> weldingDataList = new List<WeldingData>();

    [Header("Reference")]
    public Transform weldingTip;

    private bool isRecording = false;
    private float recordingTime = 0f;
    public float recordingInterval = 0.5f; // Record data every 0.5 seconds
    private float totalWeldingTime = 0f; // Accumulates only during recording
    private float totalTravelDistance = 0f;
    private Vector3 lastTipPosition;
    private Vector3 lastTravelDirection = Vector3.forward;
    private bool hasLastTipPosition = false;
    private WeldingMotionSample latestSample;

    public bool IsRecording => isRecording;

    public void StartRecording()
    {
        if (!isRecording)
        {
            hasLastTipPosition = false;
            recordingTime = 0f;
        }

        isRecording = true;
    }

    public void StopRecording()
    {
        isRecording = false;
    }

    void Update()
    {
        if (isRecording)
        {
            if (weldingTip == null) return;

            totalWeldingTime += Time.deltaTime; // Accumulate time only while recording
            recordingTime += Time.deltaTime;

            Vector3 currentPosition = weldingTip.position;
            Vector3 travelDelta = hasLastTipPosition ? currentPosition - lastTipPosition : Vector3.zero;
            float frameDistance = travelDelta.magnitude;
            float speed = Time.deltaTime > Mathf.Epsilon ? frameDistance / Time.deltaTime : 0f;

            if (frameDistance > Mathf.Epsilon)
            {
                lastTravelDirection = travelDelta / frameDistance;
                totalTravelDistance += frameDistance;
            }

            latestSample = new WeldingMotionSample
            {
                tipPosition = currentPosition,
                tipLocalRotation = weldingTip.localEulerAngles,
                travelDirection = lastTravelDirection,
                currentTravelTime = totalWeldingTime,
                travelDistance = totalTravelDistance,
                speed = speed,
                isValid = true
            };

            if (recordingTime >= recordingInterval)
            {
                recordingTime = 0f;
                WeldingData data = new WeldingData
                {
                    tipPosition = currentPosition,
                    tipLocalRotation = weldingTip.localEulerAngles,
                    currentTravelTime = totalWeldingTime,
                    travelDirection = lastTravelDirection,
                    travelDistance = totalTravelDistance,
                    speed = speed
                };
                weldingDataList.Add(data);
            }

            lastTipPosition = currentPosition;
            hasLastTipPosition = true;
        }
    }

    public List<WeldingData> GetWeldingData()
    {
        return weldingDataList;
    }

    public bool TryGetLatestSample(out WeldingMotionSample sample)
    {
        sample = latestSample;
        return latestSample.isValid;
    }
}
