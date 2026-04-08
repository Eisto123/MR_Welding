using UnityEngine;

public class FloatingCanvas : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform playerCamera;

    [Header("Follow Offset (camera local space)")]
    [SerializeField] private float forwardDistance = 1.5f;
    [SerializeField] private float verticalOffset = -0.1f;

    [Header("Smoothing")]
    [SerializeField] private float positionLerpSpeed = 8f;
    [SerializeField] private float rotationLerpSpeed = 10f;

    private void Awake()
    {
        if (playerCamera == null && Camera.main != null)
            playerCamera = Camera.main.transform;
    }

    private void LateUpdate()
    {
        if (playerCamera == null) return;

        // Desired position in front of the player's camera
        Vector3 targetPos =
            playerCamera.position +
            playerCamera.forward * forwardDistance +
            playerCamera.up * verticalOffset;

        // Frame-rate independent smoothing
        float posT = 1f - Mathf.Exp(-positionLerpSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, targetPos, posT);

        // Keep canvas oriented with camera direction (smoothly)
        Quaternion targetRot = playerCamera.rotation;
        float rotT = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotT);
    }
}
