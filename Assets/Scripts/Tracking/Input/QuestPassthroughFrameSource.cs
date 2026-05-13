#if !UNITY_WSA_10_0
using System;
using Meta.XR;
using Unity.Collections;
using UnityEngine;

public class QuestPassthroughFrameSource : MonoBehaviour, ICameraFrameSource, IFrameTextureProvider
{
    private enum FrameReadbackMode
    {
        PassthroughAccessGetColors,
        ReadPixelsFallback
    }

    private enum NativeInputOrientationCorrection
    {
        None,
        FlipVertical,
        FlipHorizontal,
        Rotate180
    }

    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;
    [SerializeField] private bool enableComponentOnStart = true;

    [Header("Latency")]
    [Tooltip("Skip Unity frames where the passthrough camera did not publish a new image. This avoids re-processing stale camera frames.")]
    [SerializeField] private bool onlyProcessUpdatedCameraFrames = true;
    [Tooltip("Use PassthroughCameraAccess.GetColors(), which uses AsyncGPUReadback internally. Falls back to ReadPixels if it fails.")]
    [SerializeField] private FrameReadbackMode readbackMode = FrameReadbackMode.PassthroughAccessGetColors;
    [Tooltip("Orientation correction applied only to the RGB frame sent into native SRT3D. YOLO/seed still use the original passthrough texture. Use this when yellow/cyan initial pose is correct but SRT3D appears to optimize against an upside-down or mirrored image.")]
    [SerializeField] private NativeInputOrientationCorrection nativeInputOrientationCorrection = NativeInputOrientationCorrection.None;
    [Tooltip("Cache the physical passthrough camera world pose at the camera image timestamp and pass it to the wireframe overlay.")]
    [SerializeField] private bool includeFrameCameraPose = true;

    [Header("Diagnostics")]
    [SerializeField] private bool logReadbackTimings = false;
    [SerializeField] private int timingLogEveryNFrames = 60;

    private Texture2D frameTexture2D;
    private RenderTexture blitReadbackTexture;
    private byte[] rgbBuffer;
    private bool isReady;
    private int processedFrameCounter;
    private bool loggedOrientationCorrection;

    public bool IsReady => isReady && passthroughCameraAccess != null && passthroughCameraAccess.GetTexture() != null;
    public int CurrentWidth => passthroughCameraAccess != null ? passthroughCameraAccess.CurrentResolution.x : 0;
    public int CurrentHeight => passthroughCameraAccess != null ? passthroughCameraAccess.CurrentResolution.y : 0;

    public void StartSource()
    {
        if (passthroughCameraAccess == null)
        {
            Debug.LogError("[QuestPassthroughFrameSource] PassthroughCameraAccess is not assigned.");
            return;
        }

        if (enableComponentOnStart)
        {
            passthroughCameraAccess.enabled = true;
        }

        isReady = true;
    }

    public void StopSource()
    {
        isReady = false;
        rgbBuffer = null;
        if (frameTexture2D != null)
        {
            Destroy(frameTexture2D);
            frameTexture2D = null;
        }
        if (blitReadbackTexture != null)
        {
            blitReadbackTexture.Release();
            Destroy(blitReadbackTexture);
            blitReadbackTexture = null;
        }
    }

    public bool TryGetFrame(out FramePacket framePacket)
    {
        framePacket = default;
        if (!IsReady)
            return false;

        if (onlyProcessUpdatedCameraFrames && !passthroughCameraAccess.IsUpdatedThisFrame)
            return false;

        var stopwatch = logReadbackTimings ? System.Diagnostics.Stopwatch.StartNew() : null;
        int width = passthroughCameraAccess.CurrentResolution.x;
        int height = passthroughCameraAccess.CurrentResolution.y;
        bool copied = false;

        if (readbackMode == FrameReadbackMode.PassthroughAccessGetColors)
            copied = TryCopyFromPassthroughColors(width, height);

        Texture sourceTexture = passthroughCameraAccess.GetTexture();
        if (!copied && !TryCopyFromTexture(sourceTexture, out width, out height))
            return false;

        ApplyNativeInputOrientationCorrection(width, height);

        bool hasPoseReference = false;
        Pose cameraPose = default;
        if (includeFrameCameraPose && passthroughCameraAccess.IsPlaying)
            hasPoseReference = TryGetFrameCameraPose(out cameraPose);

        framePacket = new FramePacket
        {
            Rgb24 = rgbBuffer,
            Width = width,
            Height = height,
            TimestampTicksUtc = passthroughCameraAccess.Timestamp != default
                ? passthroughCameraAccess.Timestamp.Ticks
                : DateTime.UtcNow.Ticks,
            RotationDegrees = 0,
            IsVerticallyMirrored =
                nativeInputOrientationCorrection == NativeInputOrientationCorrection.FlipVertical ||
                nativeInputOrientationCorrection == NativeInputOrientationCorrection.Rotate180,
            HasPoseReference = hasPoseReference,
            PoseReferencePosition = hasPoseReference ? cameraPose.position : Vector3.zero,
            PoseReferenceRotation = hasPoseReference ? cameraPose.rotation : Quaternion.identity
        };

        processedFrameCounter++;
        if (stopwatch != null &&
            timingLogEveryNFrames > 0 &&
            processedFrameCounter % timingLogEveryNFrames == 0)
        {
            stopwatch.Stop();
            Debug.Log($"[QuestPassthroughFrameSource] frame={processedFrameCounter}, " +
                      $"readbackMode={readbackMode}, readback+copy={stopwatch.Elapsed.TotalMilliseconds:F2}ms, " +
                      $"frameSize={width}x{height}, orientationCorrection={nativeInputOrientationCorrection}, " +
                      $"hasPoseRef={hasPoseReference}");
        }

        return true;
    }

    public bool TryGetPreviewTexture(out Texture texture)
    {
        texture = passthroughCameraAccess != null ? passthroughCameraAccess.GetTexture() : null;
        return texture != null;
    }

    public bool TryGetAlignedPreviewTexture(out Texture texture)
    {
        // Quest path currently does not apply extra orientation transform in frame source.
        texture = passthroughCameraAccess != null ? passthroughCameraAccess.GetTexture() : null;
        return texture != null;
    }

    private bool TryCopyFromPassthroughColors(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;

        NativeArray<Color32> pixels = passthroughCameraAccess.GetColors();
        int pixelCount = width * height;
        if (!pixels.IsCreated || pixels.Length < pixelCount)
            return false;

        CopyColorsToRgb(pixels, pixelCount);
        return true;
    }

    private bool TryCopyFromTexture(Texture sourceTexture, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (sourceTexture == null)
            return false;

        Texture2D readableTexture = GetReadableTexture(sourceTexture);
        if (readableTexture == null)
            return false;

        width = readableTexture.width;
        height = readableTexture.height;
        int pixelCount = width * height;
        NativeArray<Color32> pixels = readableTexture.GetPixelData<Color32>(0);
        if (!pixels.IsCreated || pixels.Length < pixelCount)
            return false;

        CopyColorsToRgb(pixels, pixelCount);
        return true;
    }

    private void CopyColorsToRgb(NativeArray<Color32> pixels, int pixelCount)
    {
        int expectedLen = pixelCount * 3;
        if (rgbBuffer == null || rgbBuffer.Length != expectedLen)
            rgbBuffer = new byte[expectedLen];

        for (int i = 0; i < pixelCount; i++)
        {
            Color32 c = pixels[i];
            int j = i * 3;
            rgbBuffer[j] = c.r;
            rgbBuffer[j + 1] = c.g;
            rgbBuffer[j + 2] = c.b;
        }
    }

    private void ApplyNativeInputOrientationCorrection(int width, int height)
    {
        if (nativeInputOrientationCorrection == NativeInputOrientationCorrection.None ||
            rgbBuffer == null ||
            width <= 0 ||
            height <= 0)
        {
            return;
        }

        if (!loggedOrientationCorrection)
        {
            Debug.Log($"[QuestPassthroughFrameSource] Applying native SRT3D input orientationCorrection={nativeInputOrientationCorrection}, frameSize={width}x{height}");
            loggedOrientationCorrection = true;
        }

        switch (nativeInputOrientationCorrection)
        {
            case NativeInputOrientationCorrection.FlipVertical:
                FlipVertical(width, height);
                break;
            case NativeInputOrientationCorrection.FlipHorizontal:
                FlipHorizontal(width, height);
                break;
            case NativeInputOrientationCorrection.Rotate180:
                Rotate180(width, height);
                break;
        }
    }

    private void FlipVertical(int width, int height)
    {
        int rowStride = width * 3;
        int halfRows = height / 2;
        for (int y = 0; y < halfRows; y++)
        {
            int top = y * rowStride;
            int bottom = (height - 1 - y) * rowStride;
            for (int x = 0; x < rowStride; x++)
            {
                byte tmp = rgbBuffer[top + x];
                rgbBuffer[top + x] = rgbBuffer[bottom + x];
                rgbBuffer[bottom + x] = tmp;
            }
        }
    }

    private void FlipHorizontal(int width, int height)
    {
        int rowStride = width * 3;
        int halfCols = width / 2;
        for (int y = 0; y < height; y++)
        {
            int row = y * rowStride;
            for (int x = 0; x < halfCols; x++)
            {
                SwapRgb(row + x * 3, row + (width - 1 - x) * 3);
            }
        }
    }

    private void Rotate180(int width, int height)
    {
        int pixelCount = width * height;
        int halfPixels = pixelCount / 2;
        for (int i = 0; i < halfPixels; i++)
        {
            SwapRgb(i * 3, (pixelCount - 1 - i) * 3);
        }
    }

    private void SwapRgb(int a, int b)
    {
        byte tmp = rgbBuffer[a];
        rgbBuffer[a] = rgbBuffer[b];
        rgbBuffer[b] = tmp;

        tmp = rgbBuffer[a + 1];
        rgbBuffer[a + 1] = rgbBuffer[b + 1];
        rgbBuffer[b + 1] = tmp;

        tmp = rgbBuffer[a + 2];
        rgbBuffer[a + 2] = rgbBuffer[b + 2];
        rgbBuffer[b + 2] = tmp;
    }

    private Texture2D GetReadableTexture(Texture sourceTexture)
    {
        Texture2D sourceTexture2D = sourceTexture as Texture2D;
        if (sourceTexture2D != null)
            return sourceTexture2D;

        if (frameTexture2D == null || frameTexture2D.width != sourceTexture.width || frameTexture2D.height != sourceTexture.height)
        {
            if (frameTexture2D != null)
                Destroy(frameTexture2D);
            frameTexture2D = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        }

        RenderTexture currentRT = RenderTexture.active;
        RenderTexture sourceRT = sourceTexture as RenderTexture;
        if (sourceRT == null)
        {
            sourceRT = GetOrCreateBlitReadbackTexture(sourceTexture.width, sourceTexture.height);
            Graphics.Blit(sourceTexture, sourceRT);
        }

        RenderTexture.active = sourceRT;
        frameTexture2D.ReadPixels(new Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0);
        frameTexture2D.Apply(false, false);
        RenderTexture.active = currentRT;
        return frameTexture2D;
    }

    private RenderTexture GetOrCreateBlitReadbackTexture(int width, int height)
    {
        if (blitReadbackTexture != null &&
            blitReadbackTexture.width == width &&
            blitReadbackTexture.height == height)
        {
            return blitReadbackTexture;
        }

        if (blitReadbackTexture != null)
        {
            blitReadbackTexture.Release();
            Destroy(blitReadbackTexture);
        }

        blitReadbackTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            useMipMap = false,
            autoGenerateMips = false
        };
        blitReadbackTexture.Create();
        return blitReadbackTexture;
    }

    private bool TryGetFrameCameraPose(out Pose cameraPose)
    {
        cameraPose = passthroughCameraAccess.GetCameraPose();
        Quaternion q = cameraPose.rotation;
        return q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
    }

    private void OnDisable()
    {
        StopSource();
    }
}
#endif
