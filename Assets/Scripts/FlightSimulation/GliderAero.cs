using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class GliderAero : MonoBehaviour
{
    public Rigidbody rb;

    [Header("Initial Conditions (will be overwritten by launcher)")]
    public Vector3 initialVelocityWorld = new Vector3(10f, 0.5f, 0f);
    public Quaternion initialRotationWorld = Quaternion.identity;

    [Header("Atmosphere")]
    public float rho = 1.225f;

    [Header("Geometry")]
    public float wingArea = 0.15f;     // [m^2]
    public float aspectRatio = 6f;     // b^2/S
    public float chordRef = 0.15f;     // 代表コード長（トルクスケール）

    [Header("Aerodynamics (pre-stall)")]
    [Tooltip("CL ≈ clAlpha * alpha(rad)（失速前）")]
    public float clAlpha = 5.0f;
    [Tooltip("零揚力抗力")]
    public float cd0 = 0.02f;
    [Tooltip("オズワルド効率")]
    public float oswaldE = 0.8f;

    [Header("Stall Model")]
    [Tooltip("失速迎角 [deg]（±で対称扱い）")]
    public float alphaStallDeg = 15f;
    [Tooltip("CL 最大（+/- 対称）")]
    public float clMax = 1.2f;
    [Tooltip("失速後のCL減衰（CLを徐々に落とす）")]
    public float postStallDrop = 0.5f; // 0..1

    [Header("Pitch Stability")]
    [Tooltip("Cm = cmAlpha*alpha - pitchDamping*qx")]
    public float cmAlpha = -0.5f;
    public float pitchDamping = 0.05f;

    [Header("Safety Limits")]
    [Tooltip("この速度を超えると動圧をこれ以上増やさない（発散防止）")]
    public float aeroSpeedClamp = 30f; // [m/s]
    [Tooltip("1フレームに与える合力上限")]
    public float maxForcePerStep = 50f; // [N]
    [Tooltip("1フレームに与えるトルク上限")]
    public float maxTorquePerStep = 10f; // [N·m]
    [Tooltip("機体速度に比例する簡易空力減衰（横滑りなどの雑揺れを抑える）")]
    public float linearVelocityDamping = 0.02f; // N·s/m 相当

    void Reset() { rb = GetComponent<Rigidbody>(); }

    void Start()
    {
        if (!rb) rb = GetComponent<Rigidbody>();
        rb.useGravity = true;
        rb.linearDamping = 0f;           // 抗力は自前
        rb.angularDamping = 0.02f;

        if (initialRotationWorld != Quaternion.identity)
            rb.MoveRotation(initialRotationWorld);
        if (initialVelocityWorld.sqrMagnitude > 0f)
            rb.linearVelocity = initialVelocityWorld;
    }

    void FixedUpdate()
    {
        Vector3 v = rb.linearVelocity;
        float V = v.magnitude;
        if (V < 0.05f) return;

        // 動圧（クランプ付き）
        float Veff = Mathf.Min(V, Mathf.Max(1f, aeroSpeedClamp));
        float qdyn = 0.5f * rho * Veff * Veff;

        // 速度を機体座標へ（+Z: 機体前方, +Y: 上）
        Vector3 vB = transform.InverseTransformDirection(v);
        // 空気の相対流は -vB。迎角は“機体の前方軸に対する上向き成分”
        float alpha = Mathf.Atan2(-vB.y, vB.z); // nose-up を + とする

        // 失速を含むCL近似
        float alphaStall = Mathf.Deg2Rad * Mathf.Max(1f, alphaStallDeg);
        float a = alpha;
        float CL_lin = clAlpha * a;
        float CL;
        if (Mathf.Abs(a) <= alphaStall)
        {
            CL = Mathf.Clamp(CL_lin, -clMax, clMax);
        }
        else
        {
            // 失速後はCLを鈍らせて落とす
            float sign = Mathf.Sign(a);
            float excess = Mathf.Abs(a) - alphaStall;
            float drop = Mathf.Clamp01(postStallDrop);
            float target = clMax * (1f - drop * Mathf.Clamp01(excess / (Mathf.PI * 0.5f - alphaStall)));
            CL = sign * target;
        }

        float kInduced = 1f / (Mathf.PI * Mathf.Max(0.1f, aspectRatio) * Mathf.Max(0.3f, oswaldE));
        float CD = cd0 + kInduced * CL * CL;

        // 機体座標の揚力・抗力（抗力は相対風方向、揚力は翼面法線）
        Vector3 rel = -vB.normalized; // 相対風（機体から見て前方なら -z）
        // 翼面の上向き単位ベクトル（機体+Y軸を、相対風に直交化）
        Vector3 liftDirB = Vector3.Cross(rel, Vector3.right); // 一旦X軸と作る
        if (liftDirB.sqrMagnitude < 1e-6f)
            liftDirB = Vector3.up; // 特異点回避
        liftDirB = Vector3.Cross(Vector3.Cross(rel, Vector3.right), rel).normalized;

        Vector3 dragB = -rel * (CD * qdyn * wingArea);
        Vector3 liftB = liftDirB * (CL * qdyn * wingArea);

        // 世界座標へ
        Vector3 F = transform.TransformDirection(liftB + dragB);

        // 簡易減衰（速度に比例）
        F += -linearVelocityDamping * v;

        // 力/トルクのクランプ
        if (F.magnitude > maxForcePerStep) F = F.normalized * maxForcePerStep;

        // ピッチモーメント（機体x回り、右ねじ）
        float pitchRate = transform.InverseTransformDirection(rb.angularVelocity).x;
        float Cm = cmAlpha * alpha - pitchDamping * pitchRate;
        float Mx = Cm * qdyn * wingArea * Mathf.Max(0.01f, chordRef);
        Vector3 torqueB = new Vector3(Mx, 0f, 0f);
        Vector3 T = transform.TransformDirection(torqueB);
        if (T.magnitude > maxTorquePerStep) T = T.normalized * maxTorquePerStep;

        rb.AddForce(F, ForceMode.Force);
        rb.AddTorque(T, ForceMode.Force);
    }
}
