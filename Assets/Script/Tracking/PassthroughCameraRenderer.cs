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
        // Step 1: Set up camera parameters for tracking
        // These intrinsic parameters are essential for accurate marker pose estimation
        var intrinsics = passthroughCameraAccess.Intrinsics;
        var cx = intrinsics.PrincipalPoint.x;  // Principal point X (optical center)
        var cy = intrinsics.PrincipalPoint.y;  // Principal point Y (optical center)
        var fx = intrinsics.FocalLength.x;     // Focal length X
        var fy = intrinsics.FocalLength.y;     // Focal length Y
        var width = intrinsics.SensorResolution.x;   // Image width
        var height = intrinsics.SensorResolution.y;  // Image height

        // Initialize the ArUco tracking with camera parameters
        m_arucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);

        // Step 2: Build marker dictionary from serialized list
        // This maps marker IDs to the GameObjects that should be positioned at each marker
        BuildMarkerDictionary();

        // Step 3: Set up texture for visualization
        //ConfigureResultTexture(width, height);
    }
    private void ProcessMarkerTracking()
    {
        // Step 1: Detect ArUco markers in the current camera frame
        //WebCamTexture webCamTexture = passthroughCameraAccess.GetTexture() as WebCamTexture;
        m_arucoMarkerTracking.DetectMarker(passthroughCameraAccess.GetTexture(), m_resultTexture);

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
