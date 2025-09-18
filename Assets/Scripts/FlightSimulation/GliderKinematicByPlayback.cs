using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Playback の時刻に同期し、移動は常に transform.Translate(..., Space.Self) でローカルZに沿って行う軽量キネマティクス。
/// - [launchStart, launchEnd]の加速度を積分して初速 v0 を推定
/// - t <= launchEnd は原点で待機、t > launchEnd は減衰付きで前進
/// - 姿勢はCSVから毎フレーム同期（必要なければ followRotationFromCsv=false）
/// </summary>
[DisallowMultipleComponent]
public class GliderKinematicByPlayback : MonoBehaviour
{
    [Header("Refs")]
    public PoseCsvStore store;                    // 必須
    public PosePlaybackController playback;       // スライダー時刻

    [Header("Launch Window [sec]")]
    public float launchStart = 12.6f;
    public float launchEnd   = 13.2f;

    public enum DecayMode { None, Exponential, Quadratic }

    [Header("Initial Speed from CSV")]
    [Tooltip("v0 に掛ける係数（強ければ下げる）")]
    public float v0Scale = 0.6f;
    [Tooltip("初速上限 [m/s]")]
    public float v0Max = 15f;
    [Tooltip("この前進速度未満は 0 とみなす（にじみ防止）")]
    public float v0Deadband = 0.5f;

    [Header("Speed Decay")]
    public DecayMode decay = DecayMode.Exponential;
    [Tooltip("指数減衰: v(t)=v0*e^{-k t} の k [1/s]")]
    public float expK = 0.15f;
    [Tooltip("二乗減衰: dv/dt=-k v^2 の k [1/m]")]
    public float quadK = 0.02f;
    [Tooltip("最低速度 [m/s]")]
    public float minSpeed = 0.2f;

    [Header("Rotation Follow")]
    [Tooltip("CSVの姿勢を毎フレーム適用（ON推奨）")]
    public bool followRotationFromCsv = true;
    [Tooltip("リリース姿勢に加えるピッチ補正[deg]")]
    public float addPitchOffsetDeg = 0f;

    [Header("Origin")]
    [Tooltip("発射位置の基準（未指定なら現在位置を採用）")]
    public Transform launchOrigin;

    // ---- 内部状態 ----
    Vector3 originPosWorld;
    Quaternion q0;              // 発射時の姿勢（生＋任意ピッチ）
    Vector3 fwd0World;          // q0.forward
    float v0Forward;            // q0.forward 方向の初速
    bool armed;

    float lastTimeSec = -1f;    // 前フレームの再生時刻
    float lastS = 0f;           // 前フレーム時点までの累積距離 s(tau)

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb) { rb.isKinematic = true; rb.detectCollisions = true; }
        if (!launchOrigin) launchOrigin = transform;
    }

    [ContextMenu("Calibrate From Store (capture origin & v0/q0)")]
    public void Calibrate()
    {
        if (store == null || !store.IsLoaded)
        {
            Debug.LogWarning("[GliderKinematicByPlayback] store未読込");
            return;
        }

        float t0 = Mathf.Clamp(launchStart, 0f, store.Duration);
        float t1 = Mathf.Clamp(launchEnd,   t0 + 1e-3f, store.Duration);

        originPosWorld = launchOrigin ? launchOrigin.position : transform.position;

        q0 = store.GetRawQuatAt(t1);
        if (Mathf.Abs(addPitchOffsetDeg) > 0.001f)
            q0 = q0 * Quaternion.Euler(addPitchOffsetDeg, 0f, 0f);

        fwd0World = (q0 * Vector3.forward).normalized;

        Vector3 v0World = store.EstimateInitialVelocity(t0, t1) * Mathf.Max(0f, v0Scale);
        if (v0World.magnitude > Mathf.Max(0.1f, v0Max)) v0World = v0World.normalized * v0Max;

        // ローカルZ(=q0.forward)成分だけを初速として採用（後ろ向き防止でMax(0,Dot)）
        v0Forward = Mathf.Max(0f, Vector3.Dot(v0World, fwd0World));
        if (v0Forward < v0Deadband) v0Forward = 0f;

        transform.position = originPosWorld;
        transform.rotation = q0;

        lastTimeSec = -1f;
        lastS = 0f;

        armed = true;
        Debug.Log($"[GliderKinematicByPlayback] Calibrated: v0Fwd={v0Forward:F2} m/s, q0Euler={q0.eulerAngles}, origin={originPosWorld}");
    }

    void Update()
    {
        if (store == null || playback == null || !store.IsLoaded) return;
        if (!armed) Calibrate();

        float tNow = Mathf.Clamp(playback.TimeSec, 0f, store.Duration);
        float t1 = Mathf.Clamp(launchEnd, 0f, store.Duration);

        // 発射前 or 大きく巻き戻し/ジャンプ → 原点にリセット
        bool needReset =
            lastTimeSec < 0f ||
            tNow <= t1 + 1e-6f ||
            tNow < lastTimeSec - 1e-3f ||
            Mathf.Abs(tNow - lastTimeSec) > 0.5f;

        if (needReset)
        {
            transform.position = originPosWorld;
            transform.rotation = followRotationFromCsv ? store.GetRawQuatAt(Mathf.Min(tNow, t1)) : q0;
            lastTimeSec = tNow;
            lastS = 0f;
            return;
        }

        // 姿勢をCSVに同期（任意）
        if (followRotationFromCsv)
            transform.rotation = store.GetRawQuatAt(tNow);

        // 発射後の経過時間
        float tauNow = Mathf.Max(0f, tNow - t1);

        // 累積距離 s(tau) → 差分だけ Self 方向に押し出す（★ここが本題★）
        float sNow = DistanceFromTau(tauNow);
        float ds = Mathf.Max(0f, sNow - lastS);
        if (ds > 0f)
            transform.Translate(-Vector3.forward * ds, Space.Self);

        lastS = sNow;
        lastTimeSec = tNow;
    }

    // ---- 距離・速度の解析式 ----
    float DistanceFromTau(float tau)
    {
        switch (decay)
        {
            case DecayMode.None:
                return v0Forward * tau;
            case DecayMode.Exponential:
            {
                float k = Mathf.Max(1e-5f, expK);
                return v0Forward / k * (1f - Mathf.Exp(-k * tau));
            }
            case DecayMode.Quadratic:
            default:
            {
                float kq = Mathf.Max(1e-6f, quadK);
                return (v0Forward <= 1e-6f) ? 0f : (1f / kq) * Mathf.Log(1f + kq * v0Forward * tau);
            }
        }
    }
}
