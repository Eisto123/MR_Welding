using System;
using UnityEngine;
using UnityEngine.UI;

public class WebCamFrameSource : MonoBehaviour, ICameraFrameSource, IFrameTextureProvider
{
    [Header("Webcam")]
    [SerializeField] private int requestedWidth = 1280;
    [SerializeField] private int requestedHeight = 720;
    [SerializeField] private int requestedFps = 30;
    [SerializeField] private int cameraIndex = 0;
    [SerializeField] private RawImage previewTarget;

    [Header("Orientation")]
    [SerializeField] private bool applyWebcamRotation = true;
    [SerializeField] private bool applyWebcamVerticalMirror = true;
    [SerializeField] private bool logOrientationOnce = true;

    [Header("Resolution Stability")]
    [Tooltip("Require this many consecutive frames at the same resolution before sending to tracker. Prevents DLL reset on early low-res frames.")]
    [SerializeField] private int requiredStableFrames = 5;

    private WebCamTexture webcamTexture;
    private byte[] rgbBuffer;
    private Texture2D alignedPreviewTexture;
    private Color32[] alignedPreviewColors;
    private bool didLogOrientation;
    private int lastStableWidth = -1;
    private int lastStableHeight = -1;
    private int stableFrameCount = 0;

    public bool IsReady => webcamTexture != null && webcamTexture.isPlaying;
    public int CurrentWidth => webcamTexture != null ? webcamTexture.width : 0;
    public int CurrentHeight => webcamTexture != null ? webcamTexture.height : 0;

    public void StartSource()
    {
        if (webcamTexture != null && webcamTexture.isPlaying)
            return;

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("[WebCamFrameSource] No webcam found.");
            return;
        }

        cameraIndex = Mathf.Clamp(cameraIndex, 0, devices.Length - 1);
        string deviceName = devices[cameraIndex].name;
        webcamTexture = new WebCamTexture(deviceName, requestedWidth, requestedHeight, requestedFps);
        webcamTexture.Play();

        if (previewTarget != null)
            previewTarget.texture = webcamTexture;

        Debug.Log($"[WebCamFrameSource] Webcam start index={cameraIndex}, name={deviceName}, requested={requestedWidth}x{requestedHeight}@{requestedFps}");
    }

    public void StopSource()
    {
        if (webcamTexture == null)
            return;

        if (webcamTexture.isPlaying)
            webcamTexture.Stop();

        webcamTexture = null;
        rgbBuffer = null;
        alignedPreviewColors = null;
        if (alignedPreviewTexture != null)
        {
            Destroy(alignedPreviewTexture);
            alignedPreviewTexture = null;
        }
        didLogOrientation = false;
        lastStableWidth = -1;
        lastStableHeight = -1;
        stableFrameCount = 0;
    }

    public bool TryGetFrame(out FramePacket framePacket)
    {
        framePacket = default;
        if (webcamTexture == null || !webcamTexture.isPlaying || !webcamTexture.didUpdateThisFrame)
            return false;

        int width = webcamTexture.width;
        int height = webcamTexture.height;
        if (width < 32 || height < 32)
            return false;

        if (width != lastStableWidth || height != lastStableHeight)
        {
            Debug.Log($"[WebCamFrameSource] Resolution changed: {lastStableWidth}x{lastStableHeight} -> {width}x{height}, waiting for stability...");
            lastStableWidth = width;
            lastStableHeight = height;
            stableFrameCount = 0;
            return false;
        }

        stableFrameCount++;
        if (stableFrameCount < requiredStableFrames)
            return false;

        int rotation = webcamTexture.videoRotationAngle;
        bool mirrorV = webcamTexture.videoVerticallyMirrored;
        if (logOrientationOnce && !didLogOrientation)
        {
            Debug.Log($"[WebCamFrameSource] Resolution stable at {width}x{height}, rotation={rotation} mirrorV={mirrorV}");
            didLogOrientation = true;
        }

        Color32[] pixels = webcamTexture.GetPixels32();
        int outWidth = width;
        int outHeight = height;
        byte[] outRgb;
        if (applyWebcamRotation || applyWebcamVerticalMirror)
        {
            BuildRgbBufferWithOrientation(
                pixels,
                width,
                height,
                applyWebcamRotation ? rotation : 0,
                applyWebcamVerticalMirror ? mirrorV : false,
                out outRgb,
                out outWidth,
                out outHeight);
        }
        else
        {
            int pixelCount = width * height;
            int expectedLength = pixelCount * 3;
            if (rgbBuffer == null || rgbBuffer.Length != expectedLength)
                rgbBuffer = new byte[expectedLength];

            for (int i = 0; i < pixelCount; i++)
            {
                Color32 c = pixels[i];
                int j = i * 3;
                rgbBuffer[j] = c.r;
                rgbBuffer[j + 1] = c.g;
                rgbBuffer[j + 2] = c.b;
            }
            outRgb = rgbBuffer;
        }

        framePacket = new FramePacket
        {
            Rgb24 = outRgb,
            Width = outWidth,
            Height = outHeight,
            TimestampTicksUtc = DateTime.UtcNow.Ticks,
            RotationDegrees = rotation,
            IsVerticallyMirrored = mirrorV
        };
        UpdateAlignedPreviewTexture(outRgb, outWidth, outHeight);
        return true;
    }

    public bool TryGetPreviewTexture(out Texture texture)
    {
        texture = webcamTexture;
        return webcamTexture != null && webcamTexture.isPlaying;
    }

    public bool TryGetAlignedPreviewTexture(out Texture texture)
    {
        texture = alignedPreviewTexture;
        return alignedPreviewTexture != null;
    }

    private void OnDisable()
    {
        StopSource();
    }

    private void BuildRgbBufferWithOrientation(
        Color32[] srcPixels,
        int srcWidth,
        int srcHeight,
        int rotation,
        bool mirrorVertical,
        out byte[] outRgb,
        out int outWidth,
        out int outHeight)
    {
        int rot = ((rotation % 360) + 360) % 360;
        if (rot == 90 || rot == 270)
        {
            outWidth = srcHeight;
            outHeight = srcWidth;
        }
        else
        {
            outWidth = srcWidth;
            outHeight = srcHeight;
        }

        int outPixelCount = outWidth * outHeight;
        int outLen = outPixelCount * 3;
        if (rgbBuffer == null || rgbBuffer.Length != outLen)
            rgbBuffer = new byte[outLen];

        for (int y = 0; y < srcHeight; y++)
        {
            int sy = mirrorVertical ? (srcHeight - 1 - y) : y;
            for (int x = 0; x < srcWidth; x++)
            {
                int sx = x;
                int dx;
                int dy;
                switch (rot)
                {
                    case 90:
                        dx = srcHeight - 1 - sy;
                        dy = sx;
                        break;
                    case 180:
                        dx = srcWidth - 1 - sx;
                        dy = srcHeight - 1 - sy;
                        break;
                    case 270:
                        dx = sy;
                        dy = srcWidth - 1 - sx;
                        break;
                    default:
                        dx = sx;
                        dy = sy;
                        break;
                }

                int srcIndex = y * srcWidth + x;
                int dstIndex = dy * outWidth + dx;
                Color32 c = srcPixels[srcIndex];
                int j = dstIndex * 3;
                rgbBuffer[j] = c.r;
                rgbBuffer[j + 1] = c.g;
                rgbBuffer[j + 2] = c.b;
            }
        }

        outRgb = rgbBuffer;
    }

    private void UpdateAlignedPreviewTexture(byte[] rgb24, int width, int height)
    {
        if (rgb24 == null || width <= 0 || height <= 0)
            return;

        if (alignedPreviewTexture == null || alignedPreviewTexture.width != width || alignedPreviewTexture.height != height)
        {
            if (alignedPreviewTexture != null)
                Destroy(alignedPreviewTexture);
            alignedPreviewTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            alignedPreviewColors = null;
        }

        int pixelCount = width * height;
        if (alignedPreviewColors == null || alignedPreviewColors.Length != pixelCount)
            alignedPreviewColors = new Color32[pixelCount];

        for (int i = 0; i < pixelCount; i++)
        {
            int j = i * 3;
            alignedPreviewColors[i] = new Color32(rgb24[j], rgb24[j + 1], rgb24[j + 2], 255);
        }

        alignedPreviewTexture.SetPixels32(alignedPreviewColors);
        alignedPreviewTexture.Apply(false, false);
    }
}
