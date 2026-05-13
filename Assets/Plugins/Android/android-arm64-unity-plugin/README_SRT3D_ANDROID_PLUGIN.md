# SRT3D Unity Android Plugin

Last updated: 2026-05-05

This folder contains the Android arm64-v8a native plugin package for Unity.

## Files

Copy these three `.so` files together into the Unity Android plugin folder:

```text
Assets/Plugins/Android/libs/arm64-v8a/
|- libsrt3d_unity.so
|- libopencv_java4.so
`- libc++_shared.so
```

File roles:

- `libsrt3d_unity.so`: the SRT3D Unity bridge built from this project.
- `libopencv_java4.so`: OpenCV Android runtime dependency.
- `libc++_shared.so`: Android NDK C++ runtime dependency.

Unity C# should use:

```csharp
DllImport("srt3d_unity")
```

Unity automatically maps this to `libsrt3d_unity.so` on Android.

## Native API

All public functions use C ABI and are exported from `libsrt3d_unity.so`.

### SetTrackingFiles

```cpp
bool SetTrackingFiles(const char* objPath, const char* metaPath, const char* posePath);
bool SetTrackingFilesA(const char* objPath, const char* metaPath, const char* posePath);
bool SetTrackingFilesW(const wchar_t* objPath, const wchar_t* metaPath, const wchar_t* posePath);
```

Purpose:

- Configure the object mesh `.obj`, precomputed model `.meta`, and optional initial pose `.txt`.
- `SetTrackingFiles` is the Android/Linux-friendly alias.
- `SetTrackingFilesA` uses UTF-8/narrow paths.
- `SetTrackingFilesW` is Windows-only and returns `false` on Android.
- Passing an empty or null `metaPath` derives the `.meta` path from the `.obj` path.
- Passing an empty or null `posePath` is allowed. If no file pose is available, the plugin falls back to an identity pose translated to `z = 0.8`.
- Calling this resets the runtime tracker and stops the current tracking session.

Android notes:

- The paths must be real filesystem paths.
- Do not pass `jar:file://...` StreamingAssets paths directly.
- Copy model files from `StreamingAssets` to `Application.persistentDataPath` first, then pass those persistent paths.
- Runtime-only Android builds require `.meta` to already exist.

### InitializeTracker

```cpp
bool InitializeTracker();
```

Purpose:

- Resets runtime tracker instances while keeping configured file paths.
- Validates the selected `.obj` path.
- Derives `metaPath` from `objPath` if no `.meta` path was explicitly configured.
- The heavy SRT3D pipeline is still created lazily on the first frame after tracking is started.

Important:

- `InitializeTracker` does not enable tracking by itself.
- Call `StartTrackingFromFilePose` or `StartTrackingFromPose` before sending frames to `ProcessFrame` or `ProcessFrameRgba32`.

### SetCameraIntrinsics

```cpp
bool SetCameraIntrinsics(float fx, float fy, float cx, float cy);
```

Purpose:

- Override the default camera intrinsics.
- Returns `false` if `fx` or `fy` is non-finite or <= 0, or if `cx` or `cy` is non-finite.
- Recommended on Quest if real passthrough camera intrinsics are available.
- Calling this resets runtime state, so the next valid frame rebuilds the pipeline.

Default behavior without this call:

```text
fx = fy = max(width, height)
cx = width * 0.5
cy = height * 0.5
```

Important:

- The `.meta` file should be generated with intrinsics consistent with runtime use.
- During early Quest validation, using the default approximate intrinsics is acceptable if your `.meta` was generated that way.

### ClearCameraIntrinsics

```cpp
void ClearCameraIntrinsics();
```

Purpose:

- Clears custom intrinsics.
- Restores the default approximate intrinsics behavior.
- Resets runtime state.

### SwitchTrackingObject

```cpp
bool SwitchTrackingObject(const char* objPath, const char* metaPath, const char* posePath);
```

Purpose:

- Atomic object switching helper.
- Internally equivalent to:

```text
Reset runtime state -> SetTrackingFilesA -> InitializeTracker
```

Use this for switching between `trackingobject1`, `trackingobject2`, etc.

Important:

- This plugin tracks one object at a time. It does not track multiple objects simultaneously.
- Switching object stops tracking. Call `StartTrackingFromFilePose` or `StartTrackingFromPose` before resuming frame processing.

### ResetTrackerPose

```cpp
bool ResetTrackerPose(const float* rowMajorPose16);
```

Purpose:

- Sets a runtime initial pose from a row-major 4x4 matrix.
- Updates the current pose immediately.
- If the tracker pipeline already exists, applies the pose to the current SRT3D body.
- Clears confidence to `0`.
- Does not enable tracking by itself.

Returns `false` if:

- `rowMajorPose16` is null.
- Any matrix value is non-finite.
- Matrix element `[3, 3]` is effectively zero.

### StartTrackingFromPose

```cpp
bool StartTrackingFromPose(const float* rowMajorPose16);
```

Purpose:

- Calls `ResetTrackerPose(rowMajorPose16)`.
- Enables tracking if the pose is valid.
- Clears confidence to `0`.

Use this when Unity has an externally estimated object pose, for example from a marker, saved anchor, hand placement workflow, or app-side initialization.

### StartTrackingFromFilePose

```cpp
bool StartTrackingFromFilePose();
```

Purpose:

- Enables tracking using the configured pose file.
- Clears any runtime pose previously set with `ResetTrackerPose` or `StartTrackingFromPose`.
- Resets runtime state so the pipeline is rebuilt lazily on the next valid frame.
- If the pose file cannot be loaded, the plugin falls back to an identity pose translated to `z = 0.8`.

Returns `false` if:

- No valid `.obj` path is configured.
- Runtime-only Android builds do not have a valid `.meta` path.

### StopTracking

```cpp
void StopTracking();
```

Purpose:

- Disables tracking.
- Clears confidence to `0`.
- Does not release configured file paths or the current runtime pipeline.

After calling this, `ProcessFrame` and `ProcessFrameRgba32` return `false` until tracking is started again.

### ProcessFrame

```cpp
bool ProcessFrame(unsigned char* colorBuffer, int width, int height);
```

Purpose:

- Processes one RGB24 frame.
- Input format is RGB byte order, 3 bytes per pixel.
- The plugin internally converts RGB to BGR for OpenCV/SRT3D.
- Returns `true` if tracking ran successfully for this frame.

Behavior:

- Tracking must be enabled with `StartTrackingFromFilePose` or `StartTrackingFromPose` first.
- If no pipeline exists yet, the first valid frame creates it.
- If frame resolution changes, runtime state is reset and rebuilt for the new resolution.
- Invalid input, disabled tracking, setup failure, or tracking failure returns `false` and sets confidence to `0`.

### ProcessFrameRgba32

```cpp
bool ProcessFrameRgba32(const unsigned char* rgbaBuffer, int width, int height);
```

Purpose:

- Processes one RGBA32 frame.
- Input format is RGBA byte order, 4 bytes per pixel.
- The plugin internally converts RGBA to BGR for OpenCV/SRT3D.
- Returns `true` if tracking ran successfully for this frame.

Use this when the Unity frame source naturally provides `TextureFormat.RGBA32` or another RGBA byte buffer, avoiding a Unity-side RGB24 repack.

Behavior is otherwise the same as `ProcessFrame`.

### GetTrackedPose

```cpp
void GetTrackedPose(float* outMatrix16);
```

Purpose:

- Writes the latest 4x4 pose matrix into `outMatrix16`.
- Layout is row-major, 16 floats.
- The pose is `T_cam_obj`: object pose in the OpenCV/SRT3D camera coordinate system.
- If `outMatrix16` is null, the function returns without writing.

Coordinate convention:

```text
X: right
Y: down
Z: forward
```

Unity-side conversion should keep using the verified `PoseConverter` logic:

```text
position.y *= -1
forward = (m02, -m12, m22)
up      = (-m01, m11, -m21)
```

### GetTrackingConfidence

```cpp
float GetTrackingConfidence();
```

Purpose:

- Returns the latest confidence value in `[0, 1]`.
- `0` means unavailable, stopped, failed, or low-confidence frame.

### DestroyTracker

```cpp
void DestroyTracker();
```

Purpose:

- Fully releases the current tracker, camera, body, model, and region modality runtime objects.
- Resets frame size, latest pose, confidence, tracking enabled state, and runtime initial pose.
- Keeps configured file paths and custom intrinsics.

Use before shutdown, or before a manual object switch if not using `SwitchTrackingObject`.

## Recommended Android Call Flow

Initial setup using a pose file:

```text
1. Copy obj/meta/init_pose from StreamingAssets to persistentDataPath
2. SetTrackingFiles(persistentObj, persistentMeta, persistentPose)
3. Optional: SetCameraIntrinsics(fx, fy, cx, cy)
4. InitializeTracker()
5. StartTrackingFromFilePose()
6. For each frame:
   - ProcessFrame(rgb24, width, height)
     or ProcessFrameRgba32(rgba32, width, height)
   - GetTrackedPose(matrix16)
   - GetTrackingConfidence()
7. StopTracking() when temporarily pausing tracking
8. DestroyTracker() on shutdown
```

Initial setup using a Unity-provided pose:

```text
1. Copy obj/meta from StreamingAssets to persistentDataPath
2. SetTrackingFiles(persistentObj, persistentMeta, null)
3. Optional: SetCameraIntrinsics(fx, fy, cx, cy)
4. InitializeTracker()
5. StartTrackingFromPose(rowMajorPose16)
6. Send frames with ProcessFrame or ProcessFrameRgba32
```

Object switching:

```text
1. Stop sending frames temporarily
2. SwitchTrackingObject(newObj, newMeta, newPose)
3. StartTrackingFromFilePose()
4. Resume ProcessFrame or ProcessFrameRgba32
```

Manual pose reset while tracking:

```text
1. ResetTrackerPose(rowMajorPose16)
2. Continue sending frames
```

Use `StartTrackingFromPose(rowMajorPose16)` instead if tracking is currently stopped and should start from that pose.

## C# P/Invoke Example

```csharp
using System;
using System.Runtime.InteropServices;

internal static class Srt3dNative
{
    private const string Lib = "srt3d_unity";

    [DllImport(Lib, EntryPoint = "SetTrackingFiles", CharSet = CharSet.Ansi)]
    internal static extern bool SetTrackingFiles(
        string objPath,
        string metaPath,
        string posePath);

    [DllImport(Lib, EntryPoint = "SwitchTrackingObject", CharSet = CharSet.Ansi)]
    internal static extern bool SwitchTrackingObject(
        string objPath,
        string metaPath,
        string posePath);

    [DllImport(Lib)]
    internal static extern bool InitializeTracker();

    [DllImport(Lib)]
    internal static extern bool SetCameraIntrinsics(
        float fx,
        float fy,
        float cx,
        float cy);

    [DllImport(Lib)]
    internal static extern void ClearCameraIntrinsics();

    [DllImport(Lib)]
    internal static extern bool ResetTrackerPose(
        [In] float[] rowMajorPose16);

    [DllImport(Lib)]
    internal static extern bool StartTrackingFromPose(
        [In] float[] rowMajorPose16);

    [DllImport(Lib)]
    internal static extern bool StartTrackingFromFilePose();

    [DllImport(Lib)]
    internal static extern void StopTracking();

    [DllImport(Lib)]
    internal static extern bool ProcessFrame(
        IntPtr rgb24,
        int width,
        int height);

    [DllImport(Lib)]
    internal static extern bool ProcessFrameRgba32(
        IntPtr rgba32,
        int width,
        int height);

    [DllImport(Lib)]
    internal static extern void GetTrackedPose(
        [Out] float[] outMatrix16);

    [DllImport(Lib)]
    internal static extern float GetTrackingConfidence();

    [DllImport(Lib)]
    internal static extern void DestroyTracker();
}
```

For managed `byte[]` frame data, pin the array before calling `ProcessFrame` or `ProcessFrameRgba32`, or use a native buffer pointer from your frame source.

## Model File Requirements

Each trackable object should have:

```text
object_name.obj
object_name.meta
object_name_init_pose.txt
```

For Android:

- Put source files under `Assets/StreamingAssets/SRT3D/`.
- At runtime, copy them to `Application.persistentDataPath/SRT3D/`.
- Pass the persistent paths to the native plugin.
- The pose file is optional if Unity starts tracking with `StartTrackingFromPose`.

Do not rely on Android StreamingAssets paths directly inside C++.

## Verification Commands

From the project root after building:

```bat
%UnityAndroid%\NDK\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-nm.exe -D libsrt3d_unity.so
```

Check that exported functions include:

```text
SetTrackingFiles
SetTrackingFilesA
SetTrackingFilesW
InitializeTracker
SetCameraIntrinsics
ClearCameraIntrinsics
SwitchTrackingObject
ResetTrackerPose
StartTrackingFromPose
StartTrackingFromFilePose
StopTracking
ProcessFrame
ProcessFrameRgba32
GetTrackedPose
GetTrackingConfidence
DestroyTracker
```

Check dependencies:

```bat
%UnityAndroid%\NDK\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-readelf.exe -d libsrt3d_unity.so
```

Expected dependencies include:

```text
libopencv_java4.so
libc++_shared.so
liblog.so
libandroid.so
```
