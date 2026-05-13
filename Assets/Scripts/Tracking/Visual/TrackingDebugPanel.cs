using System;
using UnityEngine;
using UnityEngine.UI;

public class TrackingDebugPanel : MonoBehaviour
{
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;
    [SerializeField] private Text statusText;
    [SerializeField] private Image statusIndicator;
    [SerializeField] private bool showLastValidAge = true;

    private long lastValidTicksUtc = -1;

    private void Start()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponent<TrackingOrchestrator>();
    }

    private void OnEnable()
    {
        if (trackingOrchestrator != null)
            trackingOrchestrator.OnTrackingResultUpdated += HandleTrackingResult;
    }

    private void OnDisable()
    {
        if (trackingOrchestrator != null)
            trackingOrchestrator.OnTrackingResultUpdated -= HandleTrackingResult;
    }

    private void HandleTrackingResult(TrackingResult result)
    {
        if (result.PoseValid)
            lastValidTicksUtc = result.TimestampTicksUtc;

        if (statusText != null)
        {
            string confText = result.HasConfidence ? result.Confidence.ToString("F3") : "N/A";
            string ageText = "N/A";
            if (showLastValidAge && lastValidTicksUtc > 0)
            {
                double ageSec = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastValidTicksUtc).TotalSeconds;
                ageText = ageSec.ToString("F2") + "s";
            }

            statusText.text =
                $"State: {result.State}\n" +
                $"ProcessOk: {result.ProcessOk}\n" +
                $"PoseValid: {result.PoseValid}\n" +
                $"Confidence: {confText}\n" +
                $"Changed: {result.ChangedCount}/16\n" +
                $"LastValidAge: {ageText}";
        }

        if (statusIndicator != null)
            statusIndicator.color = GetStateColor(result.State);
    }

    private static Color GetStateColor(TrackingState state)
    {
        switch (state)
        {
            case TrackingState.Tracking:
                return new Color(0.2f, 0.9f, 0.2f);
            case TrackingState.Lost:
                return new Color(0.95f, 0.85f, 0.2f);
            case TrackingState.Error:
                return new Color(0.95f, 0.25f, 0.2f);
            default:
                return new Color(0.7f, 0.7f, 0.7f);
        }
    }
}
