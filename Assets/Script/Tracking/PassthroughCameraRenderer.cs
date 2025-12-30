using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using PassthroughCameraSamples;
using Meta.XR.MRUtilityKit;
using TryAR.MarkerTracking;
public class PassthroughCameraRenderer : MonoBehaviour
{
    [Serializable]
    public class MarkerGameObjectPair
    {
        /// <summary>
        /// The unique ID of the AR marker to track.
        /// </summary>
        public int markerId;

        /// <summary>
        /// The GameObject to associate with this marker.
        /// </summary>
        public GameObject gameObject;
    }

    public PassthroughCameraAccess passthroughCameraAccess;
    //public Renderer targetRenderer;
    public Transform cameraRigAnchor;
    
    [Header("Marker Tracking")]
    [SerializeField] private ArUcoMarkerTracking m_arucoMarkerTracking;
    [SerializeField, Tooltip("List of marker IDs mapped to their corresponding GameObjects")]
    private List<MarkerGameObjectPair> m_markerGameObjectPairs = new List<MarkerGameObjectPair>();
    private Dictionary<int, GameObject> m_markerGameObjectDictionary = new Dictionary<int, GameObject>();

    private Texture2D m_resultTexture;

    private IEnumerator Start()
    {
        yield return InitializeCamera();
        InitializeMarkerTracking();
    }

    private IEnumerator InitializeCamera()
    {
        passthroughCameraAccess.RequestedResolution = passthroughCameraAccess.Intrinsics.SensorResolution;
            passthroughCameraAccess.enabled = true;

            // Wait until the camera texture is available
            while(passthroughCameraAccess.GetTexture() == null)
            {
                yield return null;
            }
        }
    void Update()
    {
        // if (passthroughCameraAccess != null && targetRenderer != null)
        // {
        //     Texture passthroughTexture = passthroughCameraAccess.GetTexture();
        //     if (passthroughTexture != null)
        //     {
        //         targetRenderer.material.mainTexture = passthroughTexture;
        //     }
        // }
        UpdateCameraPoses();
        ProcessMarkerTracking();
    }
    private void InitializeMarkerTracking()
    {
        // Wait until camera is ready
        if (!passthroughCameraAccess.IsPlaying)
        {
            Debug.LogWarning("Camera not ready yet");
            return;
        }

        var intrinsics = passthroughCameraAccess.Intrinsics;
        
        // Get actual texture dimensions
        int actualWidth = passthroughCameraAccess.CurrentResolution.x;   // 1280
        int actualHeight = passthroughCameraAccess.CurrentResolution.y;  // 960
        int sensorWidth = intrinsics.SensorResolution.x;                 // 1280
        int sensorHeight = intrinsics.SensorResolution.y;                // 1280
        
        Debug.Log($"Sensor resolution: {sensorWidth}x{sensorHeight}");
        Debug.Log($"Actual texture size: {actualWidth}x{actualHeight}");
        
        // Calculate crop region (centered crop)
        float cropOffsetX = (sensorWidth - actualWidth) / 2.0f;   // 0
        float cropOffsetY = (sensorHeight - actualHeight) / 2.0f; // 160
        
        Debug.Log($"Crop offset: X={cropOffsetX}, Y={cropOffsetY}");
        
        // IMPORTANT: Adjust principal point for the crop offset
        // The principal point shifts when you crop the image
        float cx = intrinsics.PrincipalPoint.x - cropOffsetX;
        float cy = intrinsics.PrincipalPoint.y - cropOffsetY;
        
        // Focal lengths remain the same (no scaling needed since width is same)
        float fx = intrinsics.FocalLength.x;
        float fy = intrinsics.FocalLength.y;
        
        Debug.Log($"Original principal point: ({intrinsics.PrincipalPoint.x}, {intrinsics.PrincipalPoint.y})");
        Debug.Log($"Adjusted principal point: ({cx}, {cy})");
        Debug.Log($"Focal length: ({fx}, {fy})");
        
        // Initialize with ACTUAL dimensions and ADJUSTED parameters
        m_arucoMarkerTracking.Initialize(actualWidth, actualHeight, cx, cy, fx, fy);
        
        BuildMarkerDictionary();
    }
    private void ProcessMarkerTracking()
    {
        Texture cameraTexture = passthroughCameraAccess.GetTexture();
    
        if (cameraTexture == null)
        {
            Debug.LogWarning("Camera texture is null");
            return;
        }
        // Step 1: Detect ArUco markers in the current camera frame
        //WebCamTexture webCamTexture = passthroughCameraAccess.GetTexture() as WebCamTexture;
        m_arucoMarkerTracking.DetectMarker(cameraTexture, null);

        // Step 2: Estimate the pose of markers and position 3D objects accordingly
        // This maps the 2D marker positions to 3D space using the camera parameters
        m_arucoMarkerTracking.EstimatePoseCanonicalMarker(m_markerGameObjectDictionary, cameraRigAnchor);
    }


    /// <summary>
    /// Builds the dictionary mapping marker IDs to GameObjects.
    /// </summary>
    private void BuildMarkerDictionary()
    {
        m_markerGameObjectDictionary.Clear();
        foreach (var pair in m_markerGameObjectPairs)
        {
            if (pair.gameObject != null)
            {
                m_markerGameObjectDictionary[pair.markerId] = pair.gameObject;
            }
        }
    }

    // private void ConfigureResultTexture(int width, int height)
    // {
    //     int divideNumber = m_arucoMarkerTracking.DivideNumber;
    //     m_resultTexture = new Texture2D(width / divideNumber, height / divideNumber, TextureFormat.RGB24, false);
    //     m_resultRawImage.texture = m_resultTexture;
    // }

    private void UpdateCameraPoses()
        {
            // Get current head pose
            var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
            
            // Update camera anchor position and rotation
            var cameraPose = passthroughCameraAccess.GetCameraPose();
            cameraRigAnchor.position = cameraPose.position;
            cameraRigAnchor.rotation = cameraPose.rotation;
        }


}
