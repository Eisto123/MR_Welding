using UnityEngine;

public class TrackingPlatformRouter : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;

    [Header("Frame Sources")]
    [SerializeField] private MonoBehaviour windowsFrameSource;
    [SerializeField] private MonoBehaviour questFrameSource;

    [Header("Native Bridges")]
    [SerializeField] private MonoBehaviour windowsBridge;
    [SerializeField] private MonoBehaviour androidBridge;

    [Header("YOLO Seed Provider")]
    [SerializeField] private MonoBehaviour poseSeedProvider;

    [Header("Routing")]
    [SerializeField] private bool forceQuestInputInEditor = false;
    [SerializeField] private bool forceAndroidBridgeInEditor = false;

    private void Awake()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponent<TrackingOrchestrator>();
        if (trackingOrchestrator == null)
        {
            Debug.LogError("[TrackingPlatformRouter] TrackingOrchestrator is missing.");
            return;
        }

        bool useQuestInput = Application.platform == RuntimePlatform.Android || forceQuestInputInEditor;
        bool useAndroidBridge = Application.platform == RuntimePlatform.Android || forceAndroidBridgeInEditor;

        MonoBehaviour selectedSource = useQuestInput ? questFrameSource : windowsFrameSource;
        MonoBehaviour selectedBridge = useAndroidBridge ? androidBridge : windowsBridge;

        SetComponentEnabled(windowsFrameSource, selectedSource == windowsFrameSource);
        SetComponentEnabled(questFrameSource, selectedSource == questFrameSource);
        SetComponentEnabled(windowsBridge, selectedBridge == windowsBridge);
        SetComponentEnabled(androidBridge, selectedBridge == androidBridge);
        SetComponentEnabled(poseSeedProvider, poseSeedProvider != null);

        trackingOrchestrator.ConfigureDependencies(selectedSource, selectedBridge, poseSeedProvider);
        Debug.Log($"[TrackingPlatformRouter] source={(selectedSource != null ? selectedSource.GetType().Name : "null")}, " +
                  $"bridge={(selectedBridge != null ? selectedBridge.GetType().Name : "null")}, " +
                  $"seed={(poseSeedProvider != null ? poseSeedProvider.GetType().Name : "null")}");
    }

    private static void SetComponentEnabled(MonoBehaviour component, bool enabled)
    {
        if (component != null)
            component.enabled = enabled;
    }
}
