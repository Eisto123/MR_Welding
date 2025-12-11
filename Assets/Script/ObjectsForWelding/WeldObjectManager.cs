using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Events;

public class WeldObjectManager : MonoBehaviour
{
    [SerializeField] private GameObject obj1;
    [SerializeField] private GameObject obj2;

    public Material Transparent;
    public Material Solid;
    public Transform bead;
    public ObjectEventSO instantiateCompleteEvent;
    private bool isConnected = false;
    private ParentConstraint parentConstraint;

    private void OnEnable()
    {
        instantiateCompleteEvent.RaiseEvent(bead, this);
    }
    
    public void ConnectWeldObjects()
    {
        if (obj1 == null || obj2 == null) return;
        if (isConnected) return;
        Vector3 originalLocalPos = obj2.transform.localPosition;
        Quaternion originalLocalRot = obj2.transform.localRotation;

        // Add and setup constraint
        parentConstraint = obj2.AddComponent<ParentConstraint>();

        ConstraintSource source = new ConstraintSource()
        {
            sourceTransform = obj1.transform,
            weight = 1f
        };
        parentConstraint.AddSource(source);

        CalculateAndSetOffsets(originalLocalPos, originalLocalRot);

        // Lock the constraint
        parentConstraint.locked = true;
        parentConstraint.constraintActive = true;
        isConnected = true;

        RecalculateColliderAndRigidbody();
        Debug.Log($"Connected {obj2.name} to {obj1.name} preserving position");
    }

    private void RecalculateColliderAndRigidbody()
    {
        // Disable obj2's collider and rigidbody
        obj2.GetComponent<Collider>().enabled = false;
        Rigidbody rb = obj2.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
        }

        // Expand obj1's BoxCollider to cover both objects
        ExpandBoxColliderToCoverBothObjects();
    }


    private void ExpandBoxColliderToCoverBothObjects()
    {
        BoxCollider obj1Collider = obj1.GetComponent<BoxCollider>();
        BoxCollider obj2Collider = obj2.GetComponent<BoxCollider>();

        if (obj1Collider == null || obj2Collider == null) return;

        // Calculate combined bounds in world space
        Bounds obj1Bounds = obj1Collider.bounds;
        Bounds obj2Bounds = obj2Collider.bounds;

        // Create combined bounds that encompass both objects
        Bounds combinedBounds = obj1Bounds;
        combinedBounds.Encapsulate(obj2Bounds);

        // Convert world bounds to obj1's local space
        Transform obj1Transform = obj1.transform;
        Vector3 localCenter = obj1Transform.InverseTransformPoint(combinedBounds.center);

        // Calculate size in local space (accounting for obj1's scale)
        Vector3 localSize = new Vector3(
            combinedBounds.size.x / obj1Transform.lossyScale.x,
            combinedBounds.size.y / obj1Transform.lossyScale.y,
            combinedBounds.size.z / obj1Transform.lossyScale.z
        );

        // Apply to obj1's BoxCollider
        obj1Collider.center = localCenter;
        obj1Collider.size = localSize;

        Debug.Log($"Expanded obj1 BoxCollider - Center: {localCenter}, Size: {localSize}");
    }

    private void CalculateAndSetOffsets(Vector3 targetLocalPos, Quaternion targetLocalRot)
    {
        // Store obj2's current world position and rotation before constraint is applied
        Vector3 obj2WorldPos = obj2.transform.position;
        Quaternion obj2WorldRot = obj2.transform.rotation;

        // Calculate the offset from obj1 to obj2 in obj1's local space
        Vector3 worldOffset = obj2WorldPos - obj1.transform.position;
        Vector3 positionOffset = obj1.transform.InverseTransformDirection(worldOffset);
    

        // Calculate rotation offset (how much obj2 should differ from obj1's rotation)
        Quaternion rotationOffset = Quaternion.Inverse(obj1.transform.rotation) * obj2WorldRot;

        // Set the offsets arrays (ParentConstraint expects arrays for multiple sources)
        parentConstraint.translationOffsets = new Vector3[] { positionOffset };
        parentConstraint.rotationOffsets = new Vector3[] { rotationOffset.eulerAngles };

        // At-rest values: where obj2 should be when constraint weight = 0
        // These should be obj2's current local position/rotation relative to its current parent
        parentConstraint.translationAtRest = targetLocalPos;
        parentConstraint.rotationAtRest = targetLocalRot.eulerAngles;

        Debug.Log($"Position Offset: {positionOffset}");
        Debug.Log($"Rotation Offset: {rotationOffset.eulerAngles}");
        Debug.Log($"Translation At Rest: {targetLocalPos}");
        Debug.Log($"Rotation At Rest: {targetLocalRot.eulerAngles}");
    }

    public void SetObjectsTransparent(bool transparent)
    {
        Material targetMaterial = transparent ? Transparent : Solid;

        if (obj1 != null)
        {
            Renderer renderer1 = obj1.GetComponent<Renderer>();
            if (renderer1 != null)
            {
                renderer1.material = targetMaterial;
            }
        }

        if (obj2 != null)
        {
            Renderer renderer2 = obj2.GetComponent<Renderer>();
            if (renderer2 != null)
            {
                renderer2.material = targetMaterial;
            }
        }
    }




}
