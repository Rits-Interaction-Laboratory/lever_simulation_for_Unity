using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class GearRotorMassProperties : MonoBehaviour
{
    [SerializeField] Vector3 calculatedCenterOfMassLocal;
    [SerializeField] Vector3 calculatedPrincipalInertia;
    [SerializeField] Quaternion calculatedInertiaTensorRotation = Quaternion.identity;
    [SerializeField] double calculatedVolume;
    [SerializeField] int meshesUsed;
    [SerializeField] string calculationStatus = "Not calculated";

    public Vector3 CalculatedCenterOfMassLocal => calculatedCenterOfMassLocal;
    public Vector3 CalculatedPrincipalInertia => calculatedPrincipalInertia;
    public Quaternion CalculatedInertiaTensorRotation => calculatedInertiaTensorRotation;
    public double CalculatedVolume => calculatedVolume;
    public int MeshesUsed => meshesUsed;
    public string CalculationStatus => calculationStatus;

    struct Integrals
    {
        public double v, x, y, z, xx, yy, zz, xy, xz, yz;
        public void Add(Integrals value, double scale)
        {
            v += value.v * scale;
            x += value.x * scale; y += value.y * scale; z += value.z * scale;
            xx += value.xx * scale; yy += value.yy * scale; zz += value.zz * scale;
            xy += value.xy * scale; xz += value.xz * scale; yz += value.yz * scale;
        }
    }

    void Awake()
    {
        if (HasCalculatedInertia())
            ApplyToRigidbody();
        else
            RecalculateFromVisibleMeshes();
    }

    public bool RecalculateFromVisibleMeshes()
    {
        Integrals total = default;
        int validMeshCount = 0;
        foreach (MeshFilter filter in GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter.sharedMesh) continue;
            try
            {
                Integrals part = Integrate(
                    filter.sharedMesh,
                    transform.worldToLocalMatrix * filter.transform.localToWorldMatrix);
                if (Math.Abs(part.v) < 1e-12) continue;
                total.Add(part, Math.Sign(part.v));
                validMeshCount++;
            }
            catch (Exception exception)
            {
                calculationStatus = $"Failed to read {filter.name}: {exception.Message}";
                return false;
            }
        }

        Rigidbody body = GetComponent<Rigidbody>();
        if (validMeshCount == 0 || total.v <= 0.0)
        {
            calculationStatus = "No closed readable mesh with non-zero volume was found.";
            return false;
        }
        if (!body || body.mass <= 0f)
        {
            calculationStatus = "GearRotor requires a Rigidbody with positive mass.";
            return false;
        }

        double mass = body.mass;
        double density = mass / total.v;
        double cx = total.x / total.v, cy = total.y / total.v, cz = total.z / total.v;
        double[,] inertia =
        {
            { density * (total.yy + total.zz) - mass * (cy * cy + cz * cz),
              -density * total.xy + mass * cx * cy,
              -density * total.xz + mass * cx * cz },
            { -density * total.xy + mass * cx * cy,
              density * (total.xx + total.zz) - mass * (cx * cx + cz * cz),
              -density * total.yz + mass * cy * cz },
            { -density * total.xz + mass * cx * cz,
              -density * total.yz + mass * cy * cz,
              density * (total.xx + total.yy) - mass * (cx * cx + cy * cy) }
        };

        if (!Diagonalize(inertia, out Vector3 principal, out Quaternion rotation))
        {
            calculationStatus = "Calculated inertia tensor is not positive and finite.";
            return false;
        }

        calculatedCenterOfMassLocal = new Vector3((float)cx, (float)cy, (float)cz);
        calculatedPrincipalInertia = principal;
        calculatedInertiaTensorRotation = rotation;
        calculatedVolume = total.v;
        meshesUsed = validMeshCount;
        calculationStatus = "Uniform-density centroid and inertia tensor calculated successfully.";
        ApplyToRigidbody();
        return true;
    }

    static Integrals Integrate(Mesh mesh, Matrix4x4 toLocal)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        Integrals sum = default;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector3 a = toLocal.MultiplyPoint3x4(vertices[triangles[i]]);
            Vector3 b = toLocal.MultiplyPoint3x4(vertices[triangles[i + 1]]);
            Vector3 c = toLocal.MultiplyPoint3x4(vertices[triangles[i + 2]]);
            double v = Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            sum.v += v;
            sum.x += v * (a.x + b.x + c.x) / 4.0;
            sum.y += v * (a.y + b.y + c.y) / 4.0;
            sum.z += v * (a.z + b.z + c.z) / 4.0;
            sum.xx += Second(v, a.x, b.x, c.x);
            sum.yy += Second(v, a.y, b.y, c.y);
            sum.zz += Second(v, a.z, b.z, c.z);
            sum.xy += Product(v, a.x, a.y, b.x, b.y, c.x, c.y);
            sum.xz += Product(v, a.x, a.z, b.x, b.z, c.x, c.z);
            sum.yz += Product(v, a.y, a.z, b.y, b.z, c.y, c.z);
        }
        return sum;
    }

    static double Second(double v, double a, double b, double c) =>
        v * (a * a + b * b + c * c + a * b + a * c + b * c) / 10.0;

    static double Product(
        double v, double ax, double ay, double bx, double by, double cx, double cy) =>
        v * (2.0 * (ax * ay + bx * by + cx * cy) +
             ax * by + ay * bx + ax * cy + ay * cx + bx * cy + by * cx) / 20.0;

    static bool Diagonalize(double[,] a, out Vector3 inertia, out Quaternion rotation)
    {
        double[,] vectors = { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };
        for (int iteration = 0; iteration < 32; iteration++)
        {
            int p = 0, q = 1;
            double largest = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > largest) { p = 0; q = 2; largest = Math.Abs(a[0, 2]); }
            if (Math.Abs(a[1, 2]) > largest) { p = 1; q = 2; largest = Math.Abs(a[1, 2]); }
            if (largest < 1e-12) break;

            double angle = 0.5 * Math.Atan2(2.0 * a[p, q], a[q, q] - a[p, p]);
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            for (int k = 0; k < 3; k++)
            {
                if (k == p || k == q) continue;
                double kp = a[k, p], kq = a[k, q];
                a[k, p] = a[p, k] = cosine * kp - sine * kq;
                a[k, q] = a[q, k] = sine * kp + cosine * kq;
            }
            double pp = a[p, p], qq = a[q, q], pq = a[p, q];
            a[p, p] = cosine * cosine * pp - 2.0 * sine * cosine * pq + sine * sine * qq;
            a[q, q] = sine * sine * pp + 2.0 * sine * cosine * pq + cosine * cosine * qq;
            a[p, q] = a[q, p] = 0.0;
            for (int k = 0; k < 3; k++)
            {
                double kp = vectors[k, p], kq = vectors[k, q];
                vectors[k, p] = cosine * kp - sine * kq;
                vectors[k, q] = sine * kp + cosine * kq;
            }
        }

        int[] order = { 0, 1, 2 };
        Array.Sort(order, (left, right) => a[left, left].CompareTo(a[right, right]));
        Vector3 x = Axis(vectors, order[0]);
        Vector3 y = Axis(vectors, order[1]);
        Vector3 z = Axis(vectors, order[2]);
        if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) z = -z;
        double i0 = a[order[0], order[0]], i1 = a[order[1], order[1]], i2 = a[order[2], order[2]];
        bool valid = Valid(i0) && Valid(i1) && Valid(i2);
        inertia = valid ? new Vector3((float)i0, (float)i1, (float)i2) : Vector3.zero;
        rotation = valid ? Quaternion.LookRotation(z, y) : Quaternion.identity;
        return valid;
    }

    static Vector3 Axis(double[,] vectors, int column) =>
        new Vector3((float)vectors[0, column], (float)vectors[1, column],
            (float)vectors[2, column]).normalized;

    static bool Valid(double value) =>
        value > 1e-12 && !double.IsNaN(value) && !double.IsInfinity(value);

    bool HasCalculatedInertia() =>
        meshesUsed > 0 &&
        calculatedPrincipalInertia.x > 0f &&
        calculatedPrincipalInertia.y > 0f &&
        calculatedPrincipalInertia.z > 0f;

    public void ApplyToRigidbody()
    {
        Rigidbody body = GetComponent<Rigidbody>();
        if (!body || meshesUsed <= 0 ||
            calculatedPrincipalInertia.x <= 0f ||
            calculatedPrincipalInertia.y <= 0f ||
            calculatedPrincipalInertia.z <= 0f)
            return;

        body.automaticCenterOfMass = false;
        body.automaticInertiaTensor = false;
        body.centerOfMass = calculatedCenterOfMassLocal;
        body.inertiaTensorRotation = calculatedInertiaTensorRotation;
        body.inertiaTensor = calculatedPrincipalInertia;
    }
}
