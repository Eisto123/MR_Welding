using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Android bridge for the SRT3D native tracker.
/// Loads model files from StreamingAssets/SRT3D/ to persistentDataPath/SRT3D/ on first run,
/// then initialises the native plugin asynchronously.
///
/// All objects listed in <see cref="additionalObjectFileNames"/> are copied at startup so that
/// <see cref="SwitchObject"/> can switch to them without any further file I/O.
/// </summary>
public class Srt3dAndroidBridge : MonoBehaviour, ITrackerNativeBridge
{
    private const string SoName = "srt3d_unity";
    private const string SRT3DSubfolder = "SRT3D";

    // ── Inspector ─────────────────────────────────────────────────────────────
    // IMPORTANT: Unity treats any file named *.meta as its own asset metadata.
    // Rename SRT3D precomputed model files from .meta → .srt3d before placing them
    // in StreamingAssets/SRT3D/, otherwise Unity will delete them on import.
    [Header("Primary Tracking Object (file names only, relative to StreamingAssets/SRT3D/)")]
    [SerializeField] private string objFileName  = "buttshape.obj";
    [SerializeField] private string metaFileName = "buttshape.srt3d";
    [SerializeField] private string poseFileName = "buttshape_pose.txt";

    [Header("Additional Objects to Pre-copy (for SwitchObject support)")]
    [Tooltip("File names of extra .obj / .meta / init_pose.txt sets to copy at startup.")]
    [SerializeField] private string[] additionalObjectFileNames =
    {
        "tshape.obj",
        "tshape.srt3d",
        "tshape_pose.txt"
    };

    [Header("Default YOLO Objects to Copy")]
    [Tooltip("Project default YOLO class files. Kept here so existing scenes that still have old primary filenames can switch to the new class objects.")]
    [SerializeField] private string[] defaultYoloObjectFileNames =
    {
        "buttshape.obj",
        "buttshape.srt3d",
        "buttshape_pose.txt",
        "tshape.obj",
        "tshape.srt3d",
        "tshape_pose.txt"
    };
    [Tooltip("When ON, files in persistentDataPath/SRT3D are overwritten from StreamingAssets on startup. Keep this ON while iterating on .obj/.srt3d files; otherwise Android may keep tracking stale templates from a previous install/run.")]
    [SerializeField] private bool overwritePersistentFilesOnStartup = true;

    [Header("Camera Intrinsics (Quest)")]
    [Tooltip("If assigned, real calibrated intrinsics are read from PassthroughCameraAccess " +
             "after initialisation and injected into the native tracker via SetCameraIntrinsics. " +
             "The .srt3d must have been generated with the SAME fx/fy/cx/cy values.")]
    [SerializeField] private Meta.XR.PassthroughCameraAccess passthroughCameraAccess;

    // ── State ──────────────────────────────────────────────────────────────────
    private bool isInitialized;
    private string lastError;
    private string persistentDir;
    private bool hasCustomIntrinsics;
    private float customFx;
    private float customFy;
    private float customCx;
    private float customCy;
    private Coroutine initialiseCoroutine;
    private int initialiseGeneration;

    public bool IsInitialized => isInitialized;
    public bool SupportsConfidence => true;
    public string LastError => lastError;

    // ── Native imports ─────────────────────────────────────────────────────────
    [DllImport(SoName, EntryPoint = "SetTrackingFiles", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetTrackingFilesNative(string objPath, string metaPath, string posePath);

    [DllImport(SoName, EntryPoint = "InitializeTracker", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool InitializeTrackerNative();

    [DllImport(SoName, EntryPoint = "SwitchTrackingObject", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SwitchTrackingObjectNative(string objPath, string metaPath, string posePath);

    [DllImport(SoName, EntryPoint = "SetCameraIntrinsics", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetCameraIntrinsicsNative(float fx, float fy, float cx, float cy);

    [DllImport(SoName, EntryPoint = "ClearCameraIntrinsics", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ClearCameraIntrinsicsNative();

    [DllImport(SoName, EntryPoint = "ResetTrackerPose", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ResetTrackerPoseNative([In] float[] rowMajorPose16);

    [DllImport(SoName, EntryPoint = "StartTrackingFromPose", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool StartTrackingFromPoseNative([In] float[] rowMajorPose16);

    [DllImport(SoName, EntryPoint = "StartTrackingFromFilePose", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool StartTrackingFromFilePoseNative();

    [DllImport(SoName, EntryPoint = "StopTracking", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StopTrackingNative();

    [DllImport(SoName, EntryPoint = "ProcessFrame", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ProcessFrameNative(byte[] colorBuffer, int width, int height);

    [DllImport(SoName, EntryPoint = "ProcessFrameRgba32", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ProcessFrameRgba32Native(IntPtr rgbaBuffer, int width, int height);

    [DllImport(SoName, EntryPoint = "GetTrackedPose", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GetTrackedPoseNative([Out] float[] outMatrix);

    [DllImport(SoName, EntryPoint = "GetTrackingConfidence", CallingConvention = CallingConvention.Cdecl)]
    private static extern float GetTrackingConfidenceNative();

    [DllImport(SoName, EntryPoint = "DestroyTracker", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyTrackerNative();

    // ── ITrackerNativeBridge ───────────────────────────────────────────────────

    /// <summary>
    /// Starts async file copy from StreamingAssets → persistentDataPath, then calls
    /// InitializeTracker on the native side. Returns true immediately to signal
    /// "no error, initialising"; <see cref="IsInitialized"/> flips to true once done.
    /// </summary>
    public bool InitializeBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        lastError = null;
        persistentDir = Path.Combine(Application.persistentDataPath, SRT3DSubfolder);
        Debug.Log($"[Srt3dAndroidBridge] InitializeBridge persistentDir={persistentDir}, overwritePersistentFilesOnStartup={overwritePersistentFilesOnStartup}");
        if (initialiseCoroutine != null)
            StopCoroutine(initialiseCoroutine);
        initialiseGeneration++;
        isInitialized = false;
        initialiseCoroutine = StartCoroutine(CopyFilesAndInitialise(initialiseGeneration));
        return true;
#else
        lastError = "Srt3dAndroidBridge only runs on Android player.";
        Debug.LogWarning("[Srt3dAndroidBridge] Active outside Android runtime.");
        return false;
#endif
    }

    public bool StartTrackingFromPose(float[] rowMajorPose16)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isInitialized)
        {
            lastError = "Cannot start tracking: bridge not yet initialised.";
            return false;
        }

        if (rowMajorPose16 == null || rowMajorPose16.Length < 16)
        {
            lastError = "Cannot start tracking: seed pose must contain 16 floats.";
            return false;
        }

        try
        {
            bool ok = StartTrackingFromPoseNative(rowMajorPose16);
            if (!ok)
                lastError = "StartTrackingFromPose returned false.";
            else
                LogNativeSeedEcho(rowMajorPose16);
            Debug.Log($"[Srt3dAndroidBridge] StartTrackingFromPose={ok}");
            return ok;
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogError($"[Srt3dAndroidBridge] StartTrackingFromPose failed: {e}");
            return false;
        }
#else
        lastError = "Srt3dAndroidBridge only runs on Android player.";
        return false;
#endif
    }

    public bool StartTrackingFromFilePose()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isInitialized)
        {
            lastError = "Cannot start tracking from file pose: bridge not yet initialised.";
            return false;
        }

        try
        {
            bool ok = StartTrackingFromFilePoseNative();
            if (!ok)
                lastError = "StartTrackingFromFilePose returned false.";
            Debug.Log($"[Srt3dAndroidBridge] StartTrackingFromFilePose={ok}");
            return ok;
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogError($"[Srt3dAndroidBridge] StartTrackingFromFilePose failed: {e}");
            return false;
        }
#else
        lastError = "Srt3dAndroidBridge only runs on Android player.";
        return false;
#endif
    }

    public void StopTracking()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isInitialized)
            return;

        try
        {
            StopTrackingNative();
            Debug.Log("[Srt3dAndroidBridge] StopTracking");
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogWarning($"[Srt3dAndroidBridge] StopTracking failed: {e.Message}");
        }
#endif
    }

    public bool ProcessFrame(byte[] rgb24, int width, int height)
    {
        if (!isInitialized)
            return false;
        try
        {
            return ProcessFrameNative(rgb24, width, height);
        }
        catch (Exception e)
        {
            lastError = e.Message;
            isInitialized = false;
            Debug.LogError($"[Srt3dAndroidBridge] ProcessFrame failed: {e}");
            return false;
        }
    }

    public bool ProcessFrameRgba32(IntPtr rgba32, int width, int height)
    {
        if (!isInitialized)
            return false;
        try
        {
            return ProcessFrameRgba32Native(rgba32, width, height);
        }
        catch (Exception e)
        {
            lastError = e.Message;
            isInitialized = false;
            Debug.LogError($"[Srt3dAndroidBridge] ProcessFrameRgba32 failed: {e}");
            return false;
        }
    }

    public void GetTrackedPose(float[] outMatrix16)
    {
        if (!isInitialized || outMatrix16 == null || outMatrix16.Length < 16)
            return;
        try
        {
            GetTrackedPoseNative(outMatrix16);
        }
        catch (Exception e)
        {
            lastError = e.Message;
            isInitialized = false;
            Debug.LogError($"[Srt3dAndroidBridge] GetTrackedPose failed: {e}");
        }
    }

    public float GetTrackingConfidence()
    {
        if (!isInitialized)
            return -1f;
        try
        {
            float c = GetTrackingConfidenceNative();
            return (float.IsNaN(c) || float.IsInfinity(c)) ? -1f : Mathf.Clamp01(c);
        }
        catch
        {
            return -1f;
        }
    }

    public void ShutdownBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        initialiseGeneration++;
        if (initialiseCoroutine != null)
        {
            StopCoroutine(initialiseCoroutine);
            initialiseCoroutine = null;
        }
#endif
        if (!isInitialized)
            return;
        try { DestroyTrackerNative(); }
        catch (Exception e) { Debug.LogWarning($"[Srt3dAndroidBridge] DestroyTracker: {e.Message}"); }
        finally { isInitialized = false; }
    }

    /// <summary>
    /// Atomically switches the tracked object.
    /// Assumes the new object's files are already in persistentDataPath (they are, because all
    /// files listed in <see cref="additionalObjectFileNames"/> are copied at startup).
    /// </summary>
    public bool SwitchObject(string objPath, string metaPath, string posePath)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!isInitialized)
        {
            lastError = "Cannot switch object: bridge not yet initialised.";
            Debug.LogWarning("[Srt3dAndroidBridge] SwitchObject called before initialisation.");
            return false;
        }
        try
        {
            isInitialized = false;
            bool ok = SwitchTrackingObjectNative(objPath, metaPath, posePath);
            if (ok)
            {
                ReapplyCustomIntrinsicsAfterObjectSwitch();
                isInitialized = true;
            }
            else
                lastError = "SwitchTrackingObject returned false.";
            Debug.Log($"[Srt3dAndroidBridge] SwitchObject={ok}, obj={objPath}");
            return ok;
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogError($"[Srt3dAndroidBridge] SwitchObject failed: {e}");
            return false;
        }
#else
        lastError = "Srt3dAndroidBridge only runs on Android player.";
        return false;
#endif
    }

    // ── Public helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Switch to a new object by file names relative to StreamingAssets/SRT3D/.
    /// All files must have been copied to persistentDataPath at startup.
    /// </summary>
    public bool SwitchObjectByFileName(string newObjFileName, string newMetaFileName, string newPoseFileName)
    {
        if (string.IsNullOrEmpty(persistentDir))
        {
            lastError = "persistentDir not set — bridge never initialised on this platform.";
            return false;
        }
        string obj  = Path.Combine(persistentDir, newObjFileName);
        string meta = Path.Combine(persistentDir, newMetaFileName);
        string pose = Path.Combine(persistentDir, newPoseFileName);
        return SwitchObject(obj, meta, pose);
    }

    /// <summary>
    /// Override native camera intrinsics (call after InitializeBridge completes).
    /// Causes the native pipeline to rebuild on the next ProcessFrame.
    /// </summary>
    public bool SetIntrinsics(float fx, float fy, float cx, float cy)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            bool ok = SetCameraIntrinsicsNative(fx, fy, cx, cy);
            if (ok)
            {
                hasCustomIntrinsics = true;
                customFx = fx;
                customFy = fy;
                customCx = cx;
                customCy = cy;
            }
            Debug.Log($"[Srt3dAndroidBridge] SetCameraIntrinsics({fx:F1},{fy:F1},{cx:F1},{cy:F1})={ok}");
            return ok;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Srt3dAndroidBridge] SetCameraIntrinsics failed: {e}");
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>Restore approximate default intrinsics (fx = fy = max(w,h)).</summary>
    public void ClearIntrinsics()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            ClearCameraIntrinsicsNative();
            hasCustomIntrinsics = false;
        }
        catch (Exception e) { Debug.LogWarning($"[Srt3dAndroidBridge] ClearCameraIntrinsics: {e.Message}"); }
#endif
    }

    private void ReapplyCustomIntrinsicsAfterObjectSwitch()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!hasCustomIntrinsics)
            return;

        bool ok = SetCameraIntrinsicsNative(customFx, customFy, customCx, customCy);
        Debug.Log($"[Srt3dAndroidBridge] Reapply intrinsics after object switch={ok}");
#endif
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private static void LogNativeSeedEcho(float[] seedRowMajorPose16)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            float[] echo = new float[16];
            GetTrackedPoseNative(echo);

            float maxAbs = MaxAbsDiff(seedRowMajorPose16, echo);
            float tErr = TranslationDistance(seedRowMajorPose16, echo);
            float rErr = RotationAngleDegrees(seedRowMajorPose16, echo);
            Debug.Log(
                $"[Srt3dAndroidBridge] Native seed echo maxAbs={maxAbs:F6}, " +
                $"rtErr=({tErr:F5}m,{rErr:F2}deg), " +
                $"seedT={FormatCvTranslation(seedRowMajorPose16)}, echoT={FormatCvTranslation(echo)}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Srt3dAndroidBridge] Native seed echo failed: {e.Message}");
        }
#endif
    }

    private static float MaxAbsDiff(float[] a, float[] b)
    {
        if (a == null || b == null)
            return float.PositiveInfinity;

        int n = Mathf.Min(a.Length, b.Length, 16);
        float maxAbs = 0f;
        for (int i = 0; i < n; i++)
            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(a[i] - b[i]));
        return maxAbs;
    }

    private static float TranslationDistance(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 12 || b.Length < 12)
            return float.PositiveInfinity;

        float dx = a[3] - b[3];
        float dy = a[7] - b[7];
        float dz = a[11] - b[11];
        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float RotationAngleDegrees(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 11 || b.Length < 11)
            return float.PositiveInfinity;

        float trace =
            b[0] * a[0] + b[1] * a[1] + b[2] * a[2] +
            b[4] * a[4] + b[5] * a[5] + b[6] * a[6] +
            b[8] * a[8] + b[9] * a[9] + b[10] * a[10];
        float cosTheta = Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f);
        return Mathf.Acos(cosTheta) * Mathf.Rad2Deg;
    }

    private static string FormatCvTranslation(float[] m)
    {
        if (m == null || m.Length < 12)
            return "(invalid)";

        return $"({m[3]:F4},{m[7]:F4},{m[11]:F4})";
    }

    private void OnDisable()
    {
        ShutdownBridge();
    }

    // ── Real intrinsics injection ──────────────────────────────────────────────

    private IEnumerator WaitForCameraAndApplyIntrinsics()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (passthroughCameraAccess == null)
        {
            Debug.Log("[Srt3dAndroidBridge] PassthroughCameraAccess not assigned — " +
                      "using approximate intrinsics (fx = fy = max(w, h)).");
            yield break;
        }

        // Wait until the camera stream is live (IsPlaying becomes true after first frame arrives).
        float timeout = 10f;
        float elapsed = 0f;
        while (!passthroughCameraAccess.IsPlaying && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!passthroughCameraAccess.IsPlaying)
        {
            Debug.LogWarning("[Srt3dAndroidBridge] Timed out waiting for PassthroughCameraAccess.IsPlaying — " +
                             "using approximate intrinsics.");
            yield break;
        }

        var intr = passthroughCameraAccess.Intrinsics;
        float fx = intr.FocalLength.x;
        float fy = intr.FocalLength.y;

        // PrincipalPoint is in sensor coordinates.
        // If the delivered frame is smaller than the sensor (cropped), the principal
        // point must be shifted to frame coordinates by subtracting the crop offset.
        // Crop is assumed to be symmetric: offset = (sensor - frame) / 2.
        Vector2Int frameRes  = passthroughCameraAccess.CurrentResolution;
        Vector2Int sensorRes = intr.SensorResolution;
        float cropX = (sensorRes.x - frameRes.x) * 0.5f;
        float cropY = (sensorRes.y - frameRes.y) * 0.5f;
        float cx = intr.PrincipalPoint.x - cropX;
        float cy = intr.PrincipalPoint.y - cropY;

        Debug.Log($"[Srt3dAndroidBridge] Real intrinsics (sensor {sensorRes} → frame {frameRes}): " +
                  $"fx={fx:F2} fy={fy:F2} cx={cx:F2} cy={cy:F2} " +
                  $"(sensor cx={intr.PrincipalPoint.x:F2} cy={intr.PrincipalPoint.y:F2}, " +
                  $"crop offset=({cropX:F1},{cropY:F1}))");

        bool ok = SetIntrinsics(fx, fy, cx, cy);
        Debug.Log($"[Srt3dAndroidBridge] SetCameraIntrinsics with real values → {ok}");
#else
        yield break;
#endif
    }

    // ── Async file copy + init coroutine ───────────────────────────────────────

    private IEnumerator CopyFilesAndInitialise(int generation)
    {
        if (generation != initialiseGeneration)
            yield break;

        Directory.CreateDirectory(persistentDir);
        Debug.Log($"[Srt3dAndroidBridge] CopyFilesAndInitialise begin, streamingAssetsSrt3d={Application.streamingAssetsPath}/{SRT3DSubfolder}");

        // Build the full list: primary object + any additional objects
        var filesToCopy = new System.Collections.Generic.List<string>
        {
            objFileName, metaFileName, poseFileName
        };
        if (additionalObjectFileNames != null)
        {
            foreach (string f in additionalObjectFileNames)
            {
                if (!string.IsNullOrWhiteSpace(f))
                    filesToCopy.Add(f.Trim());
            }
        }
        if (defaultYoloObjectFileNames != null)
        {
            foreach (string f in defaultYoloObjectFileNames)
            {
                if (!string.IsNullOrWhiteSpace(f))
                    filesToCopy.Add(f.Trim());
            }
        }
        filesToCopy.Add("buttshape.obj");
        filesToCopy.Add("buttshape.srt3d");
        filesToCopy.Add("buttshape_pose.txt");
        filesToCopy.Add("tshape.obj");
        filesToCopy.Add("tshape.srt3d");
        filesToCopy.Add("tshape_pose.txt");

        var uniqueFilesToCopy = new System.Collections.Generic.HashSet<string>(filesToCopy);
        int copiedCount = 0;
        int skippedCount = 0;
        foreach (string fileName in uniqueFilesToCopy)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            string dst = Path.Combine(persistentDir, fileName);
            if (!overwritePersistentFilesOnStartup && File.Exists(dst))
            {
                Debug.Log($"[Srt3dAndroidBridge] Already exists, skipping copy: {fileName}");
                skippedCount++;
                continue;
            }

            string srcUri = Path.Combine(Application.streamingAssetsPath, SRT3DSubfolder, fileName);
            using UnityWebRequest req = UnityWebRequest.Get(srcUri);
            yield return req.SendWebRequest();

            if (generation != initialiseGeneration)
                yield break;

            if (req.result != UnityWebRequest.Result.Success)
            {
                lastError = $"Failed to read StreamingAssets/{SRT3DSubfolder}/{fileName}: {req.error}";
                Debug.LogError($"[Srt3dAndroidBridge] {lastError}");
                initialiseCoroutine = null;
                yield break;
            }

            File.WriteAllBytes(dst, req.downloadHandler.data);
            copiedCount++;
            Debug.Log($"[Srt3dAndroidBridge] Copied {fileName} → {dst}");
        }
        Debug.Log($"[Srt3dAndroidBridge] CopyFilesAndInitialise done, copied={copiedCount}, skipped={skippedCount}, total={uniqueFilesToCopy.Count}");

        // All files ready — call native
        string persistentObj  = Path.Combine(persistentDir, objFileName);
        string persistentMeta = Path.Combine(persistentDir, metaFileName);
        string persistentPose = Path.Combine(persistentDir, poseFileName);

        bool configured = false;
        bool inited     = false;
        try
        {
            configured = SetTrackingFilesNative(persistentObj, persistentMeta, persistentPose);
            Debug.Log($"[Srt3dAndroidBridge] SetTrackingFiles={configured}");
            if (configured)
            {
                inited = InitializeTrackerNative();
                Debug.Log($"[Srt3dAndroidBridge] InitializeTracker={inited}");
            }
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogError($"[Srt3dAndroidBridge] Native init failed: {e}");
            initialiseCoroutine = null;
            yield break;
        }

        if (!inited)
        {
            lastError = "InitializeTracker returned false after file copy.";
            Debug.LogError($"[Srt3dAndroidBridge] {lastError}");
            initialiseCoroutine = null;
            yield break;
        }

        yield return WaitForCameraAndApplyIntrinsics();

        if (generation != initialiseGeneration)
            yield break;

        isInitialized = true;
        initialiseCoroutine = null;
        Debug.Log("[Srt3dAndroidBridge] Initialisation complete, ready for YOLO-seeded tracking.");
    }
}
