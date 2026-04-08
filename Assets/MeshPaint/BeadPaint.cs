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
    [SerializeField] float _brushRadius = 0.5f;
    
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

    int VoxelCount => _dimensions.x * _dimensions.y * _dimensions.z;

    void Start()
    {
        // Cache default transform
        _defaultParent = transform.parent;
        _defaultLocalPosition = transform.localPosition;
        _defaultLocalRotation = transform.localRotation;
        _defaultLocalScale = transform.localScale;

        _voxelBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _timeBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _builder = new MeshBuilder(_dimensions, _triangleBudget, _builderCompute);

        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();

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

        BlendSphereSDF(localPos, finalRadius, Time.time);
        _builder.BuildIsosurface(_voxelBuffer, _timeBuffer, _targetValue, _gridScale);
        _meshFilter.sharedMesh = _builder.Mesh;
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
                    
                    if (sdf > data[idx])
                    {
                        data[idx] = sdf;
                        timeData[idx] = currentTime;
                    }
                    idx++;
                }
            }
        }

        _voxelBuffer.SetData(data);
        _timeBuffer.SetData(timeData);
    }

    void InitializeEmptyField()
    {
        var data = new float[VoxelCount];
        var timeData = new float[VoxelCount];
        for (int i = 0; i < VoxelCount; ++i) 
        {
            data[i] = -1e3f;
            timeData[i] = 0f;
        }
        _voxelBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _timeBuffer = new ComputeBuffer(VoxelCount, sizeof(float));
        _voxelBuffer.SetData(data);
        _timeBuffer.SetData(timeData);
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
        
        // Store reference
        _storedWeldBeads.Add(weldBead);
        
        Debug.Log($"BeadPaint: Saved WeldBead_{_weldCounter} with {currentMesh.vertexCount} vertices");
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
            if (weldTip != null)
            {
                _lastPaintPos = transform.InverseTransformPoint(weldTip.position);
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
}