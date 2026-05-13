using UnityEngine;

/// <summary>
/// Synchronises a Unity Camera's projection matrix to the intrinsic parameters used by the
/// SRT3D tracker so that 3D overlays rendered by that camera project onto the image plane in
/// the same way the tracker does.
///
/// SRT3D convention: fx = fy = max(frameWidth, frameHeight), cx = frameWidth/2, cy = frameHeight/2.
/// When <see cref="useCustomIntrinsics"/> is false this component derives those values automatically
/// from the frame dimensions reported by <see cref="TrackingOrchestrator.LastResult"/>.
/// When <see cref="useCustomIntrinsics"/> is true you can supply exact calibrated values.
/// </summary>
[RequireComponent(typeof(Camera))]
public class CameraProjectionSync : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;

    [Header("Override intrinsics (leave 0 to auto-derive from frame size)")]
    [SerializeField] private bool useCustomIntrinsics = false;
    [SerializeField] private float customFx = 0f;
    [SerializeField] private float customFy = 0f;
    [SerializeField] private float customCx = 0f;
    [SerializeField] private float customCy = 0f;
    [SerializeField] private int customWidth  = 0;
    [SerializeField] private int customHeight = 0;

    [Header("Clip planes")]
    [SerializeField] private float nearClip = 0.05f;
    [SerializeField] private float farClip  = 20f;

    [Header("Debug")]
    [SerializeField] private bool logOnChange = true;

    private Camera _cam;
    private int _lastWidth;
    private int _lastHeight;
    private bool _projectionSet;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponentInParent<TrackingOrchestrator>();
    }

    private void LateUpdate()
    {
        int w, h;
        float fx, fy, cx, cy;

        if (useCustomIntrinsics && customWidth > 0 && customHeight > 0)
        {
            w  = customWidth;
            h  = customHeight;
            fx = customFx > 0f ? customFx : Mathf.Max(w, h);
            fy = customFy > 0f ? customFy : fx;
            cx = customCx > 0f ? customCx : w * 0.5f;
            cy = customCy > 0f ? customCy : h * 0.5f;
        }
        else
        {
            if (trackingOrchestrator == null)
                return;
            TrackingResult r = trackingOrchestrator.LastResult;
            w = r.FrameWidth;
            h = r.FrameHeight;
            if (w <= 0 || h <= 0)
                return;

            // SRT3D uses fx = fy = max(w, h), principal point at image centre.
            float f = Mathf.Max(w, h);
            fx = f;
            fy = f;
            cx = w * 0.5f;
            cy = h * 0.5f;
        }

        if (_projectionSet && w == _lastWidth && h == _lastHeight)
            return;

        ApplyProjection(w, h, fx, fy, cx, cy);
        _lastWidth  = w;
        _lastHeight = h;
        _projectionSet = true;
    }

    private void ApplyProjection(int w, int h, float fx, float fy, float cx, float cy)
    {
        // Build an OpenGL-style frustum from pinhole camera intrinsics.
        // left/right/bottom/top are computed at the near plane.
        float n = nearClip;
        float left   = -(cx / fx) * n;
        float right  =  ((w - cx) / fx) * n;
        float bottom = -((h - cy) / fy) * n;
        float top    =  (cy / fy) * n;

        _cam.nearClipPlane = nearClip;
        _cam.farClipPlane  = farClip;
        _cam.projectionMatrix = Matrix4x4.Frustum(left, right, bottom, top, nearClip, farClip);

        if (logOnChange)
            Debug.Log($"[CameraProjectionSync] Projection updated: {w}x{h}, fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cy:F1} | fovY≈{2f * Mathf.Atan(cy / fy) * Mathf.Rad2Deg:F2}°");
    }

    /// <summary>
    /// Call this to manually force a projection update (e.g. when the camera near/far clip changes
    /// at runtime via another component).
    /// </summary>
    public void ForceUpdate()
    {
        _projectionSet = false;
    }

    private void OnDisable()
    {
        // Restore Unity's default projection so the camera works normally when this is off.
        if (_cam != null)
            _cam.ResetProjectionMatrix();
        _projectionSet = false;
    }
}
