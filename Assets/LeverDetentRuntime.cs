using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class LeverRotorTorqueTest : MonoBehaviour
{
    const float PhysicalToLeverSign = -1f;

    [SerializeField, Min(0f)] float torque = 1f;
    [SerializeField, Min(0.01f), Tooltip("Time to reach maximum input torque [s]")]
    float torqueRampSeconds = 3f;
    [SerializeField, Min(0.01f), Tooltip("Time for input torque to decay to zero after releasing the arrow key [s]")]
    float torqueReleaseSeconds = 1f;
    [SerializeField, Tooltip("Current ramped input torque [N m]")]
    float currentAppliedTorque;
    [SerializeField, Min(0f), Tooltip("Viscous resistance coefficient: torque = -coefficient * angular velocity [N m s/rad]")]
    float rotationalResistanceCoefficient = 0.3f;
    [SerializeField, Min(0f), Tooltip("Additional viscous damping applied only while a gear tooth touches the plunger [N m s/rad]")]
    float contactDampingCoefficient = 1f;
    [SerializeField, Range(0.1f, 30f), Tooltip("Half-width of the tooth alignment region where contact damping is active [degrees]")]
    float contactDampingApproachAngleDegrees = 10f;
    [SerializeField, Range(0f, 1f), Tooltip("Current damping strength from tooth alignment")]
    float currentContactDampingStrength;
    [SerializeField, Tooltip("Current angle-based contact damping coefficient [N m s/rad]")]
    float currentContactDampingCoefficient;
    [SerializeField, Tooltip("Current contact damping torque around the rotation axis [N m]")]
    float currentContactDampingTorque;
    [SerializeField, Tooltip("Current total resistance torque around the rotation axis [N m]")]
    float currentResistanceTorque;
    [SerializeField, Tooltip("Record one CSV row per physics step for Python/Excel training data")]
    bool recordTrainingData = true;
    [SerializeField, Tooltip("Hold the lever at the specified angle. CSV recording is disabled while this mode is active.")]
    bool angleSetMode;
    [SerializeField, Range(-3.1415927f, 3.1415927f), Tooltip("Lever angle used by Angle Set Mode [radians]")]
    float targetAngleRadians;
    [SerializeField, Tooltip("Full path of the CSV currently being recorded")]
    string recordingFilePath;
    [SerializeField] Vector3 localTorqueAxis = Vector3.forward;
    [SerializeField, Tooltip("Moment of inertia about the actual hinge axis [kg m^2]")]
    float hingeInertiaKgM2;
    [SerializeField, Tooltip("Current rotational kinetic energy about the hinge axis [J]")]
    float rotationalKineticEnergyJ;
    [SerializeField, Tooltip("Signed angular momentum about the hinge axis [kg m^2/s]")]
    float angularMomentumKgM2S;
    [SerializeField, Tooltip("Current DetentPlunger velocity along its constrained axis [m/s]")]
    float plungerAxisVelocityMetersPerSecond;
    [SerializeField, Tooltip("Current DetentPlunger translational kinetic energy [J]")]
    float plungerKineticEnergyJ;
    [SerializeField, Tooltip("GearRotor gravitational potential energy relative to Play start [J]")]
    float gravitationalPotentialEnergyJ;
    [SerializeField, Tooltip("Current DetentPlunger spring potential energy [J]")]
    float plungerSpringPotentialEnergyJ;
    [SerializeField] float totalKineticEnergyJ;
    [SerializeField] float totalPotentialEnergyJ;
    [SerializeField] float lagrangianJ;
    Rigidbody body;
    HingeJoint hingeJoint;
    DetentPlungerDebug detentPlunger;
    Rigidbody plungerBody;
    Vector3 initialWorldCenterOfMass;
    StreamWriter csvWriter;
    Quaternion initialLocalRotation;
    Quaternion initialWorldRotation;
    Vector3 initialWorldPosition;
    Vector3 initialHingeAxisWorld;
    Vector3 initialHingeAnchorWorld;
    float previousDirection;
    float previousAxisSpeed;
    float axisAngleRadians;
    int sampleIndex;
    int rowsSinceFlush;
    bool originalIsKinematic;

    public bool AngleSetMode => angleSetMode;
    public float TargetAngleRadians => targetAngleRadians;
    public float CurrentContactDampingStrength => currentContactDampingStrength;
    public float CurrentContactDampingCoefficient => currentContactDampingCoefficient;
    public float CurrentContactDampingTorque => currentContactDampingTorque;
    public bool IsContactDampingEngaged => currentContactDampingCoefficient > 0.0001f;
    public bool IsContactDampingActing => Mathf.Abs(currentContactDampingTorque) > 0.000001f;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        hingeJoint = GetComponent<HingeJoint>();
    }

    void Start()
    {
        CalculateHingeInertia();
        initialLocalRotation = transform.localRotation;
        initialWorldRotation = transform.rotation;
        initialWorldPosition = transform.position;
        initialHingeAxisWorld = transform.TransformDirection(hingeJoint.axis).normalized;
        initialHingeAnchorWorld = transform.TransformPoint(hingeJoint.anchor);
        originalIsKinematic = body.isKinematic;
        initialWorldCenterOfMass = body.worldCenterOfMass;
        detentPlunger = FindFirstObjectByType<DetentPlungerDebug>();
        plungerBody = detentPlunger ? detentPlunger.GetComponent<Rigidbody>() : null;
        if (!detentPlunger || !plungerBody)
            Debug.LogError("Energy logging requires DetentPlungerDebug and Rigidbody on DetentPlunger.", this);
        Vector3 worldAxis = transform.TransformDirection(localTorqueAxis).normalized;
        previousAxisSpeed = ToLeverSign(Vector3.Dot(body.angularVelocity, worldAxis));
        if (angleSetMode)
            ApplyTargetAngle();
        else if (recordTrainingData)
            StartCsvRecording();
    }

    void CalculateHingeInertia()
    {
        if (!body || !hingeJoint)
        {
            hingeInertiaKgM2 = 0f;
            Debug.LogError("Cannot calculate hinge inertia: GearRotor requires both Rigidbody and HingeJoint.", this);
            return;
        }

        Vector3 hingeAxisLocal = hingeJoint.axis.normalized;
        Vector3 axisInPrincipalCoordinates =
            Quaternion.Inverse(body.inertiaTensorRotation) * hingeAxisLocal;
        Vector3 principalInertia = body.inertiaTensor;
        float inertiaAboutParallelComAxis =
            principalInertia.x * axisInPrincipalCoordinates.x * axisInPrincipalCoordinates.x +
            principalInertia.y * axisInPrincipalCoordinates.y * axisInPrincipalCoordinates.y +
            principalInertia.z * axisInPrincipalCoordinates.z * axisInPrincipalCoordinates.z;

        Vector3 hingeAxisWorld = transform.TransformDirection(hingeAxisLocal).normalized;
        Vector3 hingeAnchorWorld = transform.TransformPoint(hingeJoint.anchor);
        Vector3 anchorToCenterOfMass = body.worldCenterOfMass - hingeAnchorWorld;
        Vector3 perpendicularOffset = anchorToCenterOfMass -
            Vector3.Dot(anchorToCenterOfMass, hingeAxisWorld) * hingeAxisWorld;

        hingeInertiaKgM2 = inertiaAboutParallelComAxis +
            body.mass * perpendicularOffset.sqrMagnitude;

        Debug.Log(
            $"GearRotor hinge inertia I0 = {hingeInertiaKgM2:R} kg m^2 " +
            $"(I_axis = {inertiaAboutParallelComAxis:R} kg m^2, " +
            $"d = {perpendicularOffset.magnitude:R} m)",
            this);
    }

    void FixedUpdate()
    {
        if (angleSetMode)
        {
            ApplyTargetAngle();
            return;
        }

        float direction = 0f;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            if (Keyboard.current.leftArrowKey.isPressed) direction += 1f;
            if (Keyboard.current.rightArrowKey.isPressed) direction -= 1f;
        }
#else
        if (Input.GetKey(KeyCode.LeftArrow)) direction += 1f;
        if (Input.GetKey(KeyCode.RightArrow)) direction -= 1f;
#endif
        Vector3 worldAxis = transform.TransformDirection(localTorqueAxis).normalized;
        float axisSpeed = ToLeverSign(Vector3.Dot(body.angularVelocity, worldAxis));
        float angularAcceleration = (axisSpeed - previousAxisSpeed) / Time.fixedDeltaTime;
        axisAngleRadians = GetDirectAxisAngleRadians();
        rotationalKineticEnergyJ = 0.5f * hingeInertiaKgM2 * axisSpeed * axisSpeed;
        angularMomentumKgM2S = hingeInertiaKgM2 * axisSpeed;
        CalculateStateEnergies();

        float approachStrength = detentPlunger
            ? detentPlunger.CalculateToothAlignment01(contactDampingApproachAngleDegrees, worldAxis)
            : 0f;
        currentContactDampingStrength = approachStrength;
        float targetContactDamping = contactDampingCoefficient * currentContactDampingStrength;
        currentContactDampingCoefficient = targetContactDamping;
        // currentContactDampingTorque = -currentContactDampingCoefficient * axisSpeed;
        currentContactDampingTorque = 0f; // Damper disabled for comparison.
        currentResistanceTorque =
            -rotationalResistanceCoefficient * axisSpeed;
            // + currentContactDampingTorque; // Enable to apply the angle-dependent damper again.
        body.AddTorque(worldAxis * ToPhysicalSign(currentResistanceTorque), ForceMode.Force);
        float signedInputTorque = 0f;

        if (direction == 0f)
        {
            currentAppliedTorque = Mathf.MoveTowards(
                currentAppliedTorque,
                0f,
                torque / torqueReleaseSeconds * Time.fixedDeltaTime);

            signedInputTorque = currentAppliedTorque * previousDirection;
            if (currentAppliedTorque <= 0f)
                previousDirection = 0f;
        }
        else
        {
            if (direction != previousDirection)
                currentAppliedTorque = 0f;

            currentAppliedTorque = Mathf.MoveTowards(
                currentAppliedTorque,
                torque,
                torque / torqueRampSeconds * Time.fixedDeltaTime);
            previousDirection = direction;

            signedInputTorque = currentAppliedTorque * direction;
        }

        float scriptedExternalTorque = 0f; // Scripted waveform torque disabled.
        // float simulationTimeSeconds = Time.fixedTime;
        // if (simulationTimeSeconds >= 0.5f && simulationTimeSeconds < 14f)
        // {
        //     scriptedExternalTorque =
        //         0.3f * Mathf.Sin(10f * simulationTimeSeconds);
        // }

        float totalInputTorque = signedInputTorque + scriptedExternalTorque;
        if (totalInputTorque != 0f)
            body.AddTorque(worldAxis * ToPhysicalSign(totalInputTorque), ForceMode.Force);

        // Keep the existing CSV column name; it now records keyboard + scripted external torque.
        WriteCsvRow(axisSpeed, angularAcceleration, totalInputTorque);
        previousAxisSpeed = axisSpeed;

    }

    public void SetAngleMode(bool enabled, float angleRadians)
    {
        angleSetMode = enabled;
        targetAngleRadians = Mathf.Clamp(angleRadians, -Mathf.PI, Mathf.PI);
        if (!Application.isPlaying || !body)
            return;

        if (enabled)
        {
            StopCsvRecording(false, true);
            ApplyTargetAngle();
        }
        else
        {
            body.isKinematic = originalIsKinematic;
            body.angularVelocity = Vector3.zero;
            body.linearVelocity = Vector3.zero;
        }
    }

    public void ApplyTargetAngle()
    {
        if (!Application.isPlaying || !body || !hingeJoint)
            return;

        body.isKinematic = true;
        body.angularVelocity = Vector3.zero;
        body.linearVelocity = Vector3.zero;

        // Match the motion produced by torque input: rotate the whole rigidbody
        // around the HingeJoint's world-space axis and anchor, not its Transform origin.
        Quaternion hingeRotation = Quaternion.AngleAxis(
            ToPhysicalSign(targetAngleRadians) * Mathf.Rad2Deg, initialHingeAxisWorld);
        Vector3 targetPosition = initialHingeAnchorWorld +
            hingeRotation * (initialWorldPosition - initialHingeAnchorWorld);
        Quaternion targetRotation = hingeRotation * initialWorldRotation;

        body.MovePosition(targetPosition);
        body.MoveRotation(targetRotation);
    }

    void CalculateStateEnergies()
    {
        Vector3 centerOfMass = body.worldCenterOfMass;
        Vector3 centerOfMassDisplacement = centerOfMass - initialWorldCenterOfMass;
        gravitationalPotentialEnergyJ =
            -body.mass * Vector3.Dot(Physics.gravity, centerOfMassDisplacement);

        plungerAxisVelocityMetersPerSecond = 0f;
        plungerKineticEnergyJ = 0f;
        plungerSpringPotentialEnergyJ = 0f;
        if (detentPlunger && plungerBody)
        {
            Vector3 movementAxisWorld = detentPlunger.MovementAxisWorld.normalized;
            plungerAxisVelocityMetersPerSecond =
                Vector3.Dot(plungerBody.linearVelocity, movementAxisWorld);
            plungerKineticEnergyJ = 0.5f * plungerBody.mass *
                plungerAxisVelocityMetersPerSecond * plungerAxisVelocityMetersPerSecond;

            float compressionMeters = Mathf.Max(
                0f,
                detentPlunger.InitialLocalPosition.y - detentPlunger.transform.localPosition.y);
            plungerSpringPotentialEnergyJ = 0.5f * detentPlunger.PositionSpring *
                compressionMeters * compressionMeters;
        }

        totalKineticEnergyJ = rotationalKineticEnergyJ + plungerKineticEnergyJ;
        totalPotentialEnergyJ = gravitationalPotentialEnergyJ + plungerSpringPotentialEnergyJ;
        lagrangianJ = totalKineticEnergyJ - totalPotentialEnergyJ;
    }

    void StartCsvRecording()
    {
        try
        {
#if UNITY_EDITOR
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RecordedData"));
#else
            string directory = Path.Combine(Application.persistentDataPath, "RecordedData");
#endif
            Directory.CreateDirectory(directory);
            recordingFilePath = Path.Combine(directory, $"lever_training_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            csvWriter = new StreamWriter(recordingFilePath, false);
            csvWriter.WriteLine("sample,time_s,angle_rad,angular_velocity_rad_s,angular_acceleration_rad_s2,friction_torque_nm,input_torque_nm,commanded_net_torque_nm,hinge_inertia_kg_m2,rotational_kinetic_energy_j,plunger_axis_velocity_m_s,plunger_kinetic_energy_j,gravitational_potential_energy_j,plunger_spring_potential_energy_j,total_kinetic_energy_j,total_potential_energy_j,lagrangian_j,plunger_compression_m,gear_center_of_mass_x_m,gear_center_of_mass_y_m,gear_center_of_mass_z_m,angular_momentum_kg_m2_s");
            csvWriter.Flush();
            Debug.Log($"Lever training data recording started: {recordingFilePath}", this);
        }
        catch (Exception exception)
        {
            Debug.LogError($"Could not start lever data recording: {exception.Message}", this);
            recordTrainingData = false;
        }
    }

    void WriteCsvRow(float axisSpeed, float angularAcceleration, float totalInputTorque)
    {
        if (csvWriter == null)
            return;

        float commandedNetTorque = totalInputTorque + currentResistanceTorque;
        csvWriter.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R},{13:R},{14:R},{15:R},{16:R},{17:R},{18:R},{19:R},{20:R},{21:R}",
            sampleIndex++, Time.fixedTime, axisAngleRadians, axisSpeed, angularAcceleration,
            currentResistanceTorque, totalInputTorque, commandedNetTorque,
            hingeInertiaKgM2, rotationalKineticEnergyJ,
            plungerAxisVelocityMetersPerSecond, plungerKineticEnergyJ,
            gravitationalPotentialEnergyJ, plungerSpringPotentialEnergyJ,
            totalKineticEnergyJ, totalPotentialEnergyJ, lagrangianJ,
            detentPlunger ? Mathf.Max(0f, detentPlunger.InitialLocalPosition.y - detentPlunger.transform.localPosition.y) : 0f,
            body.worldCenterOfMass.x, body.worldCenterOfMass.y, body.worldCenterOfMass.z,
            angularMomentumKgM2S));

        if (++rowsSinceFlush >= 50)
        {
            csvWriter.Flush();
            rowsSinceFlush = 0;
        }
    }

    float GetDirectAxisAngleRadians()
    {
        Quaternion relative = Quaternion.Inverse(initialLocalRotation) * transform.localRotation;
        Vector3 axis = localTorqueAxis.normalized;
        float projected = relative.x * axis.x + relative.y * axis.y + relative.z * axis.z;
        float magnitude = Mathf.Sqrt(relative.w * relative.w + projected * projected);
        if (magnitude < 1e-8f)
            return 0f;

        float angle = 2f * Mathf.Atan2(projected / magnitude, relative.w / magnitude);
        if (angle > Mathf.PI) angle -= 2f * Mathf.PI;
        if (angle < -Mathf.PI) angle += 2f * Mathf.PI;
        return ToLeverSign(angle);
    }

    // Unity/PhysX uses counter-clockwise as positive around +Z.  For this lever,
    // the user-facing convention is the opposite: falling left is positive.
    static float ToLeverSign(float physicalValue) => physicalValue * PhysicalToLeverSign;
    static float ToPhysicalSign(float leverValue) => leverValue * PhysicalToLeverSign;

    void OnDestroy()
    {
        StopCsvRecording(true, false);
    }

    void StopCsvRecording(bool reportSaved, bool discardFile)
    {
        if (csvWriter == null)
            return;
        csvWriter.Flush();
        csvWriter.Dispose();
        csvWriter = null;
        if (discardFile && !string.IsNullOrEmpty(recordingFilePath) && File.Exists(recordingFilePath))
        {
            File.Delete(recordingFilePath);
            Debug.Log("Lever training CSV discarded because Angle Set Mode was enabled.", this);
            recordingFilePath = string.Empty;
            return;
        }
        if (reportSaved)
            Debug.Log($"Lever training data saved: {recordingFilePath}");
    }
}

[DisallowMultipleComponent]
public sealed class LegacyDetentPlungerDebug : MonoBehaviour
{
    [SerializeField] Vector3 initialLocalPosition;
    [SerializeField] float positionSpring = 10000000000f;
    [SerializeField] float positionDamper = 0f;
    [SerializeField] float maximumSpringForce = 1000f;
    [SerializeField, Tooltip("現在の下向き沈み込み量 [m]")] float currentCompressionMeters;
    [SerializeField, Tooltip("概算ばね力 [N]")] float estimatedSpringForceNewtons;
    [SerializeField, Tooltip("PhysXジョイントが現在発生させている力 [N]")]
    float currentJointForceNewtons;
    Rigidbody body;
    ConfigurableJoint configurableJoint;

    public float CurrentCompressionMeters => currentCompressionMeters;
    public float EstimatedSpringForceNewtons => estimatedSpringForceNewtons;
    public float CurrentJointForceNewtons => currentJointForceNewtons;

    public void CaptureInitialPosition()
    {
        initialLocalPosition = transform.localPosition;
        positionSpring = 10000000000f;
        positionDamper = 0f;
        maximumSpringForce = 1000f;
    }

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        configurableJoint = GetComponent<ConfigurableJoint>();
        if (initialLocalPosition == default) initialLocalPosition = transform.localPosition;

        if (configurableJoint)
        {
            configurableJoint.yDrive = new JointDrive
            {
                positionSpring = positionSpring,
                positionDamper = positionDamper,
                maximumForce = maximumSpringForce
            };
        }
    }

    void FixedUpdate()
    {
        var local = transform.localPosition;
        if (local.y > initialLocalPosition.y)
        {
            local.y = initialLocalPosition.y;
            body.position = transform.parent ? transform.parent.TransformPoint(local) : local;
            var up = transform.parent ? transform.parent.up : Vector3.up;
            float upwardSpeed = Vector3.Dot(body.linearVelocity, up);
            if (upwardSpeed > 0f) body.linearVelocity -= up * upwardSpeed;
        }
        currentCompressionMeters = Mathf.Max(0f, initialLocalPosition.y - transform.localPosition.y);
        estimatedSpringForceNewtons = positionSpring * currentCompressionMeters;
        currentJointForceNewtons = configurableJoint ? configurableJoint.currentForce.magnitude : 0f;
    }
}

[DisallowMultipleComponent]
public sealed class LeverContactInset : MonoBehaviour
{
    [SerializeField] RenderTexture contactView;
    [SerializeField, Range(0.2f, 0.5f)] float screenWidthRatio = 0.34f;
    [SerializeField] int marginPixels = 24;

    public void Configure(RenderTexture texture) => contactView = texture;

    void Awake() => EnsureRuntimeCanvas();

    void EnsureRuntimeCanvas()
    {
        if (!contactView || transform.Find("ContactInsetCanvas")) return;
        var canvasObject = new GameObject("ContactInsetCanvas", typeof(RectTransform), typeof(Canvas),
                                          typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(2560f, 1440f);
        scaler.matchWidthOrHeight = 0f;

        var borderObject = new GameObject("ContactInsetBorder", typeof(RectTransform), typeof(Image));
        borderObject.transform.SetParent(canvasObject.transform, false);
        var border = borderObject.GetComponent<RectTransform>();
        border.anchorMin = border.anchorMax = new Vector2(1f, 0.5f);
        border.pivot = new Vector2(1f, 0.5f);
        border.anchoredPosition = new Vector2(-24f, 0f);
        border.sizeDelta = new Vector2(648f, 488f);
        borderObject.GetComponent<Image>().color = new Color(0.02f, 0.035f, 0.045f, 0.98f);

        var viewObject = new GameObject("ContactInsetView", typeof(RectTransform), typeof(RawImage));
        viewObject.transform.SetParent(border, false);
        var view = viewObject.GetComponent<RectTransform>();
        view.anchorMin = Vector2.zero;
        view.anchorMax = Vector2.one;
        view.offsetMin = new Vector2(4f, 4f);
        view.offsetMax = new Vector2(-4f, -4f);
        viewObject.GetComponent<RawImage>().texture = contactView;
    }

    void OnGUI()
    {
        if (!contactView) return;
        float width = Mathf.Min(Screen.width * screenWidthRatio, 720f);
        float height = width * contactView.height / contactView.width;
        var outer = new Rect(Screen.width - width - marginPixels, marginPixels, width, height);
        var inner = new Rect(outer.x + 4f, outer.y + 4f, outer.width - 8f, outer.height - 8f);
        var oldColor = GUI.color;
        GUI.color = new Color(0.03f, 0.05f, 0.06f, 0.96f);
        GUI.DrawTexture(outer, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.DrawTexture(inner, contactView, ScaleMode.ScaleToFit, false);
        GUI.color = oldColor;
    }
}
