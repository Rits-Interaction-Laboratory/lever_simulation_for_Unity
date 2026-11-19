using UnityEngine;
// Transient probe used by the isolated contact validation; never attached to the authored mechanism.
public sealed class LeverContactMeasurement : MonoBehaviour
{
    public Vector3 Pivot;
    public float AngularImpulse;
    public int ContactCount;
    void OnCollisionEnter(Collision c) => Measure(c);
    void OnCollisionStay(Collision c) => Measure(c);
    void Measure(Collision c)
    {
        ContactCount += c.contactCount;
        for(int i=0;i<c.contactCount;i++)
        {
            var p=c.GetContact(i);
            AngularImpulse += Vector3.Cross(p.point-Pivot,p.impulse).z;
        }
    }
}
