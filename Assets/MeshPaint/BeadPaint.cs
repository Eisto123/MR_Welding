using UnityEngine;
using MarchingCubes;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class BeadPaint : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] Vector3Int _dimensions = new Vector3Int(64, 32, 64);
    [SerializeField] float _gridScale = 1.0f / 64;
    [SerializeField] int _triangleBudget = 65536;
    [SerializeField] float _targetValue = 0f;

    [Header("Compute shaders")]
    [SerializeField] ComputeShader _builderCompute = null;

    [Header("Paint Settings")]
    [SerializeField] public Transform weldTip;
    [SerializeField] DataRecorder _dataRecorder;
    [SerializeField] float _brushRadius = 0.5f;

    [Header("Crescent Erosion")]
    [SerializeField] bool _enableCrescentErosion = true;
    [SerializeField] float _scaleSpacing = 0.08f;
    [SerializeField, Range(0f, 1f)] float _erosionDepth = 0.16f;
    [SerializeField, Range(0.02f, 0.5f)] float _grooveWidth = 0.14f;
    [SerializeField, Range(0f, 2f)] float _crescentCurve = 0.75f;
    [SerializeField, Range(0.25f, 2f)] float _erosionLengthScale = 1.2f;
    [SerializeField, Range(0.25f, 1.5f)] float _erosionWidthScale = 0.9f;
    [SerializeField, Range(1, 8)] int _maxErosionStampsPerFrame = 3;

    [Header("Debug Field Visualization")]
    [SerializeField] bool _debugDrawFieldPoints = false;
    [SerializeField] DebugFieldSource _debugFieldSource = DebugFieldSource.Final;
    [SerializeField] bool _debugUseLastSavedField = true;
    [SerializeField, Range(1, 8)] int _debugStride = 1;
    [SerializeField] bool _debugAutoNormalize = true;
    [SerializeField] bool _debugLogFieldStats = false;
    [SerializeField] float _debugMinValue = -2f;
    [SerializeField] float _debugMaxValue = 2f;
    [SerializeField] float _debugPointScale = 0.75f;
    
    [Header("Accumulation Settings")]
    [SerializeField] bool _enableAccumulation = true;
    [SerializeField] float _accumulationRate = 1.0f;
    [SerializeField] float _maxAccumulatedRadius = 2.0f;
    [SerializeField] float _accumulationDelay = 0.2f;
    [SerializeField, Range(0f, 1f)] float _movementThreshold = 0.1f;
    
    [Header("Boundary Box Visualization")]
    [SerializeField] GameObject _boundaryBox;
    [SerializeField] bool _showBoundaryBox = true;
    
    [Header("Weld Bead Management")]
    [SerializeField] Material _weldMaterial;
    [SerializeField] int _maxWeldBeads = 10;
    
    ComputeBuffer _voxelBuffer;
    ComputeBuffer _timeBuffer;
    MeshBuilder _builder;

    // Accumulation tracking
    Vector3 _lastPaintPos;
    float _holdTimer = 0f;
    float _currentAccumulatedRadius = 0f;
    bool _isHoldingAtSamePosition = false;
    float _fallbackTravelDistance = 0f;
    float _motionTravelStartDistance = 0f;
    float _lastErosionTravelDistance = 0f;
    Vector3 _lastMotionPos;
    Vector3 _lastLocalTravelDirection = Vector3.forward;
    bool _hasLastMotionPos = false;
    bool _hasMotionTravelStartDistance = false;

    // Drawing state
    public bool isDrawing = false;
    
    // Weld bead storage
    private Transform _weldParent;
    private List<GameObject> _storedWeldBeads = new List<GameObject>();
    private int _weldCounter = 0;
    
    // Current working mesh
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;

    // ALTERNATIVE: Store the builder with each bead
    private List<MeshBuilder> _storedBuilders = new List<MeshBuilder>();

    float[] _beadData;
    float[] _erosionData;
    float[] _voxelData;
    float[] _timeData;
    float[] _lastSavedBeadData;
    float[] _lastSavedErosionData;
    float[] _lastSavedVoxelData;
    float _nextDebugStatsLogTime = 0f;

    int VoxelCount => _dimensions.x * _dimensions.y * _dimensions.z;

    enum DebugFieldSource
    {
        Final,
        Bead,
        Erosion
    }

    struct PaintMotionSample
    {
        public Vector3 localDirection;
        public float travelDistance;
        public float speed;
    }

    void Start()
    {
        // Cache default transform
        _defaultParent = transform.parent;
        _defaultLocalPosition = transform.localPosition;
        _defaultLocalRotation = transform.localRotation;
        _defaultLocalScale = transform.localScale;

        _builder = new MeshBuilder(_dimensions, _triangleBudget, _builderCompute);

        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        if (_dataRecorder == null)
        {
            _dataRecorder = FindAnyObjectByType<DataRecorder>();
        }

        if (_weldMaterial != null)
        {
            _meshRenderer.sharedMaterial = _weldMaterial; // ensure runtime mesh uses correct shader/material
        }

        _meshFilter.sharedMesh = _builder.Mesh;

        InitializeEmptyField();
        UpdateBoundaryBox();
    }

    void OnDestroy()
    {
        if (_voxelBuffer != null) _voxelBuffer.Dispose();
        if (_timeBuffer != null) _timeBuffer.Dispose();
        if (_builder != null) _builder.Dispose();
        foreach (MeshBuilder builder in _storedBuilders)
        {
            if (builder != null)
            {
                builder.Dispose();
            }
        }
    }

    void Update()
    {
        if (!isDrawing)
        {
            ResetAccumulation();
            return;
        }

        if (weldTip == null)
        {
            Debug.LogWarning("BeadPaint: weldTip not assigned!");
            return;
        }

        Vector3 worldPos = weldTip.position;
        Vector3 localPos = transform.InverseTransformPoint(worldPos);

        float moveDist = Vector3.Distance(localPos, _lastPaintPos);
        bool movedSignificantly = moveDist > _brushRadius * _movementThreshold;

        if (movedSignificantly || !_isHoldingAtSamePosition)
        {
            _lastPaintPos = localPos;
            _holdTimer = 0f;
            _currentAccumulatedRadius = 0f;
            _isHoldingAtSamePosition = true;
        }
        else if (_enableAccumulation)
        {
            _holdTimer += Time.deltaTime;
            
            if (_holdTimer > _accumulationDelay)
            {
                float accumulationTime = _holdTimer - _accumulationDelay;
                _currentAccumulatedRadius = Mathf.Min(
                    accumulationTime * _accumulationRate,
                    _maxAccumulatedRadius
                );
            }
        }

        float finalRadius = _brushRadius + _currentAccumulatedRadius;
        PaintMotionSample motionSample = CreatePaintMotionSample(localPos);

        bool fieldChanged = BlendSphereSDF(localPos, finalRadius, Time.time);
        fieldChanged |= StampPendingCrescentErosions(localPos, finalRadius, motionSample);

        if (fieldChanged)
        {
            _builder.BuildIsosurface(_voxelBuffer, _timeBuffer, _targetValue, _gridScale);
            _meshFilter.sharedMesh = _builder.Mesh;
        }
    }

    void ResetAccumulation()
    {
        _holdTimer = 0f;
        _currentAccumulatedRadius = 0f;
        _isHoldingAtSamePosition = false;
    }

    void ResetCrescentTracking()
    {
        _fallbackTravelDistance = 0f;
        _motionTravelStartDistance = 0f;
        _lastErosionTravelDistance = 0f;
        _lastLocalTravelDirection = Vector3.forward;
        _hasLastMotionPos = false;
        _hasMotionTravelStartDistance = false;
    }

    PaintMotionSample CreatePaintMotionSample(Vector3 localPos)
    {
        if (_hasLastMotionPos)
        {
            Vector3 delta = localPos - _lastMotionPos;
            float distance = delta.magnitude;
            if (distance > Mathf.Epsilon)
            {
                _fallbackTravelDistance += distance;
                _lastLocalTravelDirection = delta / distance;
            }
        }
        else
        {
            _hasLastMotionPos = true;
        }

        _lastMotionPos = localPos;

        PaintMotionSample sample = new PaintMotionSample
        {
            localDirection = _lastLocalTravelDirection,
            travelDistance = _fallbackTravelDistance,
            speed = Time.deltaTime > Mathf.Epsilon ? Vector3.Distance(localPos, _lastPaintPos) / Time.deltaTime : 0f
        };

        if (_dataRecorder != null && _dataRecorder.TryGetLatestSample(out WeldingMotionSample recorderSample))
        {
            if (!_hasMotionTravelStartDistance)
            {
                _motionTravelStartDistance = recorderSample.travelDistance;
                _hasMotionTravelStartDistance = true;
            }

            Vector3 localDirection = transform.InverseTransformDirection(recorderSample.travelDirection);
            if (localDirection.sqrMagnitude > Mathf.Epsilon)
            {
                sample.localDirection = localDirection.normalized;
            }

            float scale = Mathf.Max(0.0001f, (transform.lossyScale.x + transform.lossyScale.y + transform.lossyScale.z) / 3f);
            sample.travelDistance = Mathf.Max(0f, (recorderSample.travelDistance - _motionTravelStartDistance) / scale);
            sample.speed = recorderSample.speed / scale;
        }

        if (sample.localDirection.sqrMagnitude <= Mathf.Epsilon)
        {
            sample.localDirection = Vector3.forward;
        }

        return sample;
    }

    bool BlendSphereSDF(Vector3 localCenter, float radius, float currentTime)
    {
        int sx = _dimensions.x;
        int sy = _dimensions.y;
        int sz = _dimensions.z;

        Vector3 gridCenter = new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f);
        Vector3 centerVoxel = gridCenter + localCenter / _gridScale;
        
        float radiusInVoxels = radius / _gridScale;
        int minX = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.x - radiusInVoxels - 1f));
        int maxX = Mathf.Min(sx - 1, Mathf.CeilToInt(centerVoxel.x + radiusInVoxels + 1f));
        int minY = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.y - radiusInVoxels - 1f));
        int maxY = Mathf.Min(sy - 1, Mathf.CeilToInt(centerVoxel.y + radiusInVoxels + 1f));
        int minZ = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.z - radiusInVoxels - 1f));
        int maxZ = Mathf.Min(sz - 1, Mathf.CeilToInt(centerVoxel.z + radiusInVoxels + 1f));

        bool changed = false;
        for (int z = minZ; z <= maxZ; ++z)
        {
            for (int y = minY; y <= maxY; ++y)
            {
                for (int x = minX; x <= maxX; ++x)
                {
                    Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    float dist = Vector3.Distance(p, centerVoxel);
                    float sdf = radiusInVoxels - dist;
                    int idx = ToIndex(x, y, z);
                    
                    if (sdf > _beadData[idx])
                    {
                        _beadData[idx] = sdf;
                        _voxelData[idx] = _beadData[idx] - _erosionData[idx];
                        _timeData[idx] = currentTime;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            UploadDirtyBounds(minX, maxX, minY, maxY, minZ, maxZ, true);
        }

        return changed;
    }

    bool StampPendingCrescentErosions(Vector3 localPos, float radius, PaintMotionSample motionSample)
    {
        if (!_enableCrescentErosion || _scaleSpacing <= Mathf.Epsilon)
        {
            return false;
        }

        bool changed = false;
        int stampCount = 0;

        while (motionSample.travelDistance - _lastErosionTravelDistance >= _scaleSpacing &&
               stampCount < _maxErosionStampsPerFrame)
        {
            float stampTravel = _lastErosionTravelDistance + _scaleSpacing;
            float backtrackDistance = Mathf.Max(0f, motionSample.travelDistance - stampTravel);
            Vector3 stampCenter = localPos - motionSample.localDirection.normalized * backtrackDistance;

            changed |= StampCrescentErosion(stampCenter, motionSample.localDirection, radius);
            _lastErosionTravelDistance = stampTravel;
            stampCount++;
        }

        return changed;
    }

    bool StampCrescentErosion(Vector3 localCenter, Vector3 localDirection, float radius)
    {
        int sx = _dimensions.x;
        int sy = _dimensions.y;
        int sz = _dimensions.z;

        Vector3 travelAxis = localDirection.sqrMagnitude > Mathf.Epsilon ? localDirection.normalized : Vector3.forward;
        Vector3 lateralAxis = Vector3.Cross(Vector3.up, travelAxis);
        if (lateralAxis.sqrMagnitude <= 0.0001f)
        {
            lateralAxis = Vector3.Cross(Vector3.right, travelAxis);
        }
        lateralAxis.Normalize();
        Vector3 heightAxis = Vector3.Cross(travelAxis, lateralAxis).normalized;

        Vector3 gridCenter = new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f);
        Vector3 centerVoxel = gridCenter + localCenter / _gridScale;
        float radiusInVoxels = radius / _gridScale;
        float halfLength = Mathf.Max(1f, radiusInVoxels * _erosionLengthScale);
        float halfWidth = Mathf.Max(1f, radiusInVoxels * _erosionWidthScale);
        float grooveWidthVoxels = Mathf.Max(0.75f, radiusInVoxels * _grooveWidth);
        float depthVoxels = radiusInVoxels * _erosionDepth;
        float extent = Mathf.Max(halfLength, halfWidth) + grooveWidthVoxels + 2f;

        int minX = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.x - extent));
        int maxX = Mathf.Min(sx - 1, Mathf.CeilToInt(centerVoxel.x + extent));
        int minY = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.y - radiusInVoxels - 1f));
        int maxY = Mathf.Min(sy - 1, Mathf.CeilToInt(centerVoxel.y + radiusInVoxels + 1f));
        int minZ = Mathf.Max(0, Mathf.FloorToInt(centerVoxel.z - extent));
        int maxZ = Mathf.Min(sz - 1, Mathf.CeilToInt(centerVoxel.z + extent));

        bool changed = false;
        for (int z = minZ; z <= maxZ; ++z)
        {
            for (int y = minY; y <= maxY; ++y)
            {
                for (int x = minX; x <= maxX; ++x)
                {
                    Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    Vector3 offset = p - centerVoxel;
                    float longitudinal = Vector3.Dot(offset, travelAxis);
                    float lateral = Vector3.Dot(offset, lateralAxis);
                    float height = Vector3.Dot(offset, heightAxis);
                    float lateralNormalized = lateral / halfWidth;

                    if (Mathf.Abs(lateralNormalized) > 1f || Mathf.Abs(longitudinal) > halfLength)
                    {
                        continue;
                    }

                    float crescentCenter = _crescentCurve * lateralNormalized * lateralNormalized * halfLength * 0.55f;
                    float grooveDistance = Mathf.Abs(longitudinal - crescentCenter);
                    if (grooveDistance > grooveWidthVoxels)
                    {
                        continue;
                    }

                    float grooveMask = 1f - Smooth01(grooveDistance / grooveWidthVoxels);
                    float lateralMask = 1f - Mathf.Abs(lateralNormalized);
                    lateralMask *= lateralMask;

                    float topMask = Mathf.Clamp01((height + radiusInVoxels * 0.35f) / Mathf.Max(0.0001f, radiusInVoxels * 0.9f));
                    float erosion = depthVoxels * grooveMask * lateralMask * topMask;
                    if (erosion < 0f)
                    {
                        continue;
                    }

                    int idx = ToIndex(x, y, z);
                    if (erosion > _erosionData[idx])
                    {
                        _erosionData[idx] = erosion;
                        _voxelData[idx] = _beadData[idx] - _erosionData[idx];
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            UploadDirtyBounds(minX, maxX, minY, maxY, minZ, maxZ, false);
        }

        return changed;
    }

    float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    int ToIndex(int x, int y, int z)
    {
        return x + _dimensions.x * (y + _dimensions.y * z);
    }

    void UploadDirtyBounds(int minX, int maxX, int minY, int maxY, int minZ, int maxZ, bool uploadTime)
    {
        int rowLength = maxX - minX + 1;
        if (rowLength <= 0) return;

        for (int z = minZ; z <= maxZ; ++z)
        {
            for (int y = minY; y <= maxY; ++y)
            {
                int start = ToIndex(minX, y, z);
                _voxelBuffer.SetData(_voxelData, start, start, rowLength);
                if (uploadTime)
                {
                    _timeBuffer.SetData(_timeData, start, start, rowLength);
                }
            }
        }
    }

    void InitializeEmptyField()
    {
        if (_voxelBuffer != null) _voxelBuffer.Dispose();
        if (_timeBuffer != null) _timeBuffer.Dispose();

        _beadData = new float[VoxelCount];
        _erosionData = new float[VoxelCount];
        _voxelData = new float[VoxelCount];
        _timeData = new float[VoxelCount];
        for (int i = 0; i < VoxelCount; ++i) 
        {
            _beadData[i] = -1e3f;
            _erosionData[i] = 0f;
            _voxelData[i] = -1e3f;
            _timeData[i] = 0f;
        }
        _voxelBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _timeBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _voxelBuffer.SetData(_voxelData);
        _timeBuffer.SetData(_timeData);
    }

    void UpdateBoundaryBox()
    {
        if (_boundaryBox == null) return;

        Vector3 gridSize = new Vector3(
            _dimensions.x * _gridScale,
            _dimensions.y * _gridScale,
            _dimensions.z * _gridScale
        );

        _boundaryBox.transform.position = transform.position;
        _boundaryBox.transform.rotation = transform.rotation;
        _boundaryBox.transform.localScale = gridSize;
        _boundaryBox.SetActive(_showBoundaryBox);
    }

    // FIXED: Create independent mesh copy
    void SaveAsWeldBead()
    {
        Mesh currentMesh = _meshFilter.sharedMesh;
        
        if (currentMesh == null || currentMesh.vertexCount == 0)
        {
            Debug.LogWarning("BeadPaint: No geometry to save - skipping weld bead creation");
            return;
        }

        // Create a duplicate of this GameObject
        _weldCounter++;
        GameObject weldBead = new GameObject($"WeldBead_{_weldCounter}");
        
        // Set parent
        if (_weldParent != null)
        {
            weldBead.transform.SetParent(_weldParent);
        }
        
        // Copy transform
        weldBead.transform.position = transform.position;
        weldBead.transform.rotation = transform.rotation;
        weldBead.transform.localScale = transform.localScale;
        
        // Add MeshFilter - use the CURRENT builder's mesh
        MeshFilter newMeshFilter = weldBead.AddComponent<MeshFilter>();
        newMeshFilter.sharedMesh = currentMesh; // Use the current mesh
    
        // Add MeshRenderer with material
        MeshRenderer newMeshRenderer = weldBead.AddComponent<MeshRenderer>();
        if (_weldMaterial != null)
        {
            newMeshRenderer.sharedMaterial = _weldMaterial;
        }
        else
        {
            newMeshRenderer.sharedMaterial = _meshRenderer.sharedMaterial;
        }
        
        // Store the current builder (so its mesh doesn't get disposed)
        _storedBuilders.Add(_builder);
        CaptureLastSavedFieldSnapshot();
        
        // Store reference
        _storedWeldBeads.Add(weldBead);
        
        Debug.Log($"BeadPaint: Saved WeldBead_{_weldCounter} with {currentMesh.vertexCount} vertices");
    }

    void CaptureLastSavedFieldSnapshot()
    {
        if (_beadData == null || _erosionData == null || _voxelData == null)
        {
            return;
        }

        _lastSavedBeadData = (float[])_beadData.Clone();
        _lastSavedErosionData = (float[])_erosionData.Clone();
        _lastSavedVoxelData = (float[])_voxelData.Clone();
    }

    // Clear the current working mesh
    void ClearCurrentMesh()
    {
        _builder = new MeshBuilder(_dimensions, _triangleBudget, _builderCompute);
        InitializeEmptyField();
        _meshFilter.sharedMesh = _builder.Mesh;

        if (_weldMaterial != null)
        {
            _meshRenderer.sharedMaterial = _weldMaterial; // keep consistent after reset
        }

        Debug.Log("BeadPaint: Working mesh cleared with new builder");
    }

    // PUBLIC API: Set the parent transform for weld beads
    public void SetBeadParent(object parent)
    {
        Transform transformParent = parent as Transform;
        _weldParent = transformParent;
        Debug.Log($"BeadPaint: Weld parent set to {transformParent?.name ?? "null"}");
    }

    // PUBLIC API: Control painting state
    public void SetDrawingActive(bool active)
    {
        // Detect transition from true to false
        if (isDrawing && !active)
        {
            // Save the weld bead (shares mesh instance)
            SaveAsWeldBead();
            
            // Create new builder with new mesh for next weld
            ClearCurrentMesh();
        }
        
        isDrawing = active;
        
        if (active)
        {
            ResetCrescentTracking();
            if (weldTip != null)
            {
                _lastPaintPos = transform.InverseTransformPoint(weldTip.position);
                _lastMotionPos = _lastPaintPos;
                _hasLastMotionPos = true;
            }
        }
    }

    // PUBLIC API: Toggle boundary box visibility
    public void SetBoundaryBoxVisible(bool visible)
    {
        _showBoundaryBox = visible;
        if (_boundaryBox != null)
        {
            _boundaryBox.SetActive(visible);
        }
    }

    // PUBLIC API: Clear all stored weld beads
    public void ClearAllWeldBeads()
    {
        foreach (GameObject bead in _storedWeldBeads)
        {
            if (bead != null)
            {
                Destroy(bead);
            }
        }
        _storedWeldBeads.Clear();
        
        // Dispose old builders
        foreach (MeshBuilder builder in _storedBuilders)
        {
            if (builder != null)
            {
                builder.Dispose();
            }
        }
        _storedBuilders.Clear();
        
        _weldCounter = 0;
        Debug.Log("BeadPaint: Cleared all weld beads and builders");
    }

    // PUBLIC API: Get number of stored weld beads
    public int GetWeldBeadCount()
    {
        return _storedWeldBeads.Count;
    }

    // PUBLIC API: Get all stored weld beads
    public List<GameObject> GetAllWeldBeads()
    {
        return new List<GameObject>(_storedWeldBeads);
    }

    // Default transform cache
    private Transform _defaultParent;
    private Vector3 _defaultLocalPosition;
    private Quaternion _defaultLocalRotation;
    private Vector3 _defaultLocalScale;


    public void AlignToTrackedObject(Transform trackedTarget)
    {
        if (trackedTarget == null)
        {
            Debug.LogWarning("BeadPaint: Align target is null.");
            return;
        }

        transform.SetPositionAndRotation(trackedTarget.position, trackedTarget.rotation);
        Debug.Log($"BeadPaint: Aligned to tracked object '{trackedTarget.name}'");
    }

    public void ResetToDefaultAndClear()
    {
        isDrawing = false;
        ResetAccumulation();
        ResetCrescentTracking();

        // Restore transform to original default
        transform.SetParent(_defaultParent, false);
        transform.localPosition = _defaultLocalPosition;
        transform.localRotation = _defaultLocalRotation;
        transform.localScale = _defaultLocalScale;

        // Clear all stored beads/builders
        ClearAllWeldBeads();

        // Reset current working field/mesh
        if (_builder != null)
        {
            _builder.Dispose();
        }
        _builder = new MeshBuilder(_dimensions, _triangleBudget, _builderCompute);
        InitializeEmptyField();
        _meshFilter.sharedMesh = _builder.Mesh;

        Debug.Log("BeadPaint: Reset to default transform and cleared all weld beads.");
    }

    void OnValidate()
    {
        if (Application.isPlaying)
        {
            UpdateBoundaryBox();
        }
    }

    void OnDrawGizmosSelected()
    {
        if (!_debugDrawFieldPoints || !Application.isPlaying || !TryGetDebugFieldArrays(out _, out _, out _))
        {
            return;
        }

        DrawDebugFieldPoints();
    }

    void DrawDebugFieldPoints()
    {
        int sx = _dimensions.x;
        int sz = _dimensions.z;
        int y = Mathf.Clamp(Mathf.RoundToInt(_dimensions.y * 0.5f - 0.5f), 0, _dimensions.y - 1);
        int stride = Mathf.Max(1, _debugStride);
        float pointSize = Mathf.Max(0.0001f, _gridScale * _debugPointScale);
        GetDebugLayerStats(y, stride, out float minValue, out float maxValue, out int nonEmptyCount, out int nonZeroCount);

        if (!_debugAutoNormalize)
        {
            minValue = _debugMinValue;
            maxValue = _debugMaxValue;
        }

        float valueRange = maxValue - minValue;
        bool hasVisibleRange = valueRange > 0.0001f;
        valueRange = Mathf.Max(0.0001f, valueRange);

        if (_debugLogFieldStats && Time.unscaledTime >= _nextDebugStatsLogTime)
        {
            float localY = ((y + 0.5f) - _dimensions.y * 0.5f) * _gridScale;
            string fieldMode = IsUsingLastSavedDebugField() ? "last saved" : "live";
            Debug.Log(
                $"BeadPaint debug {_debugFieldSource} ({fieldMode}): local y={localY:F4}, voxel y={y}, " +
                $"min={minValue:F4}, max={maxValue:F4}, nonEmpty={nonEmptyCount}, nonZero={nonZeroCount}"
            );
            _nextDebugStatsLogTime = Time.unscaledTime + 1f;
        }

        for (int z = 0; z < sz; z += stride)
        {
            for (int x = 0; x < sx; x += stride)
            {
                float value = GetDebugFieldValue(ToIndex(x, y, z));
                float grayscale = hasVisibleRange ? Mathf.Clamp01((value - minValue) / valueRange) : 0f;
                Gizmos.color = new Color(grayscale, grayscale, grayscale, 0.9f);
                Gizmos.DrawCube(VoxelToWorldPosition(x, y, z), Vector3.one * pointSize);
            }
        }
    }

    void GetDebugLayerStats(int y, int stride, out float minValue, out float maxValue, out int nonEmptyCount, out int nonZeroCount)
    {
        TryGetDebugFieldArrays(out float[] beadData, out _, out _);

        minValue = float.PositiveInfinity;
        maxValue = float.NegativeInfinity;
        nonEmptyCount = 0;
        nonZeroCount = 0;

        for (int z = 0; z < _dimensions.z; z += stride)
        {
            for (int x = 0; x < _dimensions.x; x += stride)
            {
                int index = ToIndex(x, y, z);
                float value = GetDebugFieldValue(index);
                minValue = Mathf.Min(minValue, value);
                maxValue = Mathf.Max(maxValue, value);

                if (beadData != null && beadData[index] > -999f)
                {
                    nonEmptyCount++;
                }

                if (Mathf.Abs(value) > 0.0001f && value > -999f)
                {
                    nonZeroCount++;
                }
            }
        }

        if (float.IsInfinity(minValue) || float.IsInfinity(maxValue))
        {
            minValue = 0f;
            maxValue = 0f;
        }
    }

    float GetDebugFieldValue(int index)
    {
        if (!TryGetDebugFieldArrays(out float[] beadData, out float[] erosionData, out float[] voxelData))
        {
            return -1e3f;
        }

        return _debugFieldSource switch
        {
            DebugFieldSource.Bead => beadData[index],
            DebugFieldSource.Erosion => erosionData[index],
            _ => voxelData[index]
        };
    }

    bool TryGetDebugFieldArrays(out float[] beadData, out float[] erosionData, out float[] voxelData)
    {
        bool useLastSaved = _debugUseLastSavedField &&
                            _lastSavedBeadData != null &&
                            _lastSavedErosionData != null &&
                            _lastSavedVoxelData != null;

        beadData = useLastSaved ? _lastSavedBeadData : _beadData;
        erosionData = useLastSaved ? _lastSavedErosionData : _erosionData;
        voxelData = useLastSaved ? _lastSavedVoxelData : _voxelData;

        return beadData != null &&
               erosionData != null &&
               voxelData != null &&
               beadData.Length == VoxelCount &&
               erosionData.Length == VoxelCount &&
               voxelData.Length == VoxelCount;
    }

    bool IsUsingLastSavedDebugField()
    {
        return _debugUseLastSavedField &&
               _lastSavedBeadData != null &&
               _lastSavedErosionData != null &&
               _lastSavedVoxelData != null;
    }

    Vector3 VoxelToWorldPosition(int x, int y, int z)
    {
        Vector3 local = (new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) -
                         new Vector3(_dimensions.x, _dimensions.y, _dimensions.z) * 0.5f) * _gridScale;
        return transform.TransformPoint(local);
    }
}
