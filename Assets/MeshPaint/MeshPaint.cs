using UnityEngine;
using MarchingCubes;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MeshPaint : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] Vector3Int _dimensions = new Vector3Int(64, 32, 64);
    [SerializeField] float _gridScale = 4.0f / 64;
    [SerializeField] int _triangleBudget = 65536;
    [SerializeField] float _targetValue = 0f;

    [Header("Compute shaders")]
    [SerializeField] ComputeShader _builderCompute = null;

    [Header("Paint Settings")]
    [SerializeField] bool _paintWhileDragging = true;
    [SerializeField] float _brushRadius = 0.5f;
    
    [Header("Accumulation Settings")]
    [SerializeField] bool _enableAccumulation = true;
    [SerializeField] float _accumulationRate = 1.0f;
    [SerializeField] float _maxAccumulatedRadius = 2.0f;
    [SerializeField] float _accumulationDelay = 0.2f;
    [SerializeField, Range(0f, 1f)] float _movementThreshold = 0.1f;
    
    ComputeBuffer _voxelBuffer;
    ComputeBuffer _timeBuffer; // New buffer for time data
    MeshBuilder _builder;

    // Accumulation tracking
    Vector3 _lastPaintPos;
    float _holdTimer = 0f;
    float _currentAccumulatedRadius = 0f;
    bool _isHoldingAtSamePosition = false;

    int VoxelCount => _dimensions.x * _dimensions.y * _dimensions.z;

    void Start()
    {
        _voxelBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _timeBuffer = new ComputeBuffer(VoxelCount, sizeof(float)); // Initialize time buffer
        _builder = new MeshBuilder(_dimensions, _triangleBudget, _builderCompute);
        GetComponent<MeshFilter>().sharedMesh = _builder.Mesh;
        InitializeEmptyField();
    }

    void OnDestroy()
    {
        if (_voxelBuffer != null) _voxelBuffer.Dispose();
        if (_timeBuffer != null) _timeBuffer.Dispose(); // Dispose time buffer
        if (_builder != null) _builder.Dispose();
    }

    void Update()
    {
        bool doPaint = _paintWhileDragging ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0);
        
        if (!doPaint)
        {
            ResetAccumulation();
            return;
        }

        if (!TryGetMouseWorldPosition(out Vector3 worldPos)) return;

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

        // Pass current time to the blend function
        BlendSphereSDF(localPos, finalRadius, Time.time);

        // Build isosurface with time data
        _builder.BuildIsosurface(_voxelBuffer, _timeBuffer, _targetValue, _gridScale);
        GetComponent<MeshFilter>().sharedMesh = _builder.Mesh;
    }
    

    void ResetAccumulation()
    {
        _holdTimer = 0f;
        _currentAccumulatedRadius = 0f;
        _isHoldingAtSamePosition = false;
    }

    void BlendSphereSDF(Vector3 localCenter, float radius, float currentTime)
    {
        int sx = _dimensions.x;
        int sy = _dimensions.y;
        int sz = _dimensions.z;

        Vector3 gridCenter = new Vector3(sx * 0.5f, sy * 0.5f, sz * 0.5f);
        Vector3 centerVoxel = gridCenter + localCenter / _gridScale;
        
        float radiusInVoxels = radius / _gridScale;

        var data = new float[VoxelCount];
        var timeData = new float[VoxelCount];
        _voxelBuffer.GetData(data);
        _timeBuffer.GetData(timeData);

        int idx = 0;
        for (int z = 0; z < sz; ++z)
        {
            for (int y = 0; y < sy; ++y)
            {
                for (int x = 0; x < sx; ++x)
                {
                    Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    float dist = Vector3.Distance(p, centerVoxel);
                    float sdf = radiusInVoxels - dist;
                    
                    // Update voxel data
                    if (sdf > data[idx])
                    {
                        data[idx] = sdf;
                        timeData[idx] = currentTime; // Store the time when this voxel was painted
                    }
                    idx++;
                }
            }
        }

        _voxelBuffer.SetData(data);
        _timeBuffer.SetData(timeData);
    }

    bool TryGetMouseWorldPosition(out Vector3 worldPos)
    {
        worldPos = Vector3.zero;
        var cam = Camera.main;
        if (cam == null) return false;

        var ray = cam.ScreenPointToRay(Input.mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            worldPos = hit.point;
            return true;
        }

        var plane = new Plane(Vector3.up, Vector3.zero);
        if (plane.Raycast(ray, out float enter))
        {
            worldPos = ray.GetPoint(enter);
            return true;
        }

        return false;
    }

    void InitializeEmptyField()
    {
        var data = new float[VoxelCount];
        var timeData = new float[VoxelCount];
        for (int i = 0; i < VoxelCount; ++i) 
        {
            data[i] = -1e3f;
            timeData[i] = 0f; // Initialize with zero time
        }
        _voxelBuffer.SetData(data);
        _timeBuffer.SetData(timeData);
    }
}