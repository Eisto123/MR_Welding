using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

public class WeldObjectManager : MonoBehaviour
{
    [SerializeField] private GameObject obj1;
    [SerializeField] private GameObject obj2;

    private bool isConnected = false;
    private ParentConstraint parentConstraint;
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
        // Calculate local difference between obj1 and obj2 (both are children of this transform)
        Vector3 translationOffset = obj2.transform.localPosition - obj1.transform.localPosition;

        // Calculate local rotation difference
        Quaternion rotationDifference = Quaternion.Inverse(obj1.transform.localRotation) * obj2.transform.localRotation;

        // Set the offsets arrays (ParentConstraint expects arrays for multiple sources)
        parentConstraint.translationOffsets = new Vector3[] { translationOffset };
        parentConstraint.rotationOffsets = new Vector3[] { rotationDifference.eulerAngles };

        // Set at-rest values to preserve original local positions
        parentConstraint.translationAtRest = targetLocalPos;
        parentConstraint.rotationAtRest = targetLocalRot.eulerAngles;
    }



}
