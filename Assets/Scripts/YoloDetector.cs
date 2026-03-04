#if !UNITY_WSA_10_0

using System;
using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.DnnModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using Rect = OpenCVForUnity.CoreModule.Rect;

/// <summary>
/// YOLO object detector - handles model loading, inference, and debug visualization
/// Optimized for VR performance - Synchronous version
/// </summary>
public class YoloDetector : MonoBehaviour
{
    // ===========================================
    // MODEL SETTINGS
    // ===========================================
    [Header("Model Settings")]
    [Tooltip("Path to YOLO ONNX model")]
    [SerializeField] private string modelPath = "OpenCVForUnityExamples/objdetect/best.onnx";
    
    [Tooltip("Path to classes file")]
    [SerializeField] private string classesPath = "OpenCVForUnityExamples/objdetect/iron_dataset.yaml";
    
    [Tooltip("Confidence threshold for detections")]
    [Range(0.1f, 1.0f)]
    [SerializeField] private float confidenceThreshold = 0.6f;
    
    [Tooltip("NMS IoU threshold")]
    [Range(0.1f, 1.0f)]
    [SerializeField] private float nmsThreshold = 0.3f;

    // ===========================================
    // PERFORMANCE
    // ===========================================
    [Tooltip("Skip visualization (draw detections) for better performance")]
    [SerializeField] private bool skipVisualization = false;

    [Header("Debug")]
    [Tooltip("Log detection details")]
    [SerializeField] private bool debugLogging = false;

    // ===========================================
    // PRIVATE FIELDS
    // ===========================================
    
    // OpenCV DNN
    private Net yoloNet;
    private List<string> classes;
    private Scalar[] colors;

    // OpenCV processing mats - reused to avoid allocations
    private Mat processingRgbaMat;
    private Mat processingBgrMat;
    
    // For texture conversion - reused
    private Texture2D tempTexture2D;

    // Detection results
    private List<Detection> latestDetections = new List<Detection>();

    // State
    private bool isModelLoaded = false;
    private bool isInitialized = false;

    // ===========================================
    // PROPERTIES
    // ===========================================
    
    public bool IsReady => isModelLoaded && isInitialized;
    public bool IsModelLoaded => isModelLoaded;
    public List<string> Classes => classes;
    public List<Detection> LatestDetections => new List<Detection>(latestDetections);

    // ===========================================
    // INITIALIZATION
    // ===========================================

    private void Start()
    {
        StartCoroutine(LoadYoloModel());
    }

    private System.Collections.IEnumerator LoadYoloModel()
    {
        Debug.Log("Loading YOLO model...");

        // Load model file
        var modelTask = OpenCVEnv.GetFilePathTaskAsync(modelPath);
        while (!modelTask.IsCompleted)
        {
            yield return null;
        }

        string modelFilepath = modelTask.Result;

        if (string.IsNullOrEmpty(modelFilepath))
        {
            Debug.LogError($"{modelPath} not found. Please add model to StreamingAssets.");
            yield break;
        }

        // Load classes file
        var classesTask = OpenCVEnv.GetFilePathTaskAsync(classesPath);
        while (!classesTask.IsCompleted)
        {
            yield return null;
        }

        string classesFilepath = classesTask.Result;

        try
        {
            // Load ONNX model
            yoloNet = Dnn.readNetFromONNX(modelFilepath);
            
            // Load classes
            classes = LoadClasses(classesFilepath);
            
            // Initialize colors for each class
            colors = new Scalar[classes.Count];
            for (int i = 0; i < classes.Count; i++)
            {
                colors[i] = new Scalar(
                    UnityEngine.Random.Range(0, 255),
                    UnityEngine.Random.Range(0, 255),
                    UnityEngine.Random.Range(0, 255),
                    255
                );
            }

            isModelLoaded = true;
            Debug.Log($"YOLO model loaded successfully with {classes.Count} classes");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load YOLO model: {e.Message}");
        }
    }

    /// <summary>
    /// Initialize processing with the given image dimensions
    /// </summary>
    public void Initialize(int width, int height)
    {
        if (isInitialized)
        {
            Debug.LogWarning("YoloDetector already initialized");
            return;
        }

        Debug.Log($"Initializing YOLO detector with resolution: {width}x{height}");

        // Initialize processing mats
        processingRgbaMat = new Mat(height, width, CvType.CV_8UC4);
        processingBgrMat = new Mat(height, width, CvType.CV_8UC3);

        isInitialized = true;
        Debug.Log("YOLO detector initialized successfully");
    }

    // ===========================================
    // MAIN UPDATE LOOP
    // ===========================================

    private void Update()
    {
        // No threading queue to process in sync version
    }

    // ===========================================
    // DETECTION PROCESSING - SYNCHRONOUS
    // ===========================================

    /// <summary>
    /// Detect objects in texture and optionally visualize on resultTexture
    /// Synchronous version - processes immediately
    /// </summary>
    public void DetectObjects(Texture cameraTexture, Texture2D resultTexture = null)
    {
        if (!IsReady || cameraTexture == null || yoloNet == null)
        {
            return;
        }

        // Fast path: Convert texture directly
        if (!ConvertTextureToMat(cameraTexture, processingRgbaMat))
            return;

        // Convert RGBA to BGR once
        Imgproc.cvtColor(processingRgbaMat, processingBgrMat, Imgproc.COLOR_RGBA2BGR);

        // Run YOLO detection synchronously
        var detections = Detect(processingBgrMat);
        latestDetections = detections;

        // Visualize if needed
        if (resultTexture != null && !skipVisualization && detections.Count > 0)
        {
            foreach (var detection in detections)
            {
                DrawDetection(detection, processingRgbaMat);
            }
        }

        // Update result texture
        if (resultTexture != null)
        {
            OpenCVMatUtils.MatToTexture2D(processingRgbaMat, resultTexture);
        }
    }

    /// <summary>
    /// Optimized texture to Mat conversion - reuses texture2D
    /// </summary>
    private bool ConvertTextureToMat(Texture cameraTexture, Mat outputMat)
    {
        Texture2D texture2D = cameraTexture as Texture2D;
        
        if (texture2D == null)
        {
            // Handle RenderTexture case - reuse temp texture
            if (tempTexture2D == null || 
                tempTexture2D.width != cameraTexture.width || 
                tempTexture2D.height != cameraTexture.height)
            {
                if (tempTexture2D != null)
                    Destroy(tempTexture2D);
                
                tempTexture2D = new Texture2D(cameraTexture.width, cameraTexture.height, 
                    TextureFormat.RGBA32, false);
            }

            RenderTexture currentRT = RenderTexture.active;
            RenderTexture tempRT = RenderTexture.GetTemporary(
                cameraTexture.width, cameraTexture.height, 0, RenderTextureFormat.ARGB32);

            Graphics.Blit(cameraTexture, tempRT);
            RenderTexture.active = tempRT;

            tempTexture2D.ReadPixels(new UnityEngine.Rect(0, 0, cameraTexture.width, cameraTexture.height), 0, 0);
            tempTexture2D.Apply();
            texture2D = tempTexture2D;

            RenderTexture.active = currentRT;
            RenderTexture.ReleaseTemporary(tempRT);
        }

        if (texture2D == null) return false;

        // Convert to OpenCV format
        OpenCVMatUtils.Texture2DToMat(texture2D, outputMat);
        return true;
    }

    // ===========================================
    // YOLO INFERENCE - SAME AS WORKING VERSION
    // ===========================================

    private List<Detection> Detect(Mat image)
    {
        int height = image.height();
        int width = image.width();
        int length = Math.Max(height, width);
        
        // Create square image for YOLO
        Mat squareImage = Mat.zeros(length, length, CvType.CV_8UC3);
        Mat roi = new Mat(squareImage, new Rect(0, 0, width, height));
        image.copyTo(roi);

        float scale = (float)length / 640;

        // Preprocess
        Mat blob = Dnn.blobFromImage(squareImage, 1.0 / 255.0, new Size(640, 640), new Scalar(0, 0, 0), true, false);
        yoloNet.setInput(blob);

        // Forward pass
        var outputs = new List<Mat>();
        yoloNet.forward(outputs, yoloNet.getUnconnectedOutLayersNames());

        // Process outputs (YOLOv8 format)
        Mat output = outputs[0];

        int numClasses = classes.Count;
        int dimensions = 4 + numClasses;

        var detections = new List<Detection>();

        // Transpose from [1, dimensions, numAnchors] to [numAnchors, dimensions]
        using (Mat output2D = output.reshape(1, dimensions))
        using (Mat outputTransposed = output2D.t())
        {
            for (int i = 0; i < outputTransposed.rows(); i++)
            {
                using (Mat row = outputTransposed.row(i))
                {
                    float[] data = new float[row.cols()];
                    row.get(0, 0, data);

                    // Extract box coordinates (center format, normalized to 640x640)
                    float cx = data[0];
                    float cy = data[1];
                    float w = data[2];
                    float h = data[3];

                    // Extract class scores
                    float maxClassScore = float.MinValue;
                    int classId = -1;

                    for (int j = 0; j < numClasses; j++)
                    {
                        float classScore = data[4 + j];
                        if (classScore > maxClassScore)
                        {
                            maxClassScore = classScore;
                            classId = j;
                        }
                    }

                    float confidence = maxClassScore;

                    if (confidence >= confidenceThreshold)
                    {
                        // Convert from center format to corner format
                        float x = cx - w / 2f;
                        float y = cy - h / 2f;

                        // Scale back to original image space
                        int scaledX = (int)(x * scale);
                        int scaledY = (int)(y * scale);
                        int scaledW = (int)(w * scale);
                        int scaledH = (int)(h * scale);
                        
                        // Calculate center in original image space
                        float centerXInOriginal = scaledX + scaledW / 2f;
                        float centerYInOriginal = scaledY + scaledH / 2f;
                        
                        // Clamp to original image dimensions
                        centerXInOriginal = Math.Max(0, Math.Min(centerXInOriginal, width));
                        centerYInOriginal = Math.Max(0, Math.Min(centerYInOriginal, height));

                        detections.Add(new Detection
                        {
                            ClassId = classId,
                            Confidence = confidence,
                            Box = new Rect(scaledX, scaledY, scaledW, scaledH),
                            CenterX = centerXInOriginal,
                            CenterY = centerYInOriginal
                        });
                    }
                }
            }
        }

        // Clean up - CRITICAL: Dispose of temporary Mats to prevent memory leaks
        squareImage.Dispose();
        roi.Dispose();
        blob.Dispose();
        foreach (var mat in outputs) mat.Dispose();

        // Apply NMS
        detections = NMS(detections, nmsThreshold);
        
        if (debugLogging && detections.Count > 0)
        {
            Debug.Log($"Detected {detections.Count} objects");
        }
        
        return detections;
    }

    // ===========================================
    // VISUALIZATION
    // ===========================================

    private void DrawDetection(Detection detection, Mat frame)
    {
        if (detection.ClassId < 0 || detection.ClassId >= colors.Length)
            return;

        Imgproc.rectangle(frame, detection.Box, colors[detection.ClassId], 2);
        string label = $"{classes[detection.ClassId]} ({detection.Confidence:F2})";
        Imgproc.putText(frame, label, new Point(detection.Box.x, detection.Box.y - 10),
            Imgproc.FONT_HERSHEY_SIMPLEX, 0.5, colors[detection.ClassId], 2);
    }

    // ===========================================
    // UTILITIES
    // ===========================================

    private List<string> LoadClasses(string path)
    {
        return new List<string> { "ButtShape", "TShape" };
    }

    private List<Detection> NMS(List<Detection> detections, float nmsThreshold)
    {
        detections.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var result = new List<Detection>();

        while (detections.Count > 0)
        {
            var best = detections[0];
            result.Add(best);
            detections.RemoveAt(0);

            detections.RemoveAll(d => IoU(best.Box, d.Box) > nmsThreshold);
        }

        return result;
    }

    private float IoU(Rect a, Rect b)
    {
        float intersection = Math.Max(0, Math.Min(a.x + a.width, b.x + b.width) - Math.Max(a.x, b.x)) *
                             Math.Max(0, Math.Min(a.y + a.height, b.y + b.height) - Math.Max(a.y, b.y));
        float union = a.width * a.height + b.width * b.height - intersection;
        return union > 0 ? intersection / union : 0;
    }

    // ===========================================
    // CLEANUP
    // ===========================================

    private void OnDestroy()
    {
        // Clean up OpenCV resources
        yoloNet?.Dispose();
        processingRgbaMat?.Dispose();
        processingBgrMat?.Dispose();
        
        if (tempTexture2D != null)
        {
            Destroy(tempTexture2D);
        }
    }

    // ===========================================
    // DATA STRUCTURES
    // ===========================================

    [System.Serializable]
    public struct Detection
    {
        public int ClassId;
        public float Confidence;
        public Rect Box;
        public float CenterX;
        public float CenterY;
    }
}

#endif
