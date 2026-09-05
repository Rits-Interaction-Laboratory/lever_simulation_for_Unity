using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class DetentPlungerDebug : MonoBehaviour
{
    [SerializeField, Tooltip("Enable physical contact between the gear teeth and plunger. Disable to remove contact-generated torque.")]
    bool enableGearContact = true;
    [SerializeField] Vector3 initialLocalPosition;
    [SerializeField] float positionSpring = 400000f;
    [SerializeField] float positionDamper = 30f;
    [SerializeField] float maximumSpringForce = 50f;
    [SerializeField, Tooltip("Current downward displacement [m]")]
    float currentCompressionMeters;
    [SerializeField, Tooltip("Position Spring x compression [N]")]
    float estimatedSpringForceNewtons;
    [SerializeField, Tooltip("Force currently reported by the PhysX joint [N]")]
    float currentJointForceNewtons;
    [SerializeField, Tooltip("Upward spring force explicitly applied to the Rigidbody [N]")]
    float appliedSpringForceNewtons;
    [SerializeField, Tooltip("Signed joint force along the DetentPlunger local Y axis [N]")]
    float localYJointForceNewtons;
    [SerializeField, Tooltip("Signed velocity along the DetentPlunger local Y axis [m/s]")]
    float localYVelocityMetersPerSecond;
    [SerializeField, Tooltip("Number of gear colliders currently touching the plunger")]
    int contactingGearColliderCount;
    [SerializeField, TextArea(2, 8), Tooltip("Names of gear colliders currently touching the plunger")]
    string contactingGearColliderNames;
    [SerializeField, TextArea(3, 10), Tooltip("Collider pairs currently touching")]
    string contactingColliderPairs;
    [SerializeField, Tooltip("Current separation of the two joint anchors along joint Y [m]")]
    float jointAnchorSeparationY;
    [SerializeField, Tooltip("Joint-anchor Y displacement relative to the value captured on Play [m]")]
    float jointYDisplacementFromPlayStart;
    [SerializeField, Tooltip("Y position error seen by the drive: anchor separation minus target [m]")]
    float jointYDriveError;
    float initialJointAnchorSeparationY;

    Rigidbody body;
    ConfigurableJoint configurableJoint;
    SphereCollider contactTipCollider;
    Transform gearCollidersTransform;
    Collider[] gearColliders = System.Array.Empty<Collider>();
    readonly HashSet<Collider> contactingGearColliders = new HashSet<Collider>();
    readonly HashSet<string> activeContactPairs = new HashSet<string>();

    public float CurrentCompressionMeters => currentCompressionMeters;
    public Vector3 InitialLocalPosition => initialLocalPosition;
    public float PositionSpring => positionSpring;
    public float PositionDamper => positionDamper;
    public float MaximumSpringForce => maximumSpringForce;
    public Vector3 MovementAxisWorld => transform.parent ? transform.parent.up : Vector3.up;
    public float EstimatedSpringForceNewtons => estimatedSpringForceNewtons;
    public float CurrentJointForceNewtons => currentJointForceNewtons;
    public float AppliedSpringForceNewtons => appliedSpringForceNewtons;
    public float LocalYJointForceNewtons => localYJointForceNewtons;
    public float LocalYVelocityMetersPerSecond => localYVelocityMetersPerSecond;
    public int ContactingGearColliderCount => contactingGearColliderCount;
    public string ContactingGearColliderNames => contactingGearColliderNames;
    public string ContactingColliderPairs => contactingColliderPairs;
    public float JointAnchorSeparationY => jointAnchorSeparationY;
    public float JointYDisplacementFromPlayStart => jointYDisplacementFromPlayStart;
    public float JointTargetPositionY => configurableJoint ? configurableJoint.targetPosition.y : 0f;
    public float JointYDriveError => jointYDriveError;
    public bool IsBodySleeping => body && body.IsSleeping();
    public bool AreGearCollidersEnabled
    {
        get
        {
            Collider[] colliders = GetGearColliders();
            foreach (Collider gearCollider in colliders)
            {
                if (gearCollider && gearCollider.enabled)
                    return true;
            }
            return false;
        }
    }

    public void SetGearCollidersEnabled(bool enabled)
    {
        foreach (Collider gearCollider in GetGearColliders())
        {
            if (gearCollider)
                gearCollider.enabled = enabled;
        }

        if (!enabled)
        {
            contactingGearColliders.Clear();
            activeContactPairs.Clear();
            contactingGearColliderCount = 0;
            contactingGearColliderNames = "(none)";
            contactingColliderPairs = "(none)";
        }

        if (body)
            body.WakeUp();
    }

    public void CaptureInitialPosition()
    {
        initialLocalPosition = transform.localPosition;
        positionSpring = 400000f;
        positionDamper = 30f;
        maximumSpringForce = 50f;
    }

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        configurableJoint = GetComponent<ConfigurableJoint>();
        Transform contactTip = transform.Find("ContactTip");
        contactTipCollider = contactTip ? contactTip.GetComponent<SphereCollider>() : null;
        GameObject gearRotor = GameObject.Find("GearRotor");
        gearCollidersTransform = gearRotor ? gearRotor.transform.Find("GearColliders") : null;
        gearColliders = GetGearColliders();
        if (body)
        {
            body.sleepThreshold = 0f;
            body.WakeUp();
        }
        IgnoreBodyColliderAgainstGearTeeth();
        SetGearCollidersEnabled(enableGearContact);
        // The position at the moment Play starts is always the spring's rest position.
        initialLocalPosition = transform.localPosition;

        if (configurableJoint)
        {
            configurableJoint.yDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0f,
                maximumForce = 0f
            };
            initialJointAnchorSeparationY = CalculateJointAnchorSeparationY();
        }
    }

    void IgnoreBodyColliderAgainstGearTeeth()
    {
        Transform bodyColliderTransform = transform.Find("PlungerBodyCollider");
        Collider bodyCollider = bodyColliderTransform
            ? bodyColliderTransform.GetComponent<Collider>()
            : null;
        if (!bodyCollider)
            return;

        GameObject gearRotor = GameObject.Find("GearRotor");
        Transform gearColliders = gearRotor ? gearRotor.transform.Find("GearColliders") : null;
        if (!gearColliders)
            return;

        foreach (Collider toothCollider in gearColliders.GetComponentsInChildren<Collider>(true))
            Physics.IgnoreCollision(bodyCollider, toothCollider, true);
    }

    static Collider[] GetGearColliders()
    {
        GameObject gearRotor = GameObject.Find("GearRotor");
        Transform gearColliders = gearRotor ? gearRotor.transform.Find("GearColliders") : null;
        return gearColliders
            ? gearColliders.GetComponentsInChildren<Collider>(true)
            : System.Array.Empty<Collider>();
    }

    void FixedUpdate()
    {
        Vector3 localYInWorld = transform.parent ? transform.parent.up : Vector3.up;
        float compressionBeforeClamp = Mathf.Max(0f, initialLocalPosition.y - transform.localPosition.y);
        float localYSpeed = body ? Vector3.Dot(body.linearVelocity, localYInWorld) : 0f;
        appliedSpringForceNewtons = Mathf.Clamp(
            positionSpring * compressionBeforeClamp - positionDamper * localYSpeed,
            0f,
            maximumSpringForce);
        if (body && appliedSpringForceNewtons > 0f)
            body.AddForce(localYInWorld * appliedSpringForceNewtons, ForceMode.Force);

        var local = transform.localPosition;
        if (local.y > initialLocalPosition.y)
        {
            local.y = initialLocalPosition.y;
            body.position = transform.parent ? transform.parent.TransformPoint(local) : local;
            var up = transform.parent ? transform.parent.up : Vector3.up;
            float upwardSpeed = Vector3.Dot(body.linearVelocity, up);
            if (upwardSpeed > 0f)
                body.linearVelocity -= up * upwardSpeed;
        }

        currentCompressionMeters = Mathf.Max(0f, initialLocalPosition.y - transform.localPosition.y);
        estimatedSpringForceNewtons = positionSpring * currentCompressionMeters;
        currentJointForceNewtons = configurableJoint ? configurableJoint.currentForce.magnitude : 0f;

        localYJointForceNewtons = configurableJoint
            ? Vector3.Dot(configurableJoint.currentForce, localYInWorld)
            : 0f;
        localYVelocityMetersPerSecond = body
            ? Vector3.Dot(body.linearVelocity, localYInWorld)
            : 0f;
        contactingGearColliderCount = contactingGearColliders.Count;
        UpdateContactingGearColliderNames();
        UpdateContactingColliderPairs();

        jointAnchorSeparationY = CalculateJointAnchorSeparationY();
        jointYDisplacementFromPlayStart = jointAnchorSeparationY - initialJointAnchorSeparationY;
        jointYDriveError = configurableJoint
            ? jointAnchorSeparationY - configurableJoint.targetPosition.y
            : 0f;
    }

    void OnCollisionEnter(Collision collision)
    {
        RefreshContactPairs(collision);
    }

    void OnCollisionStay(Collision collision)
    {
        RefreshContactPairs(collision);
    }

    void OnCollisionExit(Collision collision)
    {
        contactingGearColliders.Clear();
        activeContactPairs.Clear();
    }

    void OnDisable()
    {
        contactingGearColliders.Clear();
        activeContactPairs.Clear();
        contactingGearColliderCount = 0;
        contactingGearColliderNames = string.Empty;
        contactingColliderPairs = string.Empty;
    }

    public float CalculateToothAlignment01(float approachAngleDegrees, Vector3 rotationAxisWorld)
    {
        if (!contactTipCollider || !gearCollidersTransform || approachAngleDegrees <= 0f)
            return 0f;

        Vector3 gearCenter = gearCollidersTransform.position;
        Vector3 tipCenter = contactTipCollider.transform.TransformPoint(contactTipCollider.center);
        Vector3 plungerDirection = Vector3.ProjectOnPlane(
            tipCenter - gearCenter,
            rotationAxisWorld).normalized;
        if (plungerDirection.sqrMagnitude <= Mathf.Epsilon)
            return 0f;

        float greatestAlignment = 0f;
        foreach (Collider gearCollider in gearColliders)
        {
            if (!gearCollider || !gearCollider.enabled || !gearCollider.gameObject.activeInHierarchy)
                continue;

            Vector3 toothDirection = Vector3.ProjectOnPlane(
                gearCollider.bounds.center - gearCenter,
                rotationAxisWorld).normalized;
            if (toothDirection.sqrMagnitude <= Mathf.Epsilon)
                continue;

            float angle = Vector3.Angle(plungerDirection, toothDirection);
            float linearAlignment = 1f - Mathf.Clamp01(angle / approachAngleDegrees);
            // Curved response (enable this line to use SmoothStep):
            // float alignment = Mathf.SmoothStep(0f, 1f, linearAlignment);
            // Linear response:
            float alignment = linearAlignment;
            greatestAlignment = Mathf.Max(greatestAlignment, alignment);
        }

        return greatestAlignment;
    }
    static bool IsGearCollider(Collider candidate)
    {
        for (Transform current = candidate.transform; current; current = current.parent)
        {
            if (current.name == "GearColliders" || current.name == "GearRotor")
                return true;
        }

        return false;
    }

    void RefreshContactPairs(Collision collision)
    {
        contactingGearColliders.Clear();
        activeContactPairs.Clear();

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);
            AddContactPair(contact.thisCollider, contact.otherCollider);
        }
    }

    void AddContactPair(Collider first, Collider second)
    {
        Collider gearCollider;
        Collider plungerCollider;

        if (IsGearCollider(first))
        {
            gearCollider = first;
            plungerCollider = second;
        }
        else if (IsGearCollider(second))
        {
            gearCollider = second;
            plungerCollider = first;
        }
        else
            return;

        contactingGearColliders.Add(gearCollider);
        activeContactPairs.Add(FormatContactPair(plungerCollider, gearCollider));
    }

    static string FormatContactPair(Collider plungerCollider, Collider gearCollider)
    {
        string plungerName = plungerCollider ? plungerCollider.name : "(unknown plunger collider)";
        string gearName = gearCollider ? gearCollider.name : "(unknown gear collider)";
        return plungerName + " (" + (plungerCollider ? plungerCollider.GetType().Name : "Collider") + ")"
            + "  <->  " + gearName + " (" + (gearCollider ? gearCollider.GetType().Name : "Collider") + ")";
    }

    void UpdateContactingGearColliderNames()
    {
        if (contactingGearColliders.Count == 0)
        {
            contactingGearColliderNames = "(none)";
            return;
        }

        var names = new List<string>(contactingGearColliders.Count);
        foreach (Collider gearCollider in contactingGearColliders)
        {
            if (gearCollider)
                names.Add(gearCollider.name + " (" + gearCollider.GetType().Name + ")");
        }
        names.Sort();
        contactingGearColliderNames = string.Join("\n", names);
    }

    void UpdateContactingColliderPairs()
    {
        if (activeContactPairs.Count == 0)
        {
            contactingColliderPairs = "(none)";
            return;
        }

        var pairs = new List<string>(activeContactPairs);
        pairs.Sort();
        contactingColliderPairs = string.Join("\n", pairs);
    }

    float CalculateJointAnchorSeparationY()
    {
        if (!configurableJoint)
            return 0f;

        Vector3 anchorWorld = transform.TransformPoint(configurableJoint.anchor);
        Vector3 connectedAnchorWorld = configurableJoint.connectedBody
            ? configurableJoint.connectedBody.transform.TransformPoint(configurableJoint.connectedAnchor)
            : configurableJoint.connectedAnchor;

        Vector3 jointX = transform.TransformDirection(configurableJoint.axis).normalized;
        Vector3 secondary = transform.TransformDirection(configurableJoint.secondaryAxis).normalized;
        Vector3 jointZ = Vector3.Cross(jointX, secondary).normalized;
        Vector3 jointY = Vector3.Cross(jointZ, jointX).normalized;
        return Vector3.Dot(anchorWorld - connectedAnchorWorld, jointY);
    }
}
