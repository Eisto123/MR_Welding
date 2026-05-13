using UnityEngine;

public class TrackingVisualizer : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;
    [SerializeField] private Transform trackedTarget;
    [SerializeField] private Transform poseReferenceTransform;
    [SerializeField] private TrackingSettings trackingSettings;

    [Header("Pose Settings")]
    [SerializeField] private bool poseIsInCameraSpace = true;
    [SerializeField] private bool flipCvYToUnity = true;
    [SerializeField] private float translationScale = 1f;
    [SerializeField, Range(0f, 1f)] private float poseSmoothing = 0.35f;

    [Header("Debug Drawing")]
    [SerializeField] private bool drawDebugAxes = true;
    [SerializeField] private float debugAxisLength = 0.08f;
    [SerializeField] private bool autoCreateDebugCube = true;
    [SerializeField] private bool grayWhenLost = true;

    private readonly PoseSmoother poseSmoother = new PoseSmoother();
    private Renderer targetRenderer;
    private static readonly Color TrackingColor = new Color(0.1f, 0.95f, 0.35f);
    private static readonly Color LostColor = new Color(0.5f, 0.5f, 0.5f);
    private static readonly Color ErrorColor = new Color(1f, 0.2f, 0.2f);

    private void Start()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponent<TrackingOrchestrator>();
        if (trackingSettings != null)
        {
            poseIsInCameraSpace = trackingSettings.PoseIsInCameraSpace;
            flipCvYToUnity = trackingSettings.FlipCvYToUnity;
            translationScale = trackingSettings.TranslationScale;
            poseSmoothing = trackingSettings.PoseSmoothing;
        }
        if (poseReferenceTransform == null && Camera.main != null)
            poseReferenceTransform = Camera.main.transform;
        if (trackedTarget == null && autoCreateDebugCube)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "TrackedPoseDebugCube";
            cube.transform.localScale = Vector3.one * 0.06f;
            trackedTarget = cube.transform;
        }

        if (trackedTarget != null)
            targetRenderer = trackedTarget.GetComponent<Renderer>();
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
        if (trackedTarget == null)
            return;

        if (result.PoseValid &&
            PoseConverter.TryBuildUnityPose(
                result.RowMajorPose16,
                flipCvYToUnity,
                translationScale,
                poseIsInCameraSpace,
                poseReferenceTransform,
                out Vector3 worldPos,
                out Quaternion worldRot))
        {
            poseSmoother.Evaluate(worldPos, worldRot, poseSmoothing, out Vector3 smoothPos, out Quaternion smoothRot);
            trackedTarget.SetPositionAndRotation(smoothPos, smoothRot);
        }

        UpdateTargetColor(result.State);
        if (drawDebugAxes)
            DrawDebugAxes(trackedTarget.position, trackedTarget.rotation, debugAxisLength);
    }

    private void UpdateTargetColor(TrackingState state)
    {
        if (targetRenderer == null)
            return;

        if (state == TrackingState.Tracking)
            targetRenderer.material.color = TrackingColor;
        else if (state == TrackingState.Error)
            targetRenderer.material.color = ErrorColor;
        else if (grayWhenLost)
            targetRenderer.material.color = LostColor;
    }

    private static void DrawDebugAxes(Vector3 p, Quaternion q, float axisLength)
    {
        Debug.DrawLine(p, p + q * Vector3.right * axisLength, Color.red, 0f, false);
        Debug.DrawLine(p, p + q * Vector3.up * axisLength, Color.green, 0f, false);
        Debug.DrawLine(p, p + q * Vector3.forward * axisLength, Color.blue, 0f, false);
    }
}
