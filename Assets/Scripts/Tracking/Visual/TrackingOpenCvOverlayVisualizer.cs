using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using Meta.XR;
using UnityEngine;
using UnityEngine.UI;
using CvRect = OpenCVForUnity.CoreModule.Rect;

public class TrackingOpenCvOverlayVisualizer : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;
    [SerializeField] private MonoBehaviour frameSourceBehaviour;
    [SerializeField] private YoloDetector yoloDetector;
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;
    [SerializeField] private RawImage outputPreview;

    [Header("Overlay")]
    [SerializeField] private string trackedLabel = "TrackedObject";
    [SerializeField] private bool preferAlignedPreviewFromFrameSource = true;
    [SerializeField] private bool drawYoloDetections = true;
    [Tooltip("Debug-only: run extra YOLO inference from this overlay. Leave off on Quest for smooth SRT3D tracking.")]
    [SerializeField] private bool runYoloForDebugOverlay = false;
    [Tooltip("If disabled, the overlay never runs YOLO while SRT3D is in Tracking state.")]
    [SerializeField] private bool runYoloWhileSrt3dTracking = false;
    [SerializeField] private int debugYoloEveryNFrames = 3;
    [SerializeField] private bool drawProjectedCenter = true;
    [SerializeField] private bool usePassthroughIntrinsicsForProjection = true;
    [SerializeField] private bool invertProjectedY = false;
    [SerializeField] private bool mirrorProjectedX = false;
    [SerializeField] private bool drawYFlipDebugCandidate = false;
    [SerializeField] private int centerRadiusPx = 6;
    [SerializeField] private float fxOverride = -1f;
    [SerializeField] private float fyOverride = -1f;
    [SerializeField] private float cxOverride = -1f;
    [SerializeField] private float cyOverride = -1f;

    private IFrameTextureProvider frameTextureProvider;
    private Mat overlayRgbaMat;
    private Texture2D overlayTexture;
    private Texture2D scratchReadableTexture;
    private Color32[] webCamColors;

    private void Start()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponent<TrackingOrchestrator>();

        if (frameSourceBehaviour == null)
            frameSourceBehaviour = FindFrameTextureProviderOnThisObject();
        if (yoloDetector == null)
            yoloDetector = FindObjectOfType<YoloDetector>();
        if (passthroughCameraAccess == null)
            passthroughCameraAccess = FindObjectOfType<PassthroughCameraAccess>();

        frameTextureProvider = frameSourceBehaviour as IFrameTextureProvider;
        if (frameTextureProvider == null)
            Debug.LogError("[TrackingOpenCvOverlayVisualizer] frameSourceBehaviour must implement IFrameTextureProvider.");
    }

    private MonoBehaviour FindFrameTextureProviderOnThisObject()
    {
        MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] is IFrameTextureProvider)
                return behaviours[i];
        }

        return GetComponent<WebCamFrameSource>();
    }

    private void Update()
    {
        if (trackingOrchestrator == null || frameTextureProvider == null || outputPreview == null)
            return;

        Texture previewTexture = null;
        if (preferAlignedPreviewFromFrameSource)
            frameTextureProvider.TryGetAlignedPreviewTexture(out previewTexture);
        if (previewTexture == null)
            frameTextureProvider.TryGetPreviewTexture(out previewTexture);
        if (previewTexture == null)
            return;

        if (!TryBuildOverlayMat(previewTexture))
            return;

        TrackingResult result = trackingOrchestrator.LastResult;
        if (runYoloForDebugOverlay)
            RunYoloForDebugOverlay(previewTexture, result);

        DrawStatusText(result);
        if (drawYoloDetections)
            DrawYoloDetections(result);

        if (drawProjectedCenter &&
            result.PoseValid &&
            result.IsConfirmed &&
            TryProjectTranslation(result, overlayRgbaMat.width(), overlayRgbaMat.height(), mirrorProjectedX, invertProjectedY, out Point center))
        {
            Imgproc.circle(overlayRgbaMat, center, Mathf.Max(2, centerRadiusPx), new Scalar(0, 255, 0, 255), -1, Imgproc.LINE_AA, 0);
            Imgproc.putText(
                overlayRgbaMat,
                BuildObjectLabel(result),
                new Point(center.x + 10, System.Math.Max(22.0, center.y - 10.0)),
                Imgproc.FONT_HERSHEY_SIMPLEX,
                0.62,
                new Scalar(0, 255, 0, 255),
                2,
                Imgproc.LINE_AA,
                false);

            if (drawYFlipDebugCandidate &&
                TryProjectTranslation(result, overlayRgbaMat.width(), overlayRgbaMat.height(), mirrorProjectedX, !invertProjectedY, out Point altCenter))
            {
                Imgproc.circle(overlayRgbaMat, altCenter, Mathf.Max(2, centerRadiusPx - 2), new Scalar(0, 220, 255, 255), 2, Imgproc.LINE_AA, 0);
                Imgproc.putText(
                    overlayRgbaMat,
                    "Y-flip candidate",
                    new Point(altCenter.x + 8, System.Math.Max(18.0, altCenter.y - 8.0)),
                    Imgproc.FONT_HERSHEY_SIMPLEX,
                    0.5,
                    new Scalar(0, 220, 255, 255),
                    1,
                    Imgproc.LINE_AA,
                    false);
            }
        }

        EnsureOutputTexture(overlayRgbaMat.width(), overlayRgbaMat.height());
        OpenCVMatUtils.MatToTexture2D(overlayRgbaMat, overlayTexture);
        if (outputPreview.texture != overlayTexture)
            outputPreview.texture = overlayTexture;

        AspectRatioFitter fitter = outputPreview.GetComponent<AspectRatioFitter>();
        if (fitter != null)
            fitter.aspectRatio = (float)overlayRgbaMat.width() / overlayRgbaMat.height();
    }

    private bool TryBuildOverlayMat(Texture previewTexture)
    {
        int width = previewTexture.width;
        int height = previewTexture.height;
        if (width <= 0 || height <= 0)
            return false;

        EnsureOverlayMat(width, height);
        if (overlayRgbaMat == null)
            return false;

        if (previewTexture is WebCamTexture webCamTexture)
        {
            EnsureWebCamColorBuffer(width, height);
            OpenCVMatUtils.WebCamTextureToMat(webCamTexture, overlayRgbaMat, webCamColors);
            return true;
        }

        Texture2D readableTexture = GetReadableTexture(previewTexture);
        if (readableTexture == null)
            return false;

        OpenCVMatUtils.Texture2DToMat(readableTexture, overlayRgbaMat);
        return true;
    }

    private void DrawStatusText(TrackingResult result)
    {
        string confText = result.HasConfidence ? result.Confidence.ToString("F2") : "N/A";
        Scalar stateColor = GetStateColor(result.State);
        string label = !string.IsNullOrEmpty(result.TrackedLabel) ? result.TrackedLabel : trackedLabel;
        Imgproc.putText(
            overlayRgbaMat,
            $"state={result.State} class={result.TrackedClassId} {label} conf={confText} valid={result.PoseValid}",
            new Point(12, 28),
            Imgproc.FONT_HERSHEY_SIMPLEX,
            0.65,
            stateColor,
            2,
            Imgproc.LINE_AA,
            false);
    }

    private void DrawYoloDetections(TrackingResult result)
    {
        if (yoloDetector == null)
            return;

        var detections = yoloDetector.LatestDetections;
        if (detections == null || detections.Count == 0)
            return;

        int width = overlayRgbaMat.width();
        int height = overlayRgbaMat.height();
        for (int i = 0; i < detections.Count; i++)
        {
            YoloDetector.Detection detection = detections[i];
            CvRect box = ClampRect(detection.Box, width, height);
            if (box.width <= 0 || box.height <= 0)
                continue;

            bool activeClass = detection.ClassId == result.TrackedClassId;
            Scalar color = GetYoloColor(detection.ClassId, activeClass);
            int thickness = activeClass ? 3 : 2;
            Imgproc.rectangle(overlayRgbaMat, box, color, thickness);

            string label = BuildYoloLabel(detection);
            double textY = System.Math.Max(22.0, box.y - 8.0);
            Imgproc.putText(
                overlayRgbaMat,
                label,
                new Point(box.x, textY),
                Imgproc.FONT_HERSHEY_SIMPLEX,
                0.58,
                color,
                2,
                Imgproc.LINE_AA,
                false);
        }
    }

    private void RunYoloForDebugOverlay(Texture previewTexture, TrackingResult result)
    {
        if (yoloDetector == null || previewTexture == null)
            return;

        bool srt3dHasTakenOver =
            result.State == TrackingState.Tracking ||
            result.PoseValid ||
            (result.TrackedClassId >= 0 && result.State != TrackingState.Lost);
        if (!runYoloWhileSrt3dTracking && srt3dHasTakenOver)
            return;

        int interval = Mathf.Max(1, debugYoloEveryNFrames);
        if (Time.frameCount % interval != 0)
            return;

        if (!yoloDetector.IsReady)
        {
            if (yoloDetector.IsModelLoaded)
                yoloDetector.Initialize(previewTexture.width, previewTexture.height);
            else
                return;
        }

        try
        {
            yoloDetector.DetectObjects(previewTexture, null);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[TrackingOpenCvOverlayVisualizer] Debug YOLO overlay failed: " + e.Message);
        }
    }

    private bool TryProjectTranslation(TrackingResult result, int width, int height, bool mirrorX, bool invertY, out Point uv)
    {
        uv = new Point();
        Vector3 t = result.TranslationRowMajor;
        if (t.z <= 1e-6f)
            return false;

        TryGetProjectionIntrinsics(width, height, out float cameraFx, out float cameraFy, out float cameraCx, out float cameraCy);
        float fx = fxOverride > 0f ? fxOverride : cameraFx;
        float fy = fyOverride > 0f ? fyOverride : cameraFy;
        float cx = cxOverride > 0f ? cxOverride : cameraCx;
        float cy = cyOverride > 0f ? cyOverride : cameraCy;

        float tx = mirrorX ? -t.x : t.x;
        float ty = invertY ? -t.y : t.y;
        double u = fx * (tx / t.z) + cx;
        double v = fy * (ty / t.z) + cy;
        if (double.IsNaN(u) || double.IsNaN(v) || double.IsInfinity(u) || double.IsInfinity(v))
            return false;

        uv = new Point(u, v);
        return true;
    }

    private string BuildObjectLabel(TrackingResult result)
    {
        string label = !string.IsNullOrEmpty(result.TrackedLabel) ? result.TrackedLabel : trackedLabel;
        if (!result.HasConfidence)
            return label;
        return $"{label}: {result.Confidence:F2}";
    }

    private string BuildYoloLabel(YoloDetector.Detection detection)
    {
        string label = $"class{detection.ClassId}";
        var classes = yoloDetector != null ? yoloDetector.Classes : null;
        if (classes != null && detection.ClassId >= 0 && detection.ClassId < classes.Count)
            label = classes[detection.ClassId];

        return $"YOLO {label}: {detection.Confidence:F2}";
    }

    private bool TryGetProjectionIntrinsics(int overlayWidth, int overlayHeight, out float fx, out float fy, out float cx, out float cy)
    {
        fx = Mathf.Max(overlayWidth, overlayHeight);
        fy = fx;
        cx = overlayWidth * 0.5f;
        cy = overlayHeight * 0.5f;

        if (!usePassthroughIntrinsicsForProjection || passthroughCameraAccess == null)
            return false;

        Vector2Int frameRes = passthroughCameraAccess.CurrentResolution;
        if (frameRes.x <= 0 || frameRes.y <= 0)
            return false;

        var intr = passthroughCameraAccess.Intrinsics;
        Vector2Int sensorRes = intr.SensorResolution;
        float cropX = (sensorRes.x - frameRes.x) * 0.5f;
        float cropY = (sensorRes.y - frameRes.y) * 0.5f;

        float scaleX = overlayWidth / Mathf.Max(1f, frameRes.x);
        float scaleY = overlayHeight / Mathf.Max(1f, frameRes.y);
        fx = intr.FocalLength.x * scaleX;
        fy = intr.FocalLength.y * scaleY;
        cx = (intr.PrincipalPoint.x - cropX) * scaleX;
        cy = (intr.PrincipalPoint.y - cropY) * scaleY;
        return true;
    }

    private static CvRect ClampRect(CvRect rect, int imageWidth, int imageHeight)
    {
        int x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, imageWidth - 1));
        int y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, imageHeight - 1));
        int right = Mathf.Clamp(rect.x + rect.width, x + 1, imageWidth);
        int bottom = Mathf.Clamp(rect.y + rect.height, y + 1, imageHeight);
        return new CvRect(x, y, right - x, bottom - y);
    }

    private static Scalar GetYoloColor(int classId, bool activeClass)
    {
        if (activeClass)
            return new Scalar(0, 255, 0, 255);

        switch (classId)
        {
            case 0:
                return new Scalar(255, 190, 40, 255);
            case 1:
                return new Scalar(60, 180, 255, 255);
            default:
                return new Scalar(255, 255, 255, 255);
        }
    }

    private static Scalar GetStateColor(TrackingState state)
    {
        switch (state)
        {
            case TrackingState.Tracking:
                return new Scalar(64, 255, 64, 255);
            case TrackingState.Lost:
                return new Scalar(255, 220, 64, 255);
            case TrackingState.Error:
                return new Scalar(255, 80, 80, 255);
            default:
                return new Scalar(220, 220, 220, 255);
        }
    }

    private void EnsureOverlayMat(int width, int height)
    {
        if (overlayRgbaMat != null && overlayRgbaMat.width() == width && overlayRgbaMat.height() == height)
            return;

        overlayRgbaMat?.Dispose();
        overlayRgbaMat = new Mat(height, width, CvType.CV_8UC4, new Scalar(0, 0, 0, 255));
    }

    private void EnsureOutputTexture(int width, int height)
    {
        if (overlayTexture != null && overlayTexture.width == width && overlayTexture.height == height)
            return;

        if (overlayTexture != null)
            Destroy(overlayTexture);
        overlayTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
    }

    private void EnsureWebCamColorBuffer(int width, int height)
    {
        int len = width * height;
        if (webCamColors == null || webCamColors.Length != len)
            webCamColors = new Color32[len];
    }

    private Texture2D GetReadableTexture(Texture sourceTexture)
    {
        Texture2D sourceTexture2D = sourceTexture as Texture2D;
        if (sourceTexture2D != null)
            return sourceTexture2D;

        if (scratchReadableTexture == null || scratchReadableTexture.width != sourceTexture.width || scratchReadableTexture.height != sourceTexture.height)
        {
            if (scratchReadableTexture != null)
                Destroy(scratchReadableTexture);
            scratchReadableTexture = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        }

        RenderTexture currentRT = RenderTexture.active;
        RenderTexture tempRT = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(sourceTexture, tempRT);
        RenderTexture.active = tempRT;
        scratchReadableTexture.ReadPixels(new UnityEngine.Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0);
        scratchReadableTexture.Apply(false, false);
        RenderTexture.active = currentRT;
        RenderTexture.ReleaseTemporary(tempRT);
        return scratchReadableTexture;
    }

    private void OnDestroy()
    {
        overlayRgbaMat?.Dispose();
        overlayRgbaMat = null;

        if (overlayTexture != null)
            Destroy(overlayTexture);
        overlayTexture = null;

        if (scratchReadableTexture != null)
            Destroy(scratchReadableTexture);
        scratchReadableTexture = null;

        webCamColors = null;
    }
}
