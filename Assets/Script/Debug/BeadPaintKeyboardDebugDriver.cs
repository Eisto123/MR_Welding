using UnityEngine;

public class BeadPaintKeyboardDebugDriver : MonoBehaviour
{
    public BeadPaint beadPaint;
    public DataRecorder dataRecorder;
    public Transform weldTip;

    public float moveSpeed = 0.25f;
    public float fastMultiplier = 3f;
    public KeyCode weldKey = KeyCode.Space;

    void Update()
    {
        if (beadPaint == null || weldTip == null) return;

        float speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * fastMultiplier : moveSpeed;

        Vector3 move = Vector3.zero;
        move += Vector3.forward * Input.GetAxisRaw("Vertical");
        move += Vector3.right * Input.GetAxisRaw("Horizontal");

        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        if (move.sqrMagnitude > 0f)
        {
            weldTip.position += move.normalized * speed * Time.deltaTime;
        }

        bool welding = Input.GetKey(weldKey) || Input.GetMouseButton(0);

        if (welding && !beadPaint.isDrawing)
        {
            dataRecorder?.StartRecording();
            beadPaint.SetDrawingActive(true);
        }
        else if (!welding && beadPaint.isDrawing)
        {
            beadPaint.SetDrawingActive(false);
            dataRecorder?.StopRecording();
        }
    }
}

