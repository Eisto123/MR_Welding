using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeldingTorch : MonoBehaviour
{
    private bool isGrabbing = false;
    private BeadPaint drawMesh;
    private DataRecorder dataRecorder;
    [SerializeField] private Transform tipPoint;

    [Header("Box Cast Settings")]
    [Tooltip("Size of the box cast (width, height, depth)")]
    public Vector3 boxSize = new Vector3(0.05f, 0.05f, 0.1f);
    
    [Tooltip("Distance of the box cast from the tip")]
    public float castDistance = 0.2f;
    
    [Tooltip("Local offset from tip point")]
    public Vector3 localOffset = Vector3.zero;
    
    [Tooltip("Layers to detect with box cast")]
    public LayerMask detectionLayers = -1;
    
    [Tooltip("Show debug visualization in scene view")]
    public bool showDebugGizmos = true;
    
    [Header("Welding Detection")]
    [Tooltip("Only allow welding when hitting these layers")]
    public LayerMask weldableLayers = -1;

    private WeldingStepType currentWeldingStepType;

    private bool isPressing = false; 

    private RaycastHit[] hitResults = new RaycastHit[10]; // Pre-allocated array for performance
    private Transform currentHitObject;
    void OnEnable()
    {
        drawMesh = FindAnyObjectByType<BeadPaint>();
        dataRecorder = FindAnyObjectByType<DataRecorder>();
    }
    public void OnGrab()
    {
        isGrabbing = true;
    }
    public void OnRelease()
    {
        isGrabbing = false;
    }

    public void UpdateCurrentStep(object step)
    {
        currentWeldingStepType = (WeldingStepType)step;
    }

    private bool PerformBoxCast()
    {
        if (tipPoint == null) return false;

        // Calculate box cast position and direction
        Vector3 castOrigin = GetBoxCastOrigin();
        Vector3 castDirection = GetBoxCastDirection();

        // Perform the box cast
        int hitCount = Physics.BoxCastNonAlloc(
            castOrigin,
            boxSize * 0.5f, // BoxCast uses half-extents
            castDirection,
            hitResults,
            tipPoint.rotation,
            castDistance,
            detectionLayers
        );

        // Process hit results
        return ProcessHitResults(hitCount);
    }

    private Vector3 GetBoxCastOrigin()
    {
        // Start the cast from the tip point plus local offset
        return tipPoint.position + tipPoint.TransformDirection(localOffset);
    }

    private Vector3 GetBoxCastDirection()
    {
        // Cast in the forward direction of the tip point
        return tipPoint.forward;
    }

    private bool ProcessHitResults(int hitCount)
    {
        currentHitObject = null;
        bool weldableHit = false;
        int ironHitCount = 0;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = hitResults[i];

            // Skip if hit object is the torch itself
            if (hit.collider.transform.IsChildOf(transform))
                continue;

            // Check if hit object is weldable
            if (IsWeldableLayer(hit.collider.gameObject.layer))
            {
                currentHitObject = hit.collider.transform;
                weldableHit = true;
                ironHitCount++;
            }
            if (ironHitCount >= 2)
            {
                PerformConnection(currentHitObject);
                break;
            }
        }
        return weldableHit;
    }
    
    private void PerformConnection(Transform obj)
    {
        if (obj == null) return;
        obj.parent.GetComponent<WeldObjectManager>()?.ConnectWeldObjects();
    }

    private bool IsWeldableLayer(int layer)
    {
        return (weldableLayers.value & (1 << layer)) != 0;
    }

    void FixedUpdate()
    {
        if (isPressing)
        {
            if (currentWeldingStepType != WeldingStepType.Tacking) return;

            if (PerformBoxCast())
            {
                if(!drawMesh.isDrawing)
                drawMesh.SetDrawingActive(true);
                dataRecorder.StartRecording();
            }
            else
            {
                if(drawMesh.isDrawing)
                drawMesh.SetDrawingActive(false);
                dataRecorder.StopRecording();
            }
        }
        else
        {
            if(drawMesh.isDrawing)
            drawMesh.SetDrawingActive(false);
        }
    }

    public void StartWelding()
    {
        if (!isGrabbing) return;
        isPressing = true;

    }

    public void StopWelding()
    {
        if (!isGrabbing)
        {
            isPressing = false;
            return;
        }
        isPressing = false;
        drawMesh.SetDrawingActive(false);
    }
    
    void OnDrawGizmos()
    {
        if (!showDebugGizmos || tipPoint == null) return;
        
        // Draw the box cast
        Vector3 castOrigin = GetBoxCastOrigin();
        Vector3 castDirection = GetBoxCastDirection();
        
        // Draw box at origin
        Gizmos.matrix = Matrix4x4.TRS(castOrigin, tipPoint.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boxSize);
        
        // Draw box at end of cast
        Vector3 endPosition = castOrigin + castDirection * castDistance;
        Gizmos.matrix = Matrix4x4.TRS(endPosition, tipPoint.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boxSize);
        
        // Draw line connecting them
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.DrawLine(castOrigin, endPosition);
        
        // Draw hit points
        if (Application.isPlaying)
        {
            Gizmos.color = Color.yellow;
            for (int i = 0; i < hitResults.Length; i++)
            {
                if (hitResults[i].collider != null)
                {
                    Gizmos.DrawSphere(hitResults[i].point, 0.01f);
                }
            }
        }
    }

    void OnDrawGizmosSelected()
    {
        if (tipPoint == null) return;
        
        // Draw additional debug info when selected
        Gizmos.color = Color.blue;
        Vector3 tipPos = tipPoint.position;
        Vector3 offsetPos = tipPos + tipPoint.TransformDirection(localOffset);
        
        // Draw tip point
        Gizmos.DrawSphere(tipPos, 0.005f);
        
        // Draw offset position
        Gizmos.DrawSphere(offsetPos, 0.003f);
        Gizmos.DrawLine(tipPos, offsetPos);
        
        // Draw forward direction
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(offsetPos, tipPoint.forward * castDistance);
    }
    

}
