using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public class Srt3dWindowsBridge : MonoBehaviour, ITrackerNativeBridge
{
    private const string DllName = "srt3d_unity";

    [Header("Tracking Files (Optional)")]
    [SerializeField] private bool configureTrackingFilesFromInspector = false;
    [SerializeField] private string objPath = "";
    [SerializeField] private string metaPath = "";
    [SerializeField] private string posePath = "";

    private IntPtr nativeModuleHandle = IntPtr.Zero;
    private bool isInitialized;
    private bool hasTrackingConfidenceApi;
    private string lastError;
    private GetTrackingConfidenceDelegate getTrackingConfidenceFunc;

    public bool IsInitialized => isInitialized;
    public bool SupportsConfidence => hasTrackingConfidenceApi;
    public string LastError => lastError;

    [DllImport(DllName, EntryPoint = "SetTrackingFilesW", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetTrackingFilesW(string objPath, string metaPath, string posePath);

    [DllImport(DllName, EntryPoint = "InitializeTracker", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool InitializeTracker();

    [DllImport(DllName, EntryPoint = "ProcessFrame", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ProcessFrameNative(byte[] colorBuffer, int width, int height);

    [DllImport(DllName, EntryPoint = "GetTrackedPose", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GetTrackedPoseNative([Out] float[] outMatrix);

    [DllImport(DllName, EntryPoint = "DestroyTracker", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyTracker();

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetTrackingConfidenceDelegate();

    public bool InitializeBridge()
    {
        lastError = null;
        if (isInitialized)
            return true;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        PrepareNativePluginLoad();
        if (nativeModuleHandle == IntPtr.Zero)
        {
            lastError = "Native module pre-load failed.";
            return false;
        }

        ConfigureTrackingFilesIfNeeded();
        try
        {
            isInitialized = InitializeTracker();
        }
        catch (Exception e)
        {
            isInitialized = false;
            lastError = e.Message;
            Debug.LogError($"[Srt3dWindowsBridge] Initialize failed: {e}");
            return false;
        }

        if (!isInitialized)
        {
            lastError = "InitializeTracker returned false.";
            return false;
        }

        return true;
#else
        lastError = "Srt3dWindowsBridge only supports Windows runtime.";
        Debug.LogError("[Srt3dWindowsBridge] This bridge only runs on Windows.");
        return false;
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
            Debug.LogError($"[Srt3dWindowsBridge] ProcessFrame failed: {e}");
            isInitialized = false;
            return false;
        }
    }

    public bool StartTrackingFromPose(float[] rowMajorPose16)
    {
        lastError = "StartTrackingFromPose is not bound in Srt3dWindowsBridge.";
        return false;
    }

    public bool StartTrackingFromFilePose()
    {
        lastError = "StartTrackingFromFilePose is not bound in Srt3dWindowsBridge.";
        return false;
    }

    public void StopTracking()
    {
        // Windows is no longer the target path for the YOLO-seeded workflow.
    }

    public bool ProcessFrameRgba32(IntPtr rgba32, int width, int height)
    {
        lastError = "ProcessFrameRgba32 is not bound in Srt3dWindowsBridge.";
        return false;
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
            Debug.LogError($"[Srt3dWindowsBridge] GetTrackedPose failed: {e}");
            isInitialized = false;
        }
    }

    public float GetTrackingConfidence()
    {
        if (!hasTrackingConfidenceApi || getTrackingConfidenceFunc == null)
            return -1f;

        try
        {
            float conf = getTrackingConfidenceFunc();
            if (float.IsNaN(conf) || float.IsInfinity(conf))
                return -1f;
            return Mathf.Clamp01(conf);
        }
        catch (Exception e)
        {
            hasTrackingConfidenceApi = false;
            getTrackingConfidenceFunc = null;
            Debug.LogWarning($"[Srt3dWindowsBridge] GetTrackingConfidence failed: {e.Message}");
            return -1f;
        }
    }

    public void ShutdownBridge()
    {
        if (isInitialized)
        {
            try
            {
                DestroyTracker();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Srt3dWindowsBridge] DestroyTracker failed: {e.Message}");
            }
        }

        isInitialized = false;
        hasTrackingConfidenceApi = false;
        getTrackingConfidenceFunc = null;

        if (nativeModuleHandle != IntPtr.Zero)
        {
            if (!FreeLibrary(nativeModuleHandle))
            {
                int err = Marshal.GetLastWin32Error();
                Debug.LogWarning($"[Srt3dWindowsBridge] FreeLibrary failed. win32={err} ({new Win32Exception(err).Message})");
            }
            nativeModuleHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Switches the tracked object by restarting the bridge with new file paths.
    /// Sequences ShutdownBridge → update paths → InitializeBridge.
    /// </summary>
    public bool SwitchObject(string newObjPath, string newMetaPath, string newPosePath)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        ShutdownBridge();
        objPath = newObjPath;
        metaPath = newMetaPath;
        posePath = newPosePath;
        configureTrackingFilesFromInspector = true;
        bool ok = InitializeBridge();
        Debug.Log($"[Srt3dWindowsBridge] SwitchObject={ok}, obj={newObjPath}");
        return ok;
#else
        lastError = "Srt3dWindowsBridge only supports Windows runtime.";
        return false;
#endif
    }

    public bool SwitchObjectByFileName(string newObjFileName, string newMetaFileName, string newPoseFileName)
    {
        lastError = "SwitchObjectByFileName is only implemented for Android StreamingAssets objects.";
        return false;
    }

    private void OnDisable()
    {
        ShutdownBridge();
    }

    private void PrepareNativePluginLoad()
    {
        string pluginPath = Path.Combine(Application.dataPath, "Plugins/Plugins");
        bool setPathOk = SetDllDirectory(pluginPath);
        Debug.Log($"[Srt3dWindowsBridge] SetDllDirectory('{pluginPath}') = {setPathOk}");

        string dllPath = Path.Combine(pluginPath, "srt3d_unity.dll");
        nativeModuleHandle = LoadLibrary(dllPath);
        if (nativeModuleHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            lastError = $"LoadLibrary failed: {dllPath}, win32={err} ({new Win32Exception(err).Message})";
            Debug.LogError("[Srt3dWindowsBridge] " + lastError);
            return;
        }

        BindOptionalNativeApis();
        Debug.Log($"[Srt3dWindowsBridge] LoadLibrary ok: {dllPath}");
    }

    private void ConfigureTrackingFilesIfNeeded()
    {
        if (!configureTrackingFilesFromInspector)
            return;

        string trimmedObj = objPath?.Trim();
        if (string.IsNullOrEmpty(trimmedObj))
        {
            Debug.LogWarning("[Srt3dWindowsBridge] configureTrackingFilesFromInspector is ON but objPath is empty.");
            return;
        }

        string trimmedMeta = string.IsNullOrWhiteSpace(metaPath) ? null : metaPath.Trim();
        string trimmedPose = string.IsNullOrWhiteSpace(posePath) ? null : posePath.Trim();
        bool configured = SetTrackingFilesW(trimmedObj, trimmedMeta, trimmedPose);

        Debug.Log(
            $"[Srt3dWindowsBridge] SetTrackingFilesW={configured}, obj={trimmedObj}, meta={(trimmedMeta ?? "<auto-from-obj>")}, pose={(trimmedPose ?? "<none>")}");
    }

    private void BindOptionalNativeApis()
    {
        hasTrackingConfidenceApi = false;
        getTrackingConfidenceFunc = null;
        if (nativeModuleHandle == IntPtr.Zero)
            return;

        IntPtr confPtr = GetProcAddress(nativeModuleHandle, "GetTrackingConfidence");
        if (confPtr == IntPtr.Zero)
            return;

        getTrackingConfidenceFunc = (GetTrackingConfidenceDelegate)Marshal.GetDelegateForFunctionPointer(
            confPtr,
            typeof(GetTrackingConfidenceDelegate));
        hasTrackingConfidenceApi = getTrackingConfidenceFunc != null;
    }
}
