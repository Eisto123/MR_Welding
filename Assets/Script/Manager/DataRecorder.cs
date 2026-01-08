using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

public class WeldingData
{
    public Vector3 tipPosition;
    public Vector3 tipLocalRotation;
    public float currentTravelTime;

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

    public void StartRecording()
    {
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
            totalWeldingTime += Time.deltaTime; // Accumulate time only while recording
            recordingTime += Time.deltaTime;
            if (recordingTime >= recordingInterval)
            {
                recordingTime = 0f;
                WeldingData data = new WeldingData
                {
                    tipPosition = weldingTip.position,
                    tipLocalRotation = weldingTip.localEulerAngles,
                    currentTravelTime = totalWeldingTime
                };
                weldingDataList.Add(data);
            }
        }
    }

    public List<WeldingData> GetWeldingData()
    {
        return weldingDataList;
    }
}
