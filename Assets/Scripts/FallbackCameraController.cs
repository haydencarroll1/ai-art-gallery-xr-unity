using UnityEngine;
using UnityEngine.InputSystem;

public class FallbackCameraController : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float mouseSensitivity = 2f;

    private float rotationX;
    private float rotationY;
    private Mouse mouse;
    private Keyboard keyboard;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        Vector3 euler = transform.eulerAngles;
        rotationX = euler.y;
        rotationY = -euler.x;
        mouse = Mouse.current;
        keyboard = Keyboard.current;
    }

    void Update()
    {
        if (mouse == null) mouse = Mouse.current;
        if (keyboard == null) keyboard = Keyboard.current;
        if (mouse == null || keyboard == null) return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 mouseDelta = mouse.delta.ReadValue();
            rotationX += mouseDelta.x * mouseSensitivity * 0.1f;
            rotationY += mouseDelta.y * mouseSensitivity * 0.1f;
            rotationY = Mathf.Clamp(rotationY, -89f, 89f);
            transform.rotation = Quaternion.Euler(-rotationY, rotationX, 0f);
        }

        float h = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
        float v = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
        Vector3 move = transform.right * h + transform.forward * v;
        move.y = 0f;
        if (move.sqrMagnitude > 0f)
            transform.position += move.normalized * moveSpeed * Time.deltaTime;
    }
}
