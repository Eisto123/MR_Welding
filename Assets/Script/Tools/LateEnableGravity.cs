using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LateEnableGravity : MonoBehaviour
{
    private Rigidbody rb;
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        StartCoroutine(EnableGravityAfterDelay(2f));
    }

    private IEnumerator EnableGravityAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        rb.useGravity = true;
    }
}
