using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws the wireframe of a reference mesh at the pose reported by <see cref="TrackingOrchestrator"/>.
///
/// Uses Graphics.DrawMesh with MeshTopology.Lines — fully compatible with URP and Built-in RP.
///
/// Setup:
///   1. Import your .obj into Assets. Unity creates a model prefab containing a MeshFilter.
///      Drag the MeshFilter component (from the imported model's root GameObject) into meshSource.
///      If you see "Built 0 edges from 'default'" the wrong MeshFilter was assigned — the Unity
///      default mesh has no triangles. Make sure the MeshFilter shows your .obj's mesh name.
///   2. Add this component to any active GameObject (e.g. the TrackingRoot).
///   3. Assign poseReferenceTransform to the physical camera reference Transform.
///   4. (Optional) Assign wireframeMaterial. If left empty a URP-compatible material is created
///      automatically from "Universal Render Pipeline/Unlit" or "Unlit/Color".
/// </summary>
[DefaultExecutionOrder(100)]
public class TrackingWireframeOverlay : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private TrackingOrchestrator trackingOrchestrator;
    [SerializeField] private Transform poseReferenceTransform;

    [Header("Mesh")]
    [Tooltip("MeshFilter from the imported .obj asset. Must have triangles (not Unity's default mesh).")]
    [SerializeField] private MeshFilter meshSource;

    [Header("Material (optional — auto-created if empty)")]
    [Tooltip("Assign a URP Unlit material. Leave empty to auto-create.")]
    [SerializeField] private Material wireframeMaterial;

    [Header("Appearance")]
    [SerializeField] private Color trackingColor = new Color(0.1f, 1f, 0.35f, 1f);
    [SerializeField] private Color lostColor     = new Color(0.5f, 0.5f, 0.5f, 0.6f);
    [Tooltip("Render on top of everything (ZTest Always). Useful when the wireframe is hidden behind scene geometry.")]
    [SerializeField] private bool alwaysOnTop = false;

    [Header("Probation Debug")]
    [Tooltip("When ON the wireframe is also rendered DURING the StartConfidenceGate probation window, drawn in 'Probation Color' (default red). Use this to inspect SRT3D's in-progress pose for objects whose confidence never crosses the threshold (e.g. TShape) — you can directly see how far off the seed is and how it drifts during the probation frames. Has no effect when StartConfidenceGate is disabled.")]
    [SerializeField] private bool showProbationWireframe = false;
    [SerializeField] private Color probationColor = new Color(1f, 0.15f, 0.15f, 1f);

    [Header("Attempt Debug")]
    [Tooltip("When ON, the latest initial SRT3D pose for the current unconfirmed seed attempt is drawn in red whenever native probation has not produced a valid pose yet. When native does produce a probation pose, the existing red probation wireframe takes over so you can watch convergence.")]
    [SerializeField] private bool showAttemptPoseWireframe = true;
    [SerializeField] private Color attemptPoseColor = new Color(1f, 0.15f, 0.15f, 1f);

    [Header("Seed Pose Debug")]
    [Tooltip("Optional reference to a MonoBehaviour that implements ISeedPoseDebugSource (e.g. YoloEnvironmentPoseSeedProvider). When 'Show Seed Pose Wireframe' is on, the same wireframe mesh is drawn at the most recent physical seed pose, in 'Seed Pose Color' (default yellow). This is the YOLO+raycast mesh pose before TemplateFrameCorrectionEuler is applied for SRT3D, so it isolates physical seed quality from native SRT3D drift. If the yellow wireframe is wrong, tune SurfaceNormalAxis/yaw. If yellow is right but red/green is rolled, tune TemplateFrameCorrectionEuler.")]
    [SerializeField] private MonoBehaviour seedPoseDebugSourceBehaviour;
    [SerializeField] private bool showSeedPoseWireframe = false;
    [SerializeField] private Color seedPoseColor = new Color(1f, 0.95f, 0.2f, 1f);
    [Tooltip("Draws the exact initial OpenCV/SRT3D pose that was handed to native, converted back through PoseConverter and template-correction inverse. Cyan should overlap yellow if Unity's seed-to-SRT3D conversion is correct; if cyan matches yellow but red drifts, the drift happens inside SRT3D after TrackIter.")]
    [SerializeField] private bool showInitialPoseWireframe = false;
    [SerializeField] private Color initialPoseColor = new Color(0f, 1f, 1f, 1f);
    [Tooltip("If true the seed wireframe is only drawn while SRT3D has not yet been confirmed (i.e. during probation or before any seed is accepted). Turn OFF to keep the seed wireframe visible during steady-state tracking, which is useful for diagnosing SRT3D drift away from the original seed.")]
    [SerializeField] private bool hideSeedWireframeAfterConfirmed = false;

    [Header("Pose Settings (must match TrackingVisualizer)")]
    [SerializeField] private bool poseIsInCameraSpace = true;
    [Tooltip("Negate the Y axis of the pose. The current Android plugin reports OpenCV camera coordinates (X right, Y down, Z forward).")]
    [SerializeField] private bool flipCvYToUnity = true;
    [SerializeField] private float translationScale = 1f;
    [Tooltip("When the frame source provides the physical camera pose at the camera frame timestamp, use it instead of the current Transform. This reduces HMD-motion latency on Quest.")]
    [SerializeField] private bool useFramePoseReferenceWhenAvailable = true;

    [Header("Mesh Scale")]
    [Tooltip("Uniform scale applied to the wireframe mesh. Use this to correct unit mismatches:\n" +
             "  .obj created in meters  → 1.0\n" +
             "  .obj created in cm      → 0.01\n" +
             "  .obj created in mm (Blender default) → 0.001")]
    [SerializeField] private float meshScale = 1f;

    private Mesh _wireMesh;
    private MaterialPropertyBlock _mpb;
    private bool _ready;
    private bool _ownsMaterial;
    private ISeedPoseDebugSource _seedPoseDebugSource;

    public float LastPoseConversionMs { get; private set; } = -1f;

    private void Start()
    {
        if (trackingOrchestrator == null)
            trackingOrchestrator = GetComponentInParent<TrackingOrchestrator>();
        if (poseReferenceTransform == null && Camera.main != null)
            poseReferenceTransform = Camera.main.transform;

        _mpb = new MaterialPropertyBlock();

        EnsureMaterial();
        BuildWireMesh();

        if (seedPoseDebugSourceBehaviour != null)
        {
            _seedPoseDebugSource = seedPoseDebugSourceBehaviour as ISeedPoseDebugSource;
            if (_seedPoseDebugSource == null)
            {
                Debug.LogWarning(
                    "[TrackingWireframeOverlay] seedPoseDebugSourceBehaviour does not implement ISeedPoseDebugSource. " +
                    "Seed pose debug wireframe will not be rendered.");
            }
        }
        else if (trackingOrchestrator != null)
        {
            MonoBehaviour[] behaviours = trackingOrchestrator.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                _seedPoseDebugSource = behaviours[i] as ISeedPoseDebugSource;
                if (_seedPoseDebugSource != null)
                    break;
            }
        }
    }

    private void EnsureMaterial()
    {
        if (wireframeMaterial != null)
            return;

        // Try URP first, then built-in fallbacks.
        string[] candidates = {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Hidden/Internal-Colored",
        };

        Shader sh = null;
        foreach (string name in candidates)
        {
            sh = Shader.Find(name);
            if (sh != null)
            {
                Debug.Log($"[TrackingWireframeOverlay] Using shader '{name}'.");
                break;
            }
        }

        if (sh == null)
        {
            Debug.LogError("[TrackingWireframeOverlay] No compatible shader found. Assign a wireframeMaterial manually.");
            return;
        }

        wireframeMaterial = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        _ownsMaterial = true;
    }

    private void BuildWireMesh()
    {
        _ready = false;
        if (meshSource == null || meshSource.sharedMesh == null)
        {
            Debug.LogWarning("[TrackingWireframeOverlay] meshSource is null — assign a MeshFilter with your .obj mesh.");
            return;
        }

        Mesh src = meshSource.sharedMesh;
        int[] tris = src.triangles;

        if (tris.Length == 0)
        {
            Debug.LogError($"[TrackingWireframeOverlay] Mesh '{src.name}' has 0 triangles. " +
                           "This is Unity's default mesh, not your imported .obj. " +
                           "Import your .obj file, drag the model into the scene, then drag its MeshFilter here.");
            return;
        }

        Vector3[] srcVerts = src.vertices;

        // Build unique edge index pairs.
        var seen    = new HashSet<long>();
        var verts   = new List<Vector3>();
        var indices = new List<int>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            TryAddEdge(srcVerts, tris[i],   tris[i+1], seen, verts, indices);
            TryAddEdge(srcVerts, tris[i+1], tris[i+2], seen, verts, indices);
            TryAddEdge(srcVerts, tris[i+2], tris[i],   seen, verts, indices);
        }

        if (_wireMesh != null)
            Destroy(_wireMesh);

        _wireMesh = new Mesh { name = "TrackingWireframeMesh" };
        _wireMesh.SetVertices(verts);
        _wireMesh.SetIndices(indices, MeshTopology.Lines, 0);
        _wireMesh.RecalculateBounds();

        _ready = true;
        Debug.Log($"[TrackingWireframeOverlay] Built {indices.Count / 2} unique edges from '{src.name}'.");
    }

    private static void TryAddEdge(Vector3[] src, int a, int b,
        HashSet<long> seen, List<Vector3> verts, List<int> indices)
    {
        int lo = Mathf.Min(a, b);
        int hi = Mathf.Max(a, b);
        long key = ((long)lo << 32) | (uint)hi;
        if (!seen.Add(key))
            return;

        int baseIdx = verts.Count;
        verts.Add(src[lo]);
        verts.Add(src[hi]);
        indices.Add(baseIdx);
        indices.Add(baseIdx + 1);
    }

    private void Update()
    {
        if (!_ready || trackingOrchestrator == null || wireframeMaterial == null)
            return;

        if (alwaysOnTop)
            wireframeMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

        TrackingResult result = trackingOrchestrator.LastResult;

        // 1) Main wireframe (the SRT3D-tracked pose). Confirmed pose renders normally; during the
        //    probation window we optionally render in the probation colour so the user can see
        //    where SRT3D is converging while it has not yet cleared the confidence gate.
        bool showUnconfirmedSrtPose = showProbationWireframe || showAttemptPoseWireframe;
        bool isProbationDraw =
            result.PoseValid && !result.IsConfirmed && result.IsInProbation && showUnconfirmedSrtPose;
        bool drawMain = result.PoseValid && (result.IsConfirmed || isProbationDraw);
        if (drawMain && TryBuildWireframeWorldPose(result, out Vector3 worldPos, out Quaternion worldRot))
        {
            Color color;
            if (isProbationDraw)
                color = probationColor;
            else if (result.State == TrackingState.Tracking)
                color = trackingColor;
            else
                color = lostColor;

            DrawWireMeshAt(worldPos, worldRot, color);
        }

        if (ShouldDrawAttemptPose(result, drawMain) &&
            _seedPoseDebugSource.TryGetLastInitialSrt3dWorldPose(
                out Pose attemptPose,
                out int attemptClassId,
                out _) &&
            (attemptClassId < 0 || attemptClassId == result.TrackedClassId))
        {
            DrawWireMeshAt(attemptPose.position, attemptPose.rotation, attemptPoseColor);
        }

        // 2) Seed pose wireframe (the YOLO+raycast pose, BEFORE SRT3D optimises it). Independent
        //    of orchestrator state — drawn whenever the seed provider has produced at least one
        //    successful seed, optionally hidden once SRT3D has confirmed the lock.
        if (showSeedPoseWireframe && _seedPoseDebugSource != null)
        {
            bool hideForConfirmed = hideSeedWireframeAfterConfirmed && result.IsConfirmed;
            if (!hideForConfirmed &&
                _seedPoseDebugSource.TryGetLastSeedWorldPose(out Pose seedPose, out _, out _))
            {
                DrawWireMeshAt(seedPose.position, seedPose.rotation, seedPoseColor);
            }
        }

        // 3) Initial pose round-trip wireframe. This is computed by the seed provider immediately
        // after it builds the exact row-major OpenCV/SRT3D matrix sent to native.
        if (showInitialPoseWireframe && _seedPoseDebugSource != null &&
            _seedPoseDebugSource.TryGetLastInitialSrt3dWorldPose(
                out Pose initialPose,
                out _,
                out _))
        {
            DrawWireMeshAt(initialPose.position, initialPose.rotation, initialPoseColor);
        }
    }

    private bool ShouldDrawAttemptPose(TrackingResult result, bool mainWireframeDrawn)
    {
        if (!showAttemptPoseWireframe ||
            mainWireframeDrawn ||
            _seedPoseDebugSource == null ||
            trackingOrchestrator == null ||
            !trackingOrchestrator.IsDetectionEnabled)
        {
            return false;
        }

        return !result.IsConfirmed &&
               result.TrackedClassId >= 0 &&
               result.State != TrackingState.Error &&
               result.State != TrackingState.NotInitialized;
    }

    public bool TryBuildWireframeWorldPose(TrackingResult result, out Vector3 worldPos, out Quaternion worldRot)
    {
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        worldPos = Vector3.zero;
        worldRot = Quaternion.identity;

        if (!result.PoseValid || !TryBuildWorldPoseFromResult(result, out worldPos, out worldRot))
        {
            LastPoseConversionMs = TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
            return false;
        }

        // SRT3D output is in the "template body" frame, which differs from the .obj's natural
        // mesh frame by TemplateFrameCorrection. Apply the inverse so callers get the same pose
        // that this overlay renders.
        if (_seedPoseDebugSource != null &&
            _seedPoseDebugSource.TryGetTemplateFrameCorrectionForClass(
                result.TrackedClassId, out Quaternion templateCorrection))
        {
            worldRot = worldRot * Quaternion.Inverse(templateCorrection);
        }

        LastPoseConversionMs = TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
        return true;
    }

    private static float TicksToMilliseconds(long ticks)
    {
        return (float)(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    private bool TryBuildWorldPoseFromResult(TrackingResult result, out Vector3 worldPos, out Quaternion worldRot)
    {
        if (useFramePoseReferenceWhenAvailable &&
            poseIsInCameraSpace &&
            result.HasPoseReference)
        {
            return PoseConverter.TryBuildUnityPose(
                result.RowMajorPose16,
                flipCvYToUnity,
                translationScale,
                true,
                result.PoseReferencePosition,
                result.PoseReferenceRotation,
                out worldPos,
                out worldRot);
        }

        return PoseConverter.TryBuildUnityPose(
            result.RowMajorPose16,
            flipCvYToUnity,
            translationScale,
            poseIsInCameraSpace,
            poseIsInCameraSpace ? poseReferenceTransform : null,
            out worldPos,
            out worldRot);
    }

    private void DrawWireMeshAt(Vector3 position, Quaternion rotation, Color color)
    {
        // Support both URP (_BaseColor) and Built-in (_Color) color properties.
        _mpb.SetColor("_BaseColor", color);
        _mpb.SetColor("_Color",     color);

        Graphics.DrawMesh(
            _wireMesh,
            Matrix4x4.TRS(position, rotation, Vector3.one * meshScale),
            wireframeMaterial,
            gameObject.layer,
            null,
            0,
            _mpb);
    }

    /// <summary>Call after changing meshSource at runtime to rebuild the wire mesh.</summary>
    public void RefreshMesh() => BuildWireMesh();

    public void SetMeshSource(MeshFilter newMeshSource)
    {
        if (newMeshSource == null || newMeshSource == meshSource)
            return;

        meshSource = newMeshSource;
        BuildWireMesh();
    }

    private void OnDestroy()
    {
        if (_wireMesh != null)
            Destroy(_wireMesh);
        if (_ownsMaterial && wireframeMaterial != null)
            Destroy(wireframeMaterial);
    }
}
