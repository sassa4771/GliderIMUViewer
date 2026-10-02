using UnityEngine;

/// <summary>
/// RealtimePoseStore の最新姿勢をこの Transform に反映する（PoseApplier のリアルタイム版）。
/// </summary>
public class RealtimePoseApplier : MonoBehaviour
{
    public RealtimePoseStore store;
    public bool applyInLocalSpace = true;
    [Tooltip("指数平滑の時定数[s]。0 で受信値をそのまま反映。")]
    [Min(0)] public float smoothingTime = 0.05f;

    Quaternion _smoothed;
    bool _init = false;
    bool _snap = false;

    void OnEnable()
    {
        if (store) store.Zeroed += OnZeroed;
    }

    void OnDisable()
    {
        if (store) store.Zeroed -= OnZeroed;
    }

    // ゼロ点を取り直した直後は、補間せずに一気に合わせる
    void OnZeroed() { _snap = true; }

    void Update()
    {
        if (store == null || !store.HasPose) return;

        Quaternion qTarget = store.Rotation;

        if (!_init) { _smoothed = qTarget; _init = true; }
        if (smoothingTime > 0f && !_snap)
        {
            float a = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(1e-4f, smoothingTime));
            _smoothed = Quaternion.Slerp(_smoothed, qTarget, a);
        }
        else _smoothed = qTarget;
        _snap = false;

        if (applyInLocalSpace) transform.localRotation = _smoothed;
        else transform.rotation = _smoothed;
    }
}
