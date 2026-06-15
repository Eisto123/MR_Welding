using UnityEngine;
using UnityEngine.Rendering;

namespace MarchingCubes {

//
// Isosurface mesh builder with the marching cubes algorithm
//
sealed class MeshBuilder : System.IDisposable
{
    #region Public members

    public Mesh Mesh => _mesh;

    public MeshBuilder(int x, int y, int z, int budget, ComputeShader compute)
      => Initialize((x, y, z), budget, compute);

    public MeshBuilder(Vector3Int dims, int budget, ComputeShader compute)
      => Initialize((dims.x, dims.y, dims.z), budget, compute);

    public void Dispose()
      => ReleaseAll();

    public void BuildIsosurface(ComputeBuffer voxels, ComputeBuffer timeBuffer, float target, float scale)
      => RunCompute(voxels, timeBuffer, target, scale);

    #endregion

    #region Private members

    (int x, int y, int z) _grids;
    int _triangleBudget;
    ComputeShader _compute;

    void Initialize((int, int, int) dims, int budget, ComputeShader compute)
    {
        _grids = dims;
        _triangleBudget = budget;
        _compute = compute;

        AllocateBuffers();
        AllocateMesh(3 * _triangleBudget);
    }

    void ReleaseAll()
    {
        ReleaseBuffers();
        ReleaseMesh();
    }

    void RunCompute(ComputeBuffer voxels, ComputeBuffer timeBuffer, float target, float scale)
    {
        ClearMeshBuffers();

        _counterBuffer.SetCounterValue(0);

        // Isosurface reconstruction
        _compute.SetInts("Dims", _grids);
        _compute.SetInt("MaxTriangle", _triangleBudget);
        _compute.SetFloat("Scale", scale);
        _compute.SetFloat("Isovalue", target);
        _compute.SetBuffer(0, "TriangleTable", _triangleTable);
        _compute.SetBuffer(0, "Voxels", voxels);
        _compute.SetBuffer(0, "TimeBuffer", timeBuffer);
        _compute.SetBuffer(0, "VertexBuffer", _vertexBuffer);
        _compute.SetBuffer(0, "IndexBuffer", _indexBuffer);
        _compute.SetBuffer(0, "Counter", _counterBuffer);
        _compute.DispatchThreads(0, _grids);

        // Bounding box
        var ext = new Vector3(_grids.x, _grids.y, _grids.z) * scale;
        _mesh.bounds = new Bounds(Vector3.zero, ext);
        SetSubMeshIndexCount(3 * _triangleBudget);
    }

    void ClearMeshBuffers()
    {
        if (_compute == null || _vertexBuffer == null || _indexBuffer == null) return;

        // Clear the full mesh buffer first. This prevents stale or uninitialized
        // triangles from being rendered before/after reconstruction.
        _compute.SetInt("MaxTriangle", _triangleBudget);
        _compute.SetBuffer(1, "VertexBuffer", _vertexBuffer);
        _compute.SetBuffer(1, "IndexBuffer", _indexBuffer);
        _compute.DispatchThreads(1, _triangleBudget, 1, 1);
    }

    #endregion

    #region Compute buffer objects

    ComputeBuffer _triangleTable;
    ComputeBuffer _counterBuffer;

    void AllocateBuffers()
    {
        // Marching cubes triangle table
        _triangleTable = new ComputeBuffer(256, sizeof(ulong));
        _triangleTable.SetData(PrecalculatedData.TriangleTable);

        // Buffer for triangle counting
        _counterBuffer = new ComputeBuffer(1, 4, ComputeBufferType.Counter);
    }

    void ReleaseBuffers()
    {
        _triangleTable.Dispose();
        _counterBuffer.Dispose();
    }

    #endregion

    #region Mesh objects

    Mesh _mesh;
    GraphicsBuffer _vertexBuffer;
    GraphicsBuffer _indexBuffer;

    void AllocateMesh(int vertexCount)
    {
        _mesh = new Mesh();

        // We want GraphicsBuffer access as Raw (ByteAddress) buffers.
        _mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
        _mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

        // Vertex position: float32 x 3
        var vp = new VertexAttributeDescriptor
          (VertexAttribute.Position, VertexAttributeFormat.Float32, 3);

        // Vertex normal: float32 x 3
        var vn = new VertexAttributeDescriptor
          (VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);

        // Vertex UV2 for time data: float32 x 2 (we'll use x component for time)
        var vuv2 = new VertexAttributeDescriptor
          (VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2);

        // Vertex/index buffer formats - now includes UV2
        _mesh.SetVertexBufferParams(vertexCount, vp, vn, vuv2);
        _mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);

        // GraphicsBuffer references
        _vertexBuffer = _mesh.GetVertexBuffer(0);
        _indexBuffer = _mesh.GetIndexBuffer();

        SetSubMeshIndexCount(0);
        _mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
        ClearMeshBuffers();
    }

    void SetSubMeshIndexCount(int indexCount)
      => _mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount),
                          MeshUpdateFlags.DontRecalculateBounds);

    void ReleaseMesh()
    {
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        Object.Destroy(_mesh);
    }

    #endregion
}

} // namespace MarchingCubes
