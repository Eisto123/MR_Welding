using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeldingTorch : MonoBehaviour
{
    public bool isGrabbing = false;

    public void OnGrab()
    {
        isGrabbing = true;
    }
    public void OnRelease()
    {
        isGrabbing = false;
    }

    public void StartWelding()
    {
        if (!isGrabbing) return;

        Debug.Log("Welding started");
    }
    

}
