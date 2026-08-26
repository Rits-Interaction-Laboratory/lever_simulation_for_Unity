using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
internal static class LeverDetentSceneSetup
{
    const string MaterialPath = "Assets/Physics/LeverContactMaterial.physicMaterial";
    const string GlassMaterialPath = "Assets/Materials/Part2Glass.mat";
    const string OutlineMaterialPath = "Assets/Materials/Part2Outline.mat";
    const string ContactViewPath = "Assets/RenderTextures/LeverContactCloseup.renderTexture";
    static LeverDetentSceneSetup()
    {
        EditorApplication.delayCall += ApplyOnce;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += ApplyOnce;
        };
    }

    [MenuItem("Tools/Lever/Apply Detent Physics Setup")]
    static void ApplyOnce()
    {
        if (Application.isPlaying) return;
        var root = GameObject.Find("Lever_Root");
        if (!root) return;
        var frame = root.transform.Find("LeverFrame");
        var rotor = root.transform.Find("GearRotor");
        var plunger = root.transform.Find("DetentPlunger");
        if (!frame || !rotor || !plunger) { Debug.LogError("Lever setup: required hierarchy was not found."); return; }
        ApplyPart2Glass(frame.Find("part2 v1:1"));
        TuneDetentContactGeometry(rotor, plunger);
        TuneLeverDynamics(rotor, plunger);
        ApplyContactInset(root.transform);
        var mainCamera = GameObject.Find("Main Camera");
        var desiredCameraPosition = new Vector3(-0.23f, 0.17f, 1.46f);
        var desiredCameraRotation = Quaternion.Euler(0f, 180f, 0f);
        if (mainCamera && (mainCamera.transform.position != desiredCameraPosition ||
            Quaternion.Angle(mainCamera.transform.rotation, desiredCameraRotation) > 0.01f))
        {
            mainCamera.transform.position = desiredCameraPosition;
            mainCamera.transform.rotation = desiredCameraRotation;
            EditorUtility.SetDirty(mainCamera.transform);
            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveScene(root.scene);
            Debug.Log("Main Camera framing applied: (0, 0.17, 1.46), Y rotation 180");
        }
        if (mainCamera)
        {
            var cameraToggle = Add<LeverCameraViewToggle>(mainCamera);
            var toggleSettings = new SerializedObject(cameraToggle);
            toggleSettings.FindProperty("overviewPosition").vector3Value = new Vector3(-0.23f, 0.17f, 1.46f);
            toggleSettings.FindProperty("startWithCloseup").boolValue = false;
            toggleSettings.ApplyModifiedPropertiesWithoutUndo();
            mainCamera.transform.position = new Vector3(-0.23f, 0.17f, 1.46f);
            mainCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            var mainCameraComponent = mainCamera.GetComponent<Camera>();
            mainCameraComponent.fieldOfView = 60f;
            mainCameraComponent.nearClipPlane = 0.3f;
            mainCameraComponent.farClipPlane = 1000f;
            EditorUtility.SetDirty(mainCamera);
            EditorUtility.SetDirty(mainCameraComponent);
            EditorUtility.SetDirty(cameraToggle);
            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveScene(root.scene);
        }
        if (rotor.Find("GearColliders") && plunger.Find("ContactTip")) return;

        Undo.SetCurrentGroupName("Configure lever detent physics");
        int group = Undo.GetCurrentGroup();
        var material = GetOrCreateMaterial();

        var frameBody = Add<Rigidbody>(frame.gameObject);
        frameBody.isKinematic = true;
        frameBody.useGravity = false;
        frameBody.interpolation = RigidbodyInterpolation.None;
        AddFrameCollider(frame.Find("part2 v1:1"));
        AddFrameCollider(frame.Find("part3 v1:1"));

        var plungerBody = Add<Rigidbody>(plunger.gameObject);
        plungerBody.mass = 0.07f;
        plungerBody.useGravity = false;
        plungerBody.isKinematic = false;
        plungerBody.interpolation = RigidbodyInterpolation.Interpolate;
        plungerBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        plungerBody.constraints = RigidbodyConstraints.None;

        var tip = NewChild(plunger, "ContactTip");
        tip.transform.position = new Vector3(-0.0002f, 0.0167f, 0f);
        var tipCollider = Add<SphereCollider>(tip);
        tipCollider.radius = 0.002f;
        tipCollider.isTrigger = false;
        tipCollider.material = material;

        var bodyProxy = NewChild(plunger, "PlungerBodyCollider");
        bodyProxy.transform.position = new Vector3(-0.0002f, 0.0085f, 0f);
        var bodyBox = Add<BoxCollider>(bodyProxy);
        bodyBox.size = new Vector3(0.0075f, 0.004f, 0.012f);
        bodyBox.enabled = true;

        var configurable = Add<ConfigurableJoint>(plunger.gameObject);
        configurable.connectedBody = frameBody;
        configurable.autoConfigureConnectedAnchor = true;
        configurable.axis = Vector3.right;
        configurable.secondaryAxis = Vector3.up;
        configurable.xMotion = ConfigurableJointMotion.Locked;
        configurable.yMotion = ConfigurableJointMotion.Limited;
        configurable.zMotion = ConfigurableJointMotion.Locked;
        configurable.angularXMotion = ConfigurableJointMotion.Locked;
        configurable.angularYMotion = ConfigurableJointMotion.Locked;
        configurable.angularZMotion = ConfigurableJointMotion.Locked;
        configurable.configuredInWorldSpace = false;
        configurable.enableCollision = false;
        configurable.linearLimit = new SoftJointLimit { limit = 0.005f, bounciness = 0f, contactDistance = 0.0005f };
        configurable.linearLimitSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
        configurable.targetPosition = Vector3.zero;
        configurable.xDrive = NoDrive();
        configurable.zDrive = NoDrive();
        configurable.yDrive = NoDrive();
        var debug = Add<DetentPlungerDebug>(plunger.gameObject);
        debug.CaptureInitialPosition();

        var rotorBody = Add<Rigidbody>(rotor.gameObject);
        rotorBody.mass = 0.25f;
        rotorBody.useGravity = true;
        rotorBody.angularDamping = 0f;
        rotorBody.ResetCenterOfMass();
        rotorBody.isKinematic = false;
        rotorBody.interpolation = RigidbodyInterpolation.Interpolate;
        rotorBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rotorBody.constraints = RigidbodyConstraints.None;

        Vector3 axle = new Vector3(0.09581f, 0.041f, 2.95041f);
        var hinge = Add<HingeJoint>(rotor.gameObject);
        hinge.connectedBody = frameBody;
        hinge.autoConfigureConnectedAnchor = true;
        hinge.anchor = axle;
        hinge.axis = Vector3.forward;
        hinge.useLimits = false;
        hinge.enableCollision = false;
        Add<LeverRotorTorqueTest>(rotor.gameObject);

        var colliders = NewChild(rotor, "GearColliders");
        colliders.transform.localPosition = axle;
        const int toothCount = 12;
        const float outerRadius = 0.0242f;
        const float radialDepth = 0.0045f;
        for (int i = 0; i < toothCount; i++)
        {
            float angle = i * 360f / toothCount;
            float radians = angle * Mathf.Deg2Rad;
            var tooth = NewChild(colliders.transform, $"ToothCollider_{i:00}");
            tooth.SetActive(i != 1 && i != 2 && i != 3 && i != 4 && i != 8 && i != 9);
            tooth.transform.localPosition = new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f) * (outerRadius - radialDepth * 0.5f);
            tooth.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            var box = Add<BoxCollider>(tooth);
            box.size = new Vector3(radialDepth, 0.007f, 0.024f);
            box.material = material;
        }

        EditorUtility.SetDirty(frame.gameObject);
        EditorUtility.SetDirty(rotor.gameObject);
        EditorUtility.SetDirty(plunger.gameObject);
        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorSceneManager.SaveScene(root.scene);
        Undo.CollapseUndoOperations(group);
        Debug.Log("Lever detent physics setup completed and scene saved.");
    }

    static JointDrive NoDrive() => new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };

    static void ApplyContactInset(Transform root)
    {
        Directory.CreateDirectory("Assets/RenderTextures");
        var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(ContactViewPath);
        if (!texture)
        {
            texture = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32)
            {
                name = "LeverContactCloseup",
                antiAliasing = 4,
                filterMode = FilterMode.Bilinear
            };
            AssetDatabase.CreateAsset(texture, ContactViewPath);
        }

        var cameraTransform = root.Find("ContactCloseupCamera");
        if (!cameraTransform)
            cameraTransform = NewChild(root, "ContactCloseupCamera").transform;
        cameraTransform.position = new Vector3(0f, 0.028f, 0.3f);
        cameraTransform.rotation = Quaternion.Euler(0f, 180f, 0f);
        var closeupCamera = Add<Camera>(cameraTransform.gameObject);
        cameraTransform.gameObject.SetActive(true);
        closeupCamera.fieldOfView = 8.8f;
        closeupCamera.nearClipPlane = 0.01f;
        closeupCamera.farClipPlane = 10f;
        closeupCamera.clearFlags = CameraClearFlags.SolidColor;
        closeupCamera.backgroundColor = new Color(0.12f, 0.15f, 0.17f, 1f);
        closeupCamera.targetTexture = null;
        closeupCamera.rect = new Rect(0.54f, 0.40f, 0.132f, 0.18f);
        closeupCamera.depth = 10f;
        closeupCamera.allowHDR = true;
        closeupCamera.allowMSAA = true;
        var urpCameraData = Add<UniversalAdditionalCameraData>(cameraTransform.gameObject);
        urpCameraData.renderType = CameraRenderType.Base;
        urpCameraData.renderPostProcessing = false;
        urpCameraData.allowXRRendering = false;

        var obsoleteCanvas = root.Find("ContactInsetCanvas");
        if (obsoleteCanvas) Undo.DestroyObjectImmediate(obsoleteCanvas.gameObject);
        var obsoleteInset = root.GetComponent<LeverContactInset>();
        if (obsoleteInset) Undo.DestroyObjectImmediate(obsoleteInset);
        EditorUtility.SetDirty(cameraTransform);
        EditorUtility.SetDirty(closeupCamera);
        EditorUtility.SetDirty(urpCameraData);
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        EditorSceneManager.SaveScene(root.gameObject.scene);
    }

    static void CreateContactInsetCanvas(Transform root, RenderTexture texture)
    {
        var existing = root.Find("ContactInsetCanvas");
        GameObject canvasObject;
        if (!existing)
        {
            canvasObject = new GameObject("ContactInsetCanvas", typeof(RectTransform), typeof(Canvas),
                                          typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create contact inset canvas");
            canvasObject.transform.SetParent(root, false);
        }
        else canvasObject = existing.gameObject;

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(2560f, 1440f);
        scaler.matchWidthOrHeight = 0f;

        var border = canvasObject.transform.Find("ContactInsetBorder") as RectTransform;
        if (!border)
        {
            var borderObject = new GameObject("ContactInsetBorder", typeof(RectTransform), typeof(Image));
            Undo.RegisterCreatedObjectUndo(borderObject, "Create contact inset border");
            borderObject.transform.SetParent(canvasObject.transform, false);
            border = borderObject.GetComponent<RectTransform>();
        }
        border.anchorMin = new Vector2(1f, 0.5f);
        border.anchorMax = new Vector2(1f, 0.5f);
        border.pivot = new Vector2(1f, 0.5f);
        border.anchoredPosition = new Vector2(-24f, 0f);
        border.sizeDelta = new Vector2(648f, 488f);
        border.GetComponent<Image>().color = new Color(0.02f, 0.035f, 0.045f, 0.98f);

        var view = border.Find("ContactInsetView") as RectTransform;
        if (!view)
        {
            var viewObject = new GameObject("ContactInsetView", typeof(RectTransform), typeof(RawImage));
            Undo.RegisterCreatedObjectUndo(viewObject, "Create contact inset view");
            viewObject.transform.SetParent(border, false);
            view = viewObject.GetComponent<RectTransform>();
        }
        view.anchorMin = Vector2.zero;
        view.anchorMax = Vector2.one;
        view.offsetMin = new Vector2(4f, 4f);
        view.offsetMax = new Vector2(-4f, -4f);
        view.GetComponent<RawImage>().texture = texture;
        view.GetComponent<RawImage>().color = Color.white;
        EditorUtility.SetDirty(canvasObject);
        EditorUtility.SetDirty(border);
        EditorUtility.SetDirty(view);
    }

    static void TuneLeverDynamics(Transform rotor, Transform plunger)
    {
        var joint = plunger.GetComponent<ConfigurableJoint>();
        if (joint)
        {
            joint.yDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0f,
                maximumForce = 0f
            };
            EditorUtility.SetDirty(joint);
        }

        var torqueTest = rotor.GetComponent<LeverRotorTorqueTest>();
        if (torqueTest)
        {
            var serialized = new SerializedObject(torqueTest);
            serialized.FindProperty("torque").floatValue = 1f;
            serialized.FindProperty("torqueRampSeconds").floatValue = 3f;
            serialized.FindProperty("torqueReleaseSeconds").floatValue = 1f;
            serialized.FindProperty("contactDampingCoefficient").floatValue = 1f;
            serialized.FindProperty("contactDampingApproachAngleDegrees").floatValue = 10f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(torqueTest);
        }
        EditorSceneManager.MarkSceneDirty(rotor.gameObject.scene);
        EditorSceneManager.SaveScene(rotor.gameObject.scene);
    }

    static void TuneDetentContactGeometry(Transform rotor, Transform plunger)
    {
        var tip = plunger.Find("ContactTip");
        var gearColliders = rotor.Find("GearColliders");
        if (!tip || !gearColliders) return;

        // The sphere's top, rather than its center, must coincide with the visible part4 tip.
        var sphere = tip.GetComponent<SphereCollider>();
        if (sphere)
        {
            sphere.radius = 0.0015f;
            sphere.contactOffset = 0.00005f;
            tip.position = new Vector3(-0.0002f, 0.0152f, 0f); // top = 0.0167 m
            EditorUtility.SetDirty(sphere);
        }

        // Put a valley at the plunger in the rest pose. Teeth arrive progressively as the rotor turns.
        const int toothCount = 12;
        const float toothRadius = 0.003f;
        const float toothCenterRadius = 0.0217f;
        const float halfToothOffset = 15f;
        var contactMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);
        for (int i = 0; i < toothCount; i++)
        {
            var tooth = gearColliders.Find($"ToothCollider_{i:00}");
            if (!tooth) continue;
            tooth.gameObject.SetActive(i != 1 && i != 2 && i != 3 && i != 4 && i != 8 && i != 9);
            float angle = i * 360f / toothCount + halfToothOffset;
            float radians = angle * Mathf.Deg2Rad;
            tooth.localPosition = new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f) * toothCenterRadius;
            tooth.localRotation = Quaternion.identity;
            var oldBox = tooth.GetComponent<BoxCollider>();
            if (oldBox) Undo.DestroyObjectImmediate(oldBox);
            var toothSphere = tooth.GetComponent<SphereCollider>();
            if (!toothSphere) toothSphere = Undo.AddComponent<SphereCollider>(tooth.gameObject);
            toothSphere.radius = toothRadius;
            toothSphere.contactOffset = 0.00005f;
            toothSphere.material = contactMaterial;
            EditorUtility.SetDirty(toothSphere);
            EditorUtility.SetDirty(tooth);
        }
        EditorSceneManager.MarkSceneDirty(rotor.gameObject.scene);
        EditorSceneManager.SaveScene(rotor.gameObject.scene);
    }

    static void ApplyPart2Glass(Transform part2)
    {
        if (!part2) return;
        var source = part2.GetComponentInChildren<MeshRenderer>();
        var sourceFilter = source ? source.GetComponent<MeshFilter>() : null;
        if (!source || !sourceFilter || !sourceFilter.sharedMesh) return;
        Directory.CreateDirectory("Assets/Materials");
        var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassMaterialPath);
        var glassShader = Shader.Find("Lever/Glass");
        if (!glassShader) return;
        if (!glass)
        {
            glass = new Material(glassShader) { name = "Part2Glass", renderQueue = 3000 };
            AssetDatabase.CreateAsset(glass, GlassMaterialPath);
        }
        glass.shader = glassShader;
        glass.SetColor("_BaseColor", new Color(0.35f, 0.72f, 0.88f, 0.1f));
        glass.SetColor("_RimColor", new Color(0.55f, 0.9f, 1f, 0.28f));
        glass.SetFloat("_RimPower", 4f);
        glass.renderQueue = 3000;
        EditorUtility.SetDirty(glass);
        var materials = new Material[source.sharedMaterials.Length];
        for (int i = 0; i < materials.Length; i++) materials[i] = glass;
        source.sharedMaterials = materials;

        var outlineShader = Shader.Find("Lever/Part Outline");
        if (!outlineShader) return;
        var outlineMaterial = AssetDatabase.LoadAssetAtPath<Material>(OutlineMaterialPath);
        if (!outlineMaterial)
        {
            outlineMaterial = new Material(outlineShader) { name = "Part2Outline" };
            outlineMaterial.SetColor("_OutlineColor", new Color(0.06f, 0.16f, 0.22f, 1f));
            outlineMaterial.SetFloat("_OutlineWidth", 0.00008f);
            AssetDatabase.CreateAsset(outlineMaterial, OutlineMaterialPath);
        }
        var outline = source.transform.parent.Find("Part2Outline");
        if (!outline)
        {
            var outlineObject = NewChild(source.transform.parent, "Part2Outline");
            outline = outlineObject.transform;
            outline.localPosition = source.transform.localPosition;
            outline.localRotation = source.transform.localRotation;
            outline.localScale = source.transform.localScale;
            Add<MeshFilter>(outlineObject).sharedMesh = sourceFilter.sharedMesh;
            Add<MeshRenderer>(outlineObject).sharedMaterial = outlineMaterial;
        }
        EditorUtility.SetDirty(source);
        EditorSceneManager.MarkSceneDirty(part2.gameObject.scene);
        EditorSceneManager.SaveScene(part2.gameObject.scene);
    }

    static PhysicsMaterial GetOrCreateMaterial()
    {
        var result = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);
        if (result) return result;
        Directory.CreateDirectory("Assets/Physics");
        result = new PhysicsMaterial("LeverContactMaterial")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        AssetDatabase.CreateAsset(result, MaterialPath);
        return result;
    }

    static void AddFrameCollider(Transform part)
    {
        if (!part || part.GetComponentInChildren<Collider>()) return;
        var renderer = part.GetComponentInChildren<Renderer>();
        if (!renderer) return;
        var proxy = NewChild(part, "FrameCollider");
        proxy.transform.position = renderer.bounds.center;
        var box = Add<BoxCollider>(proxy);
        box.size = new Vector3(renderer.bounds.size.x, renderer.bounds.size.y, renderer.bounds.size.z);
    }

    static GameObject NewChild(Transform parent, string name)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static T Add<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        return existing ? existing : Undo.AddComponent<T>(go);
    }
}
