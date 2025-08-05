using UnityEngine;

public class CameraController : MonoBehaviour
{
    [Header("Move")]
    public float _moveSpeed = 10f;

    [Header("Mouse Look")]
    public float _mouseSensitivity = 2f; // マウス感度
    public float _pitchMin = -89f;       // 上下回転の下限（俯角）
    public float _pitchMax = 89f;        // 上下回転の上限（仰角）

    private float _yaw;   // 水平回転角（Y軸）
    private float _pitch; // 垂直回転角（X軸）

    void Start()
    {
        // マウスカーソルをロック&非表示
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // いまの角度を初期値に
        var e = transform.eulerAngles;
        _yaw = e.y;
        _pitch = e.x;
    }

    void Update()
    {
        // ====== マウス回転 ======
        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        _yaw += mouseX * _mouseSensitivity;
        _pitch -= mouseY * _mouseSensitivity; // マウス上移動で上を向くように符号を反転
        _pitch = Mathf.Clamp(_pitch, _pitchMin, _pitchMax);

        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

        // ====== キーボード移動（任意） ======
        float moveX = Input.GetAxis("Horizontal"); // A/D
        float moveZ = Input.GetAxis("Vertical");   // W/S

        Vector3 move = transform.right * moveX + transform.forward * moveZ;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;

        transform.position += move * _moveSpeed * Time.deltaTime;

        // ====== Escでカーソル解放（任意） ======
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
