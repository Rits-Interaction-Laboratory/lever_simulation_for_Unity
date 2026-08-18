using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class GearRotorMassProperties : MonoBehaviour
{
    [SerializeField] Vector3 calculatedCenterOfMassLocal;
    [SerializeField] double calculatedVolume;
    [SerializeField] int meshesUsed;
    [SerializeField] string calculationStatus = "Not calculated";

    public Vector3 CalculatedCenterOfMassLocal => calculatedCenterOfMassLocal;
    public double CalculatedVolume => calculatedVolume;
    public int MeshesUsed => meshesUsed;
    public string CalculationStatus => calculationStatus;

    void Awake()
    {
        ApplyToRigidbody();
    }

    public bool RecalculateFromVisibleMeshes()
    {
        double totalVolume = 0.0;
        Vector3 weightedCentroid = Vector3.zero;
        int validMeshCount = 0;

        foreach (MeshFilter filter in GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (!mesh)
                continue;

            try
            {
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Matrix4x4 toRotorLocal = transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                double signedVolume = 0.0;
                Vector3 signedMoment = Vector3.zero;

                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = toRotorLocal.MultiplyPoint3x4(vertices[triangles[i]]);
                    Vector3 b = toRotorLocal.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    Vector3 c = toRotorLocal.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                    double tetraVolume = Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                    signedVolume += tetraVolume;
                    signedMoment += (a + b + c) * ((float)tetraVolume / 4f);
                }

                if (Math.Abs(signedVolume) < 1e-12)
                    continue;

                Vector3 meshCentroid = signedMoment / (float)signedVolume;
                double meshVolume = Math.Abs(signedVolume);
                weightedCentroid += meshCentroid * (float)meshVolume;
                totalVolume += meshVolume;
                validMeshCount++;
            }
            catch (Exception exception)
            {
                calculationStatus = $"Failed to read {filter.name}: {exception.Message}";
                return false;
            }
        }

        if (validMeshCount == 0 || totalVolume <= 0.0)
        {
            calculationStatus = "No closed readable mesh with non-zero volume was found.";
            return false;
        }

        calculatedCenterOfMassLocal = weightedCentroid / (float)totalVolume;
        calculatedVolume = totalVolume;
        meshesUsed = validMeshCount;
        calculationStatus = "Uniform-density volume centroid calculated successfully.";
        ApplyToRigidbody();
        return true;
    }

    public void ApplyToRigidbody()
    {
        Rigidbody body = GetComponent<Rigidbody>();
        if (body && meshesUsed > 0)
            body.centerOfMass = calculatedCenterOfMassLocal;
    }
}
