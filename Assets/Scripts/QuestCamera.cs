#if !UNITY_WSA_10_0

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Meta.XR;

public class QuestCamera : MonoBehaviour
{
    // ===========================================
    // COMPONENT REFERENCES
    // ===========================================
    [Header("Camera")]
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;
    
    [Header("Detection")]
    [SerializeField] private YoloDetector yoloDetector;
    
    [Header("Tracking")]
    [SerializeField] private List<ClassPrefabMapping> classPrefabMappings = new List<ClassPrefabMapping>();
    [SerializeField] private GameObject defaultDetectionPrefab; // Fallback for unmapped classes
    [SerializeField] private EnvironmentRaycastManager raycastManager;
    [SerializeField] private Transform cameraRigAnchor;

    [Header("Visualization")]
    [SerializeField] private RawImage resultPreview;

    // ===========================================
    // PERFORMANCE
    // ===========================================
    [Header("Performance")]
    [Tooltip("Process every N frames")]
    [SerializeField] private int scanFrequency = 1;

    [Header("Debug")]
    [Tooltip("Log detection details")]
    [SerializeField] private bool debugLogging = false;

    // ===========================================
    // CLASS PREFAB MAPPING
    // ===========================================
    [System.Serializable]
    public class ClassPrefabMapping
    {
        [Tooltip("Class index or name")]
        public string className;
        public int classId = -1; // -1 means use className for matching
        [Tooltip("Prefab to instantiate for this class")]
        public GameObject prefab;
    }

    // ===========================================
    // PRIVATE FIELDS
    // ===========================================
    
    // Tracked objects
    private Dictionary<int, GameObject> detectionObjects = new Dictionary<int, GameObject>();

    // Texture
    private Texture2D resultTexture;
    
    // State
    private bool isReady = false;
    private bool enableProcessingFrame = false;

    // AutoPlacement scan flow
    private bool autoPlacementScanActive = false;
    private bool pendingAutoPlacementStart = false;
    private bool hasFoundObjectThisScan = false;

    public event Action OnAutoPlacementObjectFound;
    public Transform LastAutoPlacementTarget { get; private set; }

    // Cache for class ID to prefab lookup
    private Dictionary<int, GameObject> classPrefabCache = new Dictionary<int, GameObject>();

    public bool IsReady => isReady;

    // ===========================================
    // INITIALIZATION
    // ===========================================

    private void Start()
    {
        StartCoroutine(Initialize());
    }

    private System.Collections.IEnumerator Initialize()
    {
        // Initialize camera
        yield return InitializeCamera();

        // Wait for YOLO detector to be ready
        if (yoloDetector == null)
        {
            Debug.LogError("YoloDetector reference is missing!");
            yield break;
        }

        // Wait only for MODEL to load, not full initialization
        while (!yoloDetector.IsModelLoaded)
        {
            Debug.Log("Waiting for YOLO model to load...");
            yield return null;
        }

        // Initialize YOLO detector with camera dimensions
        int width = passthroughCameraAccess.CurrentResolution.x;
        int height = passthroughCameraAccess.CurrentResolution.y;
        yoloDetector.Initialize(width, height);

        // Now it should be fully ready
        if (!yoloDetector.IsReady)
        {
            Debug.LogError("YOLO detector failed to initialize!");
            yield break;
        }

        // Build class prefab cache
        BuildClassPrefabCache();

        // Initialize result texture
        InitializeResultTexture(width, height);

        isReady = true;
        Debug.Log("Quest camera integration initialized successfully");

        // If AutoPlacement requested before init completed, start processing now
        if (pendingAutoPlacementStart || autoPlacementScanActive)
        {
            enableProcessingFrame = true;
            pendingAutoPlacementStart = false;
        }
    }

    private void BuildClassPrefabCache()
    {
        classPrefabCache.Clear();

        foreach (var mapping in classPrefabMappings)
        {
            if (mapping.prefab == null)
            {
                Debug.LogWarning($"Prefab not assigned for class mapping: {mapping.className}");
                continue;
            }

            // If classId is specified, use it directly
            if (mapping.classId >= 0)
            {
                classPrefabCache[mapping.classId] = mapping.prefab;
                Debug.Log($"Mapped class ID {mapping.classId} to prefab: {mapping.prefab.name}");
            }
            // Otherwise, find class ID by name
            else if (!string.IsNullOrEmpty(mapping.className))
            {
                int foundClassId = yoloDetector.Classes.FindIndex(c => 
                    c.Equals(mapping.className, System.StringComparison.OrdinalIgnoreCase));
                
                if (foundClassId >= 0)
                {
                    classPrefabCache[foundClassId] = mapping.prefab;
                    Debug.Log($"Mapped class '{mapping.className}' (ID {foundClassId}) to prefab: {mapping.prefab.name}");
                }
                else
                {
                    Debug.LogWarning($"Class name '{mapping.className}' not found in YOLO classes");
                }
            }
        }
    }

    private System.Collections.IEnumerator InitializeCamera()
    {
        passthroughCameraAccess.RequestedResolution = passthroughCameraAccess.Intrinsics.SensorResolution;
        passthroughCameraAccess.enabled = true;

        // Wait until the camera texture is available
        while (passthroughCameraAccess.GetTexture() == null)
        {
            yield return null;
        }

        Debug.Log("Camera initialized");
    }

    private void InitializeResultTexture(int width, int height)
    {
        // Create result texture for visualization
        resultTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        
        if (resultPreview != null)
        {
            resultPreview.texture = resultTexture;
            var aspectRatioFitter = resultPreview.GetComponent<AspectRatioFitter>();
            if (aspectRatioFitter != null)
            {
                aspectRatioFitter.aspectRatio = (float)width / height;
            }
        }
    }

    // ===========================================
    // MAIN UPDATE LOOP
    // ===========================================

    private void Update()
    {
        if(OVRInput.GetDown(OVRInput.Button.One))
        {
            enableProcessingFrame = !enableProcessingFrame;
            Debug.Log($"Toggled frame processing: {(enableProcessingFrame ? "ON" : "OFF")}");
        }

        if (!isReady || !yoloDetector.IsReady || !enableProcessingFrame)
        {
            return;
        }

        if (Time.frameCount % scanFrequency == 0)
        {
            ProcessFrame();

            if (autoPlacementScanActive && !hasFoundObjectThisScan && detectionObjects.Count > 0)
            {
                var tracked = GetFirstActiveTrackedObjectTransform();
                if (tracked != null)
                {
                    LastAutoPlacementTarget = tracked;
                }

                hasFoundObjectThisScan = true;
                StopAutoPlacementScan();
                OnAutoPlacementObjectFound?.Invoke();
            }
        }
    }

    private Transform GetFirstActiveTrackedObjectTransform()
    {
        foreach (var kvp in detectionObjects)
        {
            if (kvp.Value != null && kvp.Value.activeInHierarchy)
            {
                return kvp.Value.transform;
            }
        }
        return null;
    }

    public void StartAutoPlacementScan()
    {
        autoPlacementScanActive = true;
        hasFoundObjectThisScan = false;

        ClearDetectionObjects();

        if (isReady && yoloDetector != null && yoloDetector.IsReady)
        {
            enableProcessingFrame = true;
            pendingAutoPlacementStart = false;
        }
        else
        {
            pendingAutoPlacementStart = true;
        }

        Debug.Log("AutoPlacement scan started.");
    }

    public void StopAutoPlacementScan()
    {
        autoPlacementScanActive = false;
        pendingAutoPlacementStart = false;
        enableProcessingFrame = false;
        Debug.Log("AutoPlacement scan stopped.");
    }

    private void ClearDetectionObjects()
    {
        foreach (var kvp in detectionObjects)
        {
            if (kvp.Value != null)
            {
                Destroy(kvp.Value);
            }
        }
        detectionObjects.Clear();
    }

    // ===========================================
    // FRAME PROCESSING
    // ===========================================

    private void ProcessFrame()
    {
        UpdateCameraPoses();
        Texture cameraTexture = passthroughCameraAccess.GetTexture();
        if (cameraTexture == null)
        {
            return;
        }

        // Process frame through YOLO detector (this also updates resultTexture)
        yoloDetector.DetectObjects(cameraTexture, resultTexture);

        // Update tracked objects in 3D space
        UpdateTrackedObjects();
    }
    private void UpdateCameraPoses()
    {
        // Get current camera pose (with lens offset)
        var cameraPose = passthroughCameraAccess.GetCameraPose();
        cameraRigAnchor.position = cameraPose.position;
        cameraRigAnchor.rotation = cameraPose.rotation;
    }
    

    // ===========================================
    // 3D OBJECT TRACKING
    // ===========================================

    private void UpdateTrackedObjects()
    {
        List<YoloDetector.Detection> currentDetections = yoloDetector.LatestDetections;
        HashSet<int> currentlyTrackedIds = new HashSet<int>();

        // Get image dimensions for normalization
        int imageWidth = passthroughCameraAccess.CurrentResolution.x;
        int imageHeight = passthroughCameraAccess.CurrentResolution.y;

        // Update or create objects for each detection
        for (int i = 0; i < currentDetections.Count; i++)
        {
            YoloDetector.Detection detection = currentDetections[i];

            // Convert detection center to viewport coordinates (0-1)
            float normalizedX = detection.CenterX / imageWidth;
            float normalizedY = detection.CenterY / imageHeight;

            // Flip Y: Image space (0,0 = top-left) → Viewport space (0,0 = bottom-left)
            Vector2 viewportPoint = new Vector2(normalizedX, 1.0f - normalizedY);

            if (debugLogging)
            {
                Debug.Log($"Detection {i}: center=({detection.CenterX:F1},{detection.CenterY:F1}) " +
                          $"normalized=({normalizedX:F3},{normalizedY:F3}) " +
                          $"viewport=({viewportPoint.x:F3},{viewportPoint.y:F3})");
            }

            // Convert to world pose using raycast
            Pose worldPose = ConvertScreenPointToWorldPose(viewportPoint);

            // Only update if raycast was successful
            if (worldPose.position != Vector3.zero)
            {
                int objectId = i; // Simplified tracking by index
                currentlyTrackedIds.Add(objectId);

                // Get or create detection object
                if (!detectionObjects.TryGetValue(objectId, out GameObject detectionObject))
                {
                    detectionObject = CreateDetectionObject(objectId, detection.ClassId);
                    detectionObjects[objectId] = detectionObject;
                }

                // Update position and rotation
                detectionObject.transform.SetPositionAndRotation(worldPose.position, worldPose.rotation);
                detectionObject.SetActive(true);
                
                if (debugLogging)
                {
                    string className = detection.ClassId < yoloDetector.Classes.Count 
                        ? yoloDetector.Classes[detection.ClassId] 
                        : "Unknown";
                    Debug.Log($"Placed {className} at world position: {worldPose.position}");
                }
            }
            else if (debugLogging)
            {
                Debug.Log($"Raycast failed for detection {i} at viewport {viewportPoint}");
            }
        }

        // Hide or remove objects that are no longer detected
        List<int> idsToRemove = new List<int>();
        foreach (var kvp in detectionObjects)
        {
            if (!currentlyTrackedIds.Contains(kvp.Key))
            {
                kvp.Value.SetActive(false);
                idsToRemove.Add(kvp.Key);
            }
        }

        // Clean up old objects
        foreach (int id in idsToRemove)
        {
            if (detectionObjects.TryGetValue(id, out GameObject obj))
            {
                Destroy(obj);
                detectionObjects.Remove(id);
            }
        }
    }

    // ===========================================
    // RAYCASTING
    // ===========================================

    private Pose ConvertScreenPointToWorldPose(Vector2 viewportPoint)
    {
        // Get current camera pose
        Pose cameraPose = passthroughCameraAccess.GetCameraPose();

        // Create ray from viewport point
        Ray ray = passthroughCameraAccess.ViewportPointToRay(viewportPoint, cameraPose);

        // Raycast to find world position
        if (raycastManager != null && raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, maxDistance: 100f))
        {
            if (hit.status == EnvironmentRaycastHitStatus.Hit)
            {
                Quaternion fullRotation = Quaternion.LookRotation(hit.normal);
                float zAngle = fullRotation.eulerAngles.z;

                // Create pose at hit point, with only Z-axis rotation
                return new Pose
                {
                    position = hit.point,
                    rotation = Quaternion.Euler(0, 0, zAngle)
                };
            }
        }

        // Return identity pose if raycast failed
        return Pose.identity;
    }

    // ===========================================
    // OBJECT CREATION
    // ===========================================

    private GameObject CreateDetectionObject(int objectId, int classId)
    {
        GameObject detectionObject;

        string className = classId < yoloDetector.Classes.Count 
            ? yoloDetector.Classes[classId] 
            : "Unknown";

        // Get the appropriate prefab for this class
        GameObject prefabToUse = GetPrefabForClass(classId);

        if (prefabToUse != null)
        {
            detectionObject = Instantiate(prefabToUse);
            detectionObject.name = $"Detection_{className}_{objectId}";
            
            if (debugLogging)
            {
                Debug.Log($"Created {className} using prefab: {prefabToUse.name}");
            }
        }
        else
        {
            // Fallback: Create a simple cube
            detectionObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            detectionObject.name = $"Detection_{className}_{objectId}";
            detectionObject.transform.localScale = Vector3.one * 0.1f;
            
            // Random color
            var renderer = detectionObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = UnityEngine.Random.ColorHSV();
            }
            
            if (debugLogging)
            {
                Debug.Log($"Created {className} using fallback cube (no prefab mapped)");
            }
        }

        return detectionObject;
    }

    private GameObject GetPrefabForClass(int classId)
    {
        // Check if we have a specific prefab for this class
        if (classPrefabCache.TryGetValue(classId, out GameObject prefab))
        {
            return prefab;
        }

        // Return default prefab as fallback
        return defaultDetectionPrefab;
    }

    // ===========================================
    // CLEANUP
    // ===========================================

    private void OnDestroy()
    {
        // Clean up detection objects
        foreach (var kvp in detectionObjects)
        {
            if (kvp.Value != null)
                Destroy(kvp.Value);
        }
        detectionObjects.Clear();

        if (resultTexture != null)
        {
            Destroy(resultTexture);
        }
    }
}

#endif
