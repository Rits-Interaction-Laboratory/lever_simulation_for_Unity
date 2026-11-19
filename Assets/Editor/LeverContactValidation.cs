using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class LeverContactValidation
{
    public static string Run(float spring, float damper, float extension, float offset, float speedDegrees, float startAngle = -60f, float endAngle = 60f)
    {
        Scene scene;
        // A separate local physics scene keeps the user's mechanism untouched.

        scene = SceneManager.CreateScene("ContactValidation_" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        var physics = scene.GetPhysicsScene();
        var rotor = GameObject.Find("GearRotor");
        var source = rotor.transform.Find("GearColliders");
        var hinge = rotor.GetComponent<HingeJoint>();
        Vector3 pivot = rotor.transform.TransformPoint(hinge.anchor);
        var cam = new GameObject("ValidationCam");
        SceneManager.MoveGameObjectToScene(cam, scene);
        cam.transform.position = pivot;
        var rb = cam.AddComponent<Rigidbody>(); rb.isKinematic = true;
        foreach (var original in source.GetComponentsInChildren<MeshCollider>())
        {
            var obj = new GameObject(original.name); obj.transform.SetParent(cam.transform, false);
            obj.transform.position = original.transform.position;
            obj.transform.rotation = original.transform.rotation;
            obj.transform.localScale = original.transform.lossyScale;
            var c = obj.AddComponent<MeshCollider>(); c.convex = true; c.sharedMesh = original.sharedMesh;
            c.sharedMaterial = original.sharedMaterial; c.contactOffset = offset;
        }
        var originalTip = GameObject.Find("DetentPlunger").transform.Find("ContactTip").GetComponent<SphereCollider>();
        var follower = new GameObject("ValidationFollower"); SceneManager.MoveGameObjectToScene(follower, scene);
        follower.transform.position = originalTip.transform.TransformPoint(originalTip.center);
        var body = follower.AddComponent<Rigidbody>(); body.mass = 0.07f; body.useGravity = false;
        body.constraints = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotation;
        body.solverIterations = 20; body.solverVelocityIterations = 10; body.sleepThreshold = 0f;
        var tip = follower.AddComponent<SphereCollider>(); tip.radius = originalTip.radius; tip.sharedMaterial = originalTip.sharedMaterial; tip.contactOffset = offset;
        var smoothSource = source.GetComponent<LeverCamSmoothContact>();
        if (smoothSource && smoothSource.enabled) cam.AddComponent<LeverCamSmoothContact>().Configure(cam.transform, tip);
        float rest = body.position.y + extension;
        const float dt = 0.0005f;
        var camColliders = cam.GetComponentsInChildren<Collider>();
        var probe = follower.AddComponent<LeverContactMeasurement>(); probe.Pivot = pivot;
        int samples=0, gaps=0, flips=0, zeroImpulse=0, contactLoss=0; float lastTorque=0, maxVelocity=0, maxForce=0, torqueVariation=0;
        float minY=float.PositiveInfinity,maxY=float.NegativeInfinity;
        float previousTorqueDelta=0, maxTorqueStep=0; double torqueSecondDifferenceSquared=0;
        var trace = new System.Text.StringBuilder("time_s,angle_deg,torque_nm,force_n,tip_y_m,tip_velocity_m_s\n");
        try
        {
            int total=(int)((Mathf.Abs(endAngle-startAngle) / Mathf.Abs(speedDegrees)+0.5f)/dt);
            for(int i=0;i<total;i++)
            {
                float angle = startAngle + Mathf.Max(0f,i*dt-0.5f)*speedDegrees;
                rb.MoveRotation(Quaternion.AngleAxis(angle,Vector3.forward));
                float force=Mathf.Clamp(spring*Mathf.Max(0,rest-body.position.y)-damper*body.linearVelocity.y,0f,50f);
                body.AddForce(Vector3.up*force);
                if(body.position.y>rest) { body.position=new Vector3(body.position.x,rest,body.position.z); body.linearVelocity=Vector3.zero; }
                probe.AngularImpulse=0; probe.ContactCount=0; physics.Simulate(dt);
                if(i*dt<0.5f)continue;
                int n=0; float torque=0f; foreach(var c in camColliders) { Vector3 q=c.ClosestPoint(body.position); Vector3 delta=body.position-q; if(delta.magnitude <= tip.radius+0.0001f) { n++; if(delta.sqrMagnitude>1e-12f) torque += Vector3.Cross(q-pivot,-delta.normalized*force).z; } }
                
                torque = probe.AngularImpulse / dt; samples++; if(probe.ContactCount==0)contactLoss++; if(n==0)gaps++; if(Mathf.Abs(torque)<0.00001f)zeroImpulse++;
                if(Mathf.Abs(torque)>0.001f && Mathf.Abs(lastTorque)>0.001f && torque*lastTorque<0)flips++;
                float torqueDelta=torque-lastTorque; if(samples>2) { torqueSecondDifferenceSquared+=(torqueDelta-previousTorqueDelta)*(torqueDelta-previousTorqueDelta); maxTorqueStep=Mathf.Max(maxTorqueStep,Mathf.Abs(torqueDelta)); } previousTorqueDelta=torqueDelta; torqueVariation+=Mathf.Abs(torqueDelta); lastTorque=torque;
                trace.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}",i*dt,angle,torque,force,body.position.y,body.linearVelocity.y));
                maxVelocity=Mathf.Max(maxVelocity,Mathf.Abs(body.linearVelocity.y));maxForce=Mathf.Max(maxForce,force);
                minY=Mathf.Min(minY,body.position.y);maxY=Mathf.Max(maxY,body.position.y);
            }
            string folder=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"..","ContactValidation")); System.IO.Directory.CreateDirectory(folder); string tracePath=System.IO.Path.Combine(folder,"contact_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_ffff")+".csv"); System.IO.File.WriteAllText(tracePath,trace.ToString());
            return $"trace={tracePath} secondDiffRms={Math.Sqrt(torqueSecondDifferenceSquared/Math.Max(1,samples-2)):F6} maxStep={maxTorqueStep:F6} k={spring} c={damper} extension={extension} offset={offset} speed={speedDegrees} samples={samples} contactLoss={100f*contactLoss/samples:F3}% geometryProximityGap={100f*gaps/samples:F3}% zeroTorque={100f*zeroImpulse/samples:F3}% flips={flips} torqueTV={torqueVariation:F4} maxV={maxVelocity:F4} forceMax={maxForce:F3} tipY=[{minY:F6},{maxY:F6}]";
        }
        finally { UnityEngine.Object.DestroyImmediate(cam); UnityEngine.Object.DestroyImmediate(follower); SceneManager.UnloadSceneAsync(scene); }
    }
}
