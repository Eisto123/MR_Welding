using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DrawMesh : MonoBehaviour
{
    private bool isDrawing = false;
    private Mesh currentMesh;
    private GameObject currentWeldObject;
    public Transform drawPoint;
    private Vector3 lastPosition;
    public float lineWidth = 0.01f;
    public float minDistance = 0.005f;
    
    [Header("Capsule Settings")]
    public int radialSegments = 8; // Number of sides around the cylinder
    public bool addEndCaps = true; // Whether to close the ends
    
    [Header("Weld Management")]
    [Tooltip("Material to apply to weld beads")]
    public Material weldMaterial;
    [Tooltip("Parent object to organize weld beads under")]
    public Transform weldParent;
    [Tooltip("Maximum number of weld beads to keep (0 = unlimited)")]
    public int maxWeldBeads = 10;
    
    // Current mesh data
    private List<Vector3> allVertices = new List<Vector3>();
    private List<Vector2> allUVs = new List<Vector2>();
    private List<int> allTriangles = new List<int>();
    private float totalLength = 0f;
    
    // Weld management
    private List<GameObject> storedWeldBeads = new List<GameObject>();
    private int weldCounter = 0;

    void Start()
    {
        // Create weld parent if not assigned
        if (weldParent == null)
        {
            GameObject parentObj = new GameObject("WeldBeads");
            weldParent = parentObj.transform;
        }
    }

    private void CreateNewWeldObject()
    {
        // Create new GameObject for this weld
        weldCounter++;
        GameObject newWeldObject = new GameObject($"WeldBead_{weldCounter}");
        
        // Set parent
        if (weldParent != null)
        {
            newWeldObject.transform.SetParent(weldParent);
        }
        
        // Add components
        MeshFilter meshFilter = newWeldObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = newWeldObject.AddComponent<MeshRenderer>();
        
        // Create new mesh
        currentMesh = new Mesh();
        currentMesh.name = $"WeldMesh_{weldCounter}";
        meshFilter.mesh = currentMesh;
        
        // Apply material
        if (weldMaterial != null)
        {
            meshRenderer.material = weldMaterial;
        }
        else
        {
            // Create default material if none provided
            Material defaultMaterial = new Material(Shader.Find("Standard"));
            defaultMaterial.color = Color.yellow;
            defaultMaterial.SetFloat("_Metallic", 0.8f);
            defaultMaterial.SetFloat("_Smoothness", 0.6f);
            defaultMaterial.EnableKeyword("_EMISSION");
            defaultMaterial.SetColor("_EmissionColor", Color.yellow * 0.5f);
            meshRenderer.material = defaultMaterial;
        }
        
        // Store reference
        currentWeldObject = newWeldObject;
        storedWeldBeads.Add(newWeldObject);
        
        // Manage max weld beads limit
        if (maxWeldBeads > 0 && storedWeldBeads.Count > maxWeldBeads)
        {
            GameObject oldestWeld = storedWeldBeads[0];
            storedWeldBeads.RemoveAt(0);
            if (oldestWeld != null)
            {
                DestroyImmediate(oldestWeld);
            }
        }
        
        Debug.Log($"Created new weld bead: {newWeldObject.name}");
    }

    private void CreateMesh()
    {
        // Create new weld object for this drawing session
        CreateNewWeldObject();
        
        // Clear previous data
        allVertices.Clear();
        allUVs.Clear();
        allTriangles.Clear();
        totalLength = 0f;
        
        // Create initial ring of vertices at the starting position
        CreateInitialRing();
        
        // Update the mesh immediately
        UpdateMeshData();
        
        lastPosition = drawPoint.position;
    }

    private void CreateInitialRing()
    {
        Vector3 center = drawPoint.position;
        Vector3 forward = drawPoint.forward;
        Vector3 right = drawPoint.right;
        Vector3 up = drawPoint.up;
        
        // Create a ring of vertices around the starting point
        for (int i = 0; i < radialSegments; i++)
        {
            float angle = (float)i / radialSegments * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * lineWidth;
            float y = Mathf.Sin(angle) * lineWidth;
            
            Vector3 offset = right * x + up * y;
            Vector3 vertex = center + offset;
            
            allVertices.Add(vertex);
            allUVs.Add(new Vector2((float)i / radialSegments, 0f));
        }
        
        // Add center vertex for end cap if needed
        if (addEndCaps)
        {
            allVertices.Add(center);
            allUVs.Add(new Vector2(0.5f, 0.5f));
            
            // Create start cap
            CreateEndCap(0, center, true);
        }
    }

    private void UpdateMesh()
    {
        if (currentMesh == null || currentWeldObject == null) return;
        
        float distance = Vector3.Distance(lastPosition, drawPoint.position);
        if (distance < minDistance)
        {
            return;
        }
        
        totalLength += distance;
        
        // Calculate direction and orientation
        Vector3 direction = (drawPoint.position - lastPosition).normalized;
        Vector3 right = Vector3.Cross(direction, Vector3.up).normalized;
        Vector3 up = Vector3.Cross(right, direction).normalized;
        
        // If direction is too close to up vector, use forward as reference
        if (Vector3.Dot(direction, Vector3.up) > 0.9f)
        {
            right = Vector3.Cross(direction, Vector3.forward).normalized;
            up = Vector3.Cross(right, direction).normalized;
        }
        
        int previousRingStart = allVertices.Count - radialSegments - (addEndCaps ? 1 : 0);
        int currentRingStart = allVertices.Count;
        
        // Create new ring of vertices at current position
        for (int i = 0; i < radialSegments; i++)
        {
            float angle = (float)i / radialSegments * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * lineWidth;
            float y = Mathf.Sin(angle) * lineWidth;
            
            Vector3 offset = right * x + up * y;
            Vector3 vertex = drawPoint.position + offset;
            
            allVertices.Add(vertex);
            allUVs.Add(new Vector2((float)i / radialSegments, totalLength / lineWidth));
        }
        
        // Create triangles connecting the rings
        CreateRingConnection(previousRingStart, currentRingStart);
        
        // Update mesh
        UpdateMeshData();
        
        lastPosition = drawPoint.position;
    }
    
    private void CreateRingConnection(int prevRingStart, int currRingStart)
    {
        for (int i = 0; i < radialSegments; i++)
        {
            int next = (i + 1) % radialSegments;
            
            // Current ring indices
            int curr0 = currRingStart + i;
            int curr1 = currRingStart + next;
            
            // Previous ring indices
            int prev0 = prevRingStart + i;
            int prev1 = prevRingStart + next;
            
            // Create two triangles for each quad
            // Triangle 1
            allTriangles.Add(prev0);
            allTriangles.Add(curr0);
            allTriangles.Add(prev1);
            
            // Triangle 2
            allTriangles.Add(prev1);
            allTriangles.Add(curr0);
            allTriangles.Add(curr1);
        }
    }
    
    private void CreateEndCap(int ringStart, Vector3 center, bool isStart)
    {
        if (!addEndCaps) return;
        
        int centerIndex = allVertices.Count;
        allVertices.Add(center);
        allUVs.Add(new Vector2(0.5f, 0.5f));
        
        for (int i = 0; i < radialSegments; i++)
        {
            int next = (i + 1) % radialSegments;
            
            if (isStart)
            {
                // Start cap (facing backwards)
                allTriangles.Add(centerIndex);
                allTriangles.Add(ringStart + i);
                allTriangles.Add(ringStart + next);
            }
            else
            {
                // End cap (facing forwards)
                allTriangles.Add(centerIndex);
                allTriangles.Add(ringStart + next);
                allTriangles.Add(ringStart + i);
            }
        }
    }
    
    private void UpdateMeshData()
    {
        if (currentMesh == null) return;
        
        currentMesh.Clear();
        currentMesh.vertices = allVertices.ToArray();
        currentMesh.uv = allUVs.ToArray();
        currentMesh.triangles = allTriangles.ToArray();
        
        // Calculate normals for proper lighting
        currentMesh.RecalculateNormals();
        currentMesh.RecalculateBounds();
        currentMesh.MarkDynamic();
    }
    
    void FixedUpdate()
    {
        if (isDrawing)
        {
            UpdateMesh();
        }
    }
    
    public void SetDrawingActive(bool isActive)
    {
        if (isActive && !isDrawing)
        {
            // Start new weld
            CreateMesh();
        }
        else if (!isActive && isDrawing)
        {
            // Finish current weld
            if (addEndCaps && allVertices.Count > 0)
            {
                // Add end cap when finishing
                int lastRingStart = allVertices.Count - radialSegments - 1; // -1 for center vertex
                CreateEndCap(lastRingStart, drawPoint.position, false);
                UpdateMeshData();
            }
            
            // Finalize the current weld object
            if (currentWeldObject != null)
            {
                // Optionally add collider to finished weld
                currentWeldObject.AddComponent<MeshCollider>().convex = true;
                
                Debug.Log($"Finished weld bead: {currentWeldObject.name} with {allVertices.Count} vertices");
            }
            
            // Clear references to current weld
            currentMesh = null;
            currentWeldObject = null;
        }
        
        isDrawing = isActive;
    }
    
    // Public methods for weld management
    public void ClearAllWelds()
    {
        foreach (GameObject weldBead in storedWeldBeads)
        {
            if (weldBead != null)
            {
                DestroyImmediate(weldBead);
            }
        }
        storedWeldBeads.Clear();
        weldCounter = 0;
        
        Debug.Log("Cleared all weld beads");
    }
    
    public void ClearLastWeld()
    {
        if (storedWeldBeads.Count > 0)
        {
            GameObject lastWeld = storedWeldBeads[storedWeldBeads.Count - 1];
            storedWeldBeads.RemoveAt(storedWeldBeads.Count - 1);
            
            if (lastWeld != null)
            {
                DestroyImmediate(lastWeld);
                Debug.Log($"Cleared last weld bead: {lastWeld.name}");
            }
        }
    }
    
    public int GetWeldCount()
    {
        return storedWeldBeads.Count;
    }
    
    public List<GameObject> GetAllWeldBeads()
    {
        return new List<GameObject>(storedWeldBeads);
    }
    
    public GameObject GetCurrentWeldBead()
    {
        return currentWeldObject;
    }
}