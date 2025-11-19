using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using OpenCVMarkerBasedAR;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

namespace MarkerBasedAR
{
    [RequireComponent(typeof(MultiSource2MatHelper))]
    public class MarkerTracking : MonoBehaviour
    {
        [Header("AR Camera")]
        public Camera ARCamera;

        [Header("Marker Settings")]
        public MarkerSettings[] markerSettings;

        [Header("Passthrough Camera Access")]
        [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;

        private Texture2D passthroughTexture;
        private Mat camMatrix;
        private MatOfDouble distCoeffs;
        private MarkerDetector markerDetector;
        private Matrix4x4 invertYM;
        private Matrix4x4 invertZM;

        private void Start()
        {
            // Initialize PassthroughCameraAccess
            if (passthroughCameraAccess == null)
            {
                Debug.LogError("PassthroughCameraAccess is not assigned.");
                enabled = false;
                return;
            }

            // Initialize marker detection
            InitializeMarkerDetection();
        }

        private void Update()
        {
            // Ensure the passthrough camera is playing
            if (!passthroughCameraAccess.IsPlaying)
            {
                Debug.LogWarning("Passthrough camera is not playing.");
                return;
            }

            // Get the passthrough camera texture
            passthroughTexture = passthroughCameraAccess.GetTexture() as Texture2D;

            if (passthroughTexture == null)
            {
                Debug.LogWarning("Failed to retrieve passthrough camera texture.");
                return;
            }

            // Convert Texture2D to OpenCV Mat
            Mat rgbaMat = new Mat(passthroughTexture.height, passthroughTexture.width, CvType.CV_8UC4);
            OpenCVMatUtils.Texture2DToMat(passthroughTexture, rgbaMat);

            // Process the Mat for marker detection
            ProcessMarkers(rgbaMat);

            // Release the Mat to avoid memory leaks
            rgbaMat.Dispose();
        }

        private void InitializeMarkerDetection()
        {
            // Get camera intrinsics from PassthroughCameraAccess
            var intrinsics = passthroughCameraAccess.Intrinsics;

            if (intrinsics.FocalLength == Vector2.zero)
            {
                Debug.LogError("Failed to retrieve camera intrinsics.");
                enabled = false;
                return;
            }

            // Set up camera matrix
            camMatrix = new Mat(3, 3, CvType.CV_64FC1);
            camMatrix.put(0, 0, intrinsics.FocalLength.x);
            camMatrix.put(0, 1, 0);
            camMatrix.put(0, 2, intrinsics.PrincipalPoint.x);
            camMatrix.put(1, 0, 0);
            camMatrix.put(1, 1, intrinsics.FocalLength.y);
            camMatrix.put(1, 2, intrinsics.PrincipalPoint.y);
            camMatrix.put(2, 0, 0);
            camMatrix.put(2, 1, 0);
            camMatrix.put(2, 2, 1.0f);

            Debug.Log("Camera Matrix: " + camMatrix.dump());

            // Set up distortion coefficients (assuming no distortion)
            distCoeffs = new MatOfDouble(0, 0, 0, 0);

            // Initialize marker detector
            MarkerDesign[] markerDesigns = new MarkerDesign[markerSettings.Length];
            for (int i = 0; i < markerDesigns.Length; i++)
            {
                markerDesigns[i] = markerSettings[i].markerDesign;
            }

            markerDetector = new MarkerDetector(camMatrix, distCoeffs, markerDesigns);

            // Set up coordinate system transformations
            invertYM = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, -1, 1));
            invertZM = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
        }

        private void ProcessMarkers(Mat rgbaMat)
        {
            // Detect markers in the frame
            markerDetector.processFrame(rgbaMat, 1);

            // Disable all AR objects initially
            foreach (MarkerSettings settings in markerSettings)
            {
                settings.setAllARGameObjectsDisable();
            }

            // Get detected markers
            List<Marker> findMarkers = markerDetector.getFindMarkers();

            foreach (Marker marker in findMarkers)
            {
                foreach (MarkerSettings settings in markerSettings)
                {
                    if (marker.id == settings.getMarkerId())
                    {
                        Matrix4x4 transformationM = marker.transformation;

                        // Convert OpenCV right-handed coordinates to Unity left-handed coordinates
                        Matrix4x4 ARM = invertYM * transformationM * invertYM;

                        // Apply Y-axis and Z-axis reflection matrix
                        ARM = ARM * invertYM * invertZM;

                        // Transform to world space
                        ARM = ARCamera.transform.localToWorldMatrix * ARM;

                        // Apply the transformation to the AR object
                        GameObject ARGameObject = settings.getARGameObject();
                        if (ARGameObject != null)
                        {
                            OpenCVARUtils.SetTransformFromMatrix(ARGameObject.transform, ref ARM);
                            ARGameObject.SetActive(true);
                        }
                    }
                }
            }
        }
    }
}
