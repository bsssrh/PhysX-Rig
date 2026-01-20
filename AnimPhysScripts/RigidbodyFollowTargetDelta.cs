using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class RigidbodyFollowTargetDeltaPose : MonoBehaviour, IForceRecordSource
{
    [Header("References")]
    public Transform target;
    public Rigidbody rb;

    [Header("Delta Follow")]
    [Range(0f, 2f)]
    public float deltaMultiplier = 1f;

    public bool useAxisMask = false;
    public bool followX = true;
    public bool followY = true;
    public bool followZ = true;

    [Header("Position PD")]
    public float positionSpring = 180f;
    public float positionDamping = 35f;
    public float maxAccel = 400f;

    [Header("Rotation Hold")]
    public bool holdRotation = true;
    public bool followRotationDelta = true;

    public float rotationSpring = 80f;        // deg-domain spring
    public float rotationDamping = 14f;       // deg-domain damping
    public float maxAngularAccelDeg = 600f;   // deg/s^2 clamp

    [Header("Upright (anti-tilt)")]
    public bool keepUpright = true;
    public Vector3 worldUp = new Vector3(0, 1, 0);
    public float uprightSpring = 120f; // rad-domain
    public float uprightDamping = 18f; // rad-domain

    [Header("Hold / Stability")]
    public bool extraHoldDamping = true;
    public float holdEpsilon = 0.01f;
    public float holdDamping = 10f;

    [Header("Gravity")]
    public bool compensateGravity = true;

    [Header("Safety")]
    public bool autoRecalibrateOnLargeJump = true;
    public float largeJumpDistance = 2f;

    [Header("Debug")]
    public bool drawDebug = false;

    // --- IForceRecordSource ---
    public Rigidbody Body => rb;
    public Vector3 LastAppliedAccel { get; private set; }     // m/s^2
    public Vector3 LastAppliedAngAccel { get; private set; }  // rad/s^2

    private Vector3 lastTargetPos;
    private Quaternion lastTargetRot;

    private Vector3 desiredPos;
    private Quaternion desiredRot;

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        if (rb == null)
        {
            Debug.LogError($"{nameof(RigidbodyFollowTargetDeltaPose)}: Rigidbody missing on {name}");
            enabled = false;
            return;
        }

        if (target == null)
        {
            Debug.LogError($"{nameof(RigidbodyFollowTargetDeltaPose)}: Target not assigned on {name}");
            enabled = false;
            return;
        }

        lastTargetPos = target.position;
        lastTargetRot = target.rotation;

        desiredPos = rb.position;
        desiredRot = rb.rotation;
    }

    private void FixedUpdate()
    {
        if (target == null || rb == null) return;

        // Clear each tick
        LastAppliedAccel = Vector3.zero;
        LastAppliedAngAccel = Vector3.zero;

        // Target delta position
        Vector3 currentTargetPos = target.position;
        Vector3 targetDeltaPos = currentTargetPos - lastTargetPos;

        if (autoRecalibrateOnLargeJump && targetDeltaPos.sqrMagnitude > largeJumpDistance * largeJumpDistance)
        {
            Recalibrate();
            return;
        }

        lastTargetPos = currentTargetPos;

        if (useAxisMask)
        {
            targetDeltaPos = new Vector3(
                followX ? targetDeltaPos.x : 0f,
                followY ? targetDeltaPos.y : 0f,
                followZ ? targetDeltaPos.z : 0f
            );
        }

        desiredPos += targetDeltaPos * deltaMultiplier;

        // Position PD (accel form)
        Vector3 posError = desiredPos - rb.position;
        Vector3 accel = (positionSpring * posError) - (positionDamping * rb.velocity);

        if (compensateGravity && rb.useGravity)
            accel += -Physics.gravity;

        if (extraHoldDamping && posError.sqrMagnitude <= holdEpsilon * holdEpsilon)
            accel += (-rb.velocity * holdDamping);

        accel = ClampMagnitude(accel, maxAccel);

        rb.AddForce(accel, ForceMode.Acceleration);
        LastAppliedAccel = accel;

        // Rotation
        if (holdRotation)
        {
            Quaternion currentTargetRot = target.rotation;

            if (followRotationDelta)
            {
                Quaternion deltaRot = currentTargetRot * Quaternion.Inverse(lastTargetRot);
                desiredRot = deltaRot * desiredRot;
            }

            lastTargetRot = currentTargetRot;

            Vector3 angAccelFromRot = ApplyRotationPD_Accel(desiredRot, rotationSpring, rotationDamping, maxAngularAccelDeg);
            LastAppliedAngAccel += angAccelFromRot;

            if (keepUpright)
            {
                Vector3 uprightAngAccel = ComputeUprightAngAccel(worldUp.normalized);
                uprightAngAccel = ClampMagnitude(uprightAngAccel, maxAngularAccelDeg * Mathf.Deg2Rad);

                rb.AddTorque(uprightAngAccel, ForceMode.Acceleration);
                LastAppliedAngAccel += uprightAngAccel;
            }
        }

        if (drawDebug)
        {
            Debug.DrawLine(rb.position, desiredPos, Color.yellow, Time.fixedDeltaTime);
            Debug.DrawRay(desiredPos, Vector3.up * 0.15f, Color.green, Time.fixedDeltaTime);
        }
    }

    private Vector3 ApplyRotationPD_Accel(Quaternion targetRot, float springDeg, float dampingDeg, float maxAngAccelDegLocal)
    {
        Quaternion q = targetRot * Quaternion.Inverse(rb.rotation);
        if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }

        q.ToAngleAxis(out float angleDeg, out Vector3 axis);
        if (float.IsNaN(axis.x)) return Vector3.zero;
        if (angleDeg > 180f) angleDeg -= 360f;

        Vector3 angVelDeg = rb.angularVelocity * Mathf.Rad2Deg;
        Vector3 angAccelDeg = (springDeg * angleDeg * axis) - (dampingDeg * angVelDeg);
        angAccelDeg = ClampMagnitude(angAccelDeg, maxAngAccelDegLocal);

        Vector3 angAccelRad = angAccelDeg * Mathf.Deg2Rad;
        rb.AddTorque(angAccelRad, ForceMode.Acceleration);

        return angAccelRad;
    }

    private Vector3 ComputeUprightAngAccel(Vector3 upWorld)
    {
        Vector3 up = rb.rotation * Vector3.up;
        Vector3 axis = Vector3.Cross(up, upWorld);
        float axisMag = axis.magnitude;

        if (axisMag < 0.000001f) return Vector3.zero;

        axis /= axisMag;
        float angleRad = Mathf.Asin(Mathf.Clamp(axisMag, -1f, 1f));

        Vector3 angVel = rb.angularVelocity;
        Vector3 angAccel = (uprightSpring * angleRad * axis) - (uprightDamping * angVel);

        return angAccel;
    }

    private static Vector3 ClampMagnitude(Vector3 v, float maxMag)
    {
        float mag = v.magnitude;
        if (mag > maxMag && mag > 0f) return v * (maxMag / mag);
        return v;
    }

    public void Recalibrate()
    {
        if (target == null || rb == null) return;
        lastTargetPos = target.position;
        lastTargetRot = target.rotation;
        desiredPos = rb.position;
        desiredRot = rb.rotation;
    }
}
