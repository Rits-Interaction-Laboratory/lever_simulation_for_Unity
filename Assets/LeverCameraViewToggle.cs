using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class LeverCameraViewToggle : MonoBehaviour
{
    [SerializeField] Vector3 overviewPosition = new Vector3(-0.23f, 0.17f, 1.46f);
    [SerializeField] Vector3 closeupPosition = new Vector3(0f, 0.038f, 0.12f);
    [SerializeField] Vector3 cameraEuler = new Vector3(0f, 180f, 0f);
    [SerializeField] float overviewFieldOfView = 60f;
    [SerializeField] float closeupFieldOfView = 35f;
    [SerializeField] bool startWithCloseup = false;
    Camera viewCamera;
    bool closeup;

    void Awake()
    {
        viewCamera = GetComponent<Camera>();
        closeup = startWithCloseup;
        ApplyView();
    }

    void Update()
    {
        bool pressed = false;
#if ENABLE_INPUT_SYSTEM
        pressed = Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame;
#else
        pressed = Input.GetKeyDown(KeyCode.C);
#endif
        if (!pressed) return;
        closeup = !closeup;
        ApplyView();
    }

    void ApplyView()
    {
        transform.position = closeup ? closeupPosition : overviewPosition;
        transform.rotation = Quaternion.Euler(cameraEuler);
        viewCamera.fieldOfView = closeup ? closeupFieldOfView : overviewFieldOfView;
        viewCamera.nearClipPlane = closeup ? 0.01f : 0.3f;
        viewCamera.farClipPlane = 10f;
    }
}
