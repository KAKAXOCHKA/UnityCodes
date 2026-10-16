using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(-100)]
public class FirstPersonController : MonoBehaviour
{
    public float walkSpeed = 4.2f;
    public float sprintSpeed = 7.1f;
    public float jumpHeight = 1.15f;
    public float gravity = -20f;
    public float lookSensitivity = 0.09f;

    [Header("Приседание (удерживать Ctrl)")]
    [Min(0f)] public float crouchSpeed = 2f;
    [Min(0.7f)] public float standHeight = 1.8f;
    [Min(0.7f)] public float crouchHeight = 1f;
    [Min(0.1f)] public float crouchSmoothTime = 12f;
    public LayerMask headroomMask = ~0;
    public bool IsCrouching { get; private set; }

    CharacterController controller;
    PlayerVitals vitals;
    Camera viewCamera;
    float pitch;
    float verticalVelocity;
    Vector3 planarVelocity;
    bool skipLook;
    Vector3 standingCameraPosition;
    Vector3 originalCenter;
    float feetOffset;
    float originalStepOffset;
    readonly Collider[] headroomHits = new Collider[16];

    float StandingHeight => Mathf.Max(standHeight, controller.radius * 2f);
    float CrouchingHeight => Mathf.Clamp(crouchHeight, controller.radius * 2f, StandingHeight);

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        vitals = GetComponent<PlayerVitals>();
        originalCenter = controller.center;
        feetOffset = controller.center.y - controller.height * 0.5f;
        originalStepOffset = controller.stepOffset;
    }

    void Start()
    {
        viewCamera = GetComponentInChildren<Camera>();
        if (viewCamera != null) standingCameraPosition = viewCamera.transform.localPosition;
        LockCursor();
    }

    void Update()
    {
        HandleCursor();

        if (vitals != null && vitals.IsDead)
        {
            ApplyGravityOnly();
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            planarVelocity = Vector3.zero;
            ApplyGravityOnly();
            return;
        }

        Look();
        Move();
    }

    void HandleCursor()
    {
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            if (Cursor.lockState == CursorLockMode.Locked)
                UnlockCursor();
            else
                LockCursor();
        }

        if (Cursor.lockState != CursorLockMode.Locked && mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            LockCursor();
            skipLook = true;
        }
    }

    void Look()
    {
        if (skipLook)
        {
            skipLook = false;
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked || Mouse.current == null || viewCamera == null)
            return;

        Vector2 delta = Mouse.current.delta.ReadValue();
        transform.Rotate(0f, delta.x * lookSensitivity, 0f);
        pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, -85f, 85f);
        viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move()
    {
        Keyboard keyboard = Keyboard.current;
        Vector2 input = Vector2.zero;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) input.x -= 1f;
        }

        if (input.sqrMagnitude > 1f)
            input.Normalize();

        bool crouch = keyboard != null && keyboard.leftCtrlKey.isPressed;
        UpdateCrouch(crouch, Time.deltaTime);
        bool sprint = !IsCrouching && keyboard != null && keyboard.leftShiftKey.isPressed && input.y > 0.1f;
        float speed = IsCrouching ? crouchSpeed : sprint ? sprintSpeed : walkSpeed;
        Vector3 wish = (transform.right * input.x + transform.forward * input.y) * speed;
        float blend = 1f - Mathf.Exp(-14f * Time.deltaTime);
        planarVelocity = Vector3.Lerp(planarVelocity, wish, blend);

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        if (!IsCrouching && controller.isGrounded && keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;
        Vector3 motion = planarVelocity;
        motion.y = verticalVelocity;
        CollisionFlags collisions = controller.Move(motion * Time.deltaTime);
        if ((collisions & CollisionFlags.Above) != 0 && verticalVelocity > 0f) verticalVelocity = 0f;
    }

    void UpdateCrouch(bool requested, float deltaTime)
    {
        bool stayLow = requested || !CanStand();
        float target = stayLow ? CrouchingHeight : StandingHeight;
        float blend = 1f - Mathf.Exp(-Mathf.Max(0.1f, crouchSmoothTime) * deltaTime);
        float height = Mathf.Lerp(controller.height, target, blend);
        if (Mathf.Abs(height - target) < 0.002f) height = target;
        SetHeight(height);
        IsCrouching = stayLow || height < StandingHeight - 0.02f;
    }

    bool CanStand()
    {
        if (controller.height >= StandingHeight - 0.002f) return true;
        Vector3 scale = transform.lossyScale;
        float radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        Vector3 up = transform.up;
        Vector3 center = transform.TransformPoint(controller.center);
        Vector3 head = center + up * (controller.height * Mathf.Abs(scale.y) * 0.5f - radius);
        Vector3 standingHead = head + up * ((StandingHeight - controller.height) * Mathf.Abs(scale.y));
        // Test only the space above the current head, so the floor cannot block standing.
        int count = Physics.OverlapCapsuleNonAlloc(head, standingHead, Mathf.Max(0.01f, radius - 0.01f),
            headroomHits, headroomMask, QueryTriggerInteraction.Ignore);
        if (count == headroomHits.Length) return false;
        for (int i = 0; i < count; i++)
            if (!headroomHits[i].transform.IsChildOf(transform)) return false;
        return true;
    }

    void SetHeight(float height)
    {
        // Keep the bottom of the capsule fixed while its top moves.
        controller.stepOffset = Mathf.Min(originalStepOffset, height * 0.45f);
        controller.height = height;
        controller.center = new Vector3(originalCenter.x, feetOffset + height * 0.5f, originalCenter.z);
        if (viewCamera != null)
            viewCamera.transform.localPosition = standingCameraPosition + Vector3.up * (height - StandingHeight);
    }

    void ApplyGravityOnly()
    {
        planarVelocity = Vector3.Lerp(planarVelocity, Vector3.zero, 1f - Mathf.Exp(-8f * Time.deltaTime));
        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(new Vector3(planarVelocity.x, verticalVelocity, planarVelocity.z) * Time.deltaTime);
    }

    public static void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public void ResetMotion()
    {
        verticalVelocity = 0f;
        planarVelocity = Vector3.zero;
        pitch = 0f;
        bool canStand = CanStand();
        SetHeight(canStand ? StandingHeight : CrouchingHeight);
        IsCrouching = !canStand;
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.identity;
        skipLook = true;
    }

    void OnDisable() => UnlockCursor();

    void OnApplicationFocus(bool focused)
    {
        if (!focused) UnlockCursor();
    }

    public static void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
