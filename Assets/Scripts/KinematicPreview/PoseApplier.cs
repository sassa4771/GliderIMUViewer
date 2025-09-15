using UnityEngine;

public class PoseApplier : MonoBehaviour
{
    public PoseCsvStore store;
    public PosePlaybackController controller;
    public bool applyInLocalSpace = true;
    [Min(0)] public float smoothingTime = 0.05f;

    Quaternion _smoothed;
    bool _init = false;

    void Update()
    {
        if (store == null || controller == null || !store.IsLoaded) return;

        Quaternion qTarget = store.EvaluateQuat(controller.TimeSec);

        if (!_init) { _smoothed = applyInLocalSpace ? transform.localRotation : transform.rotation; _init = true; }
        if (smoothingTime > 0f)
        {
            float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, smoothingTime));
            _smoothed = Quaternion.Slerp(_smoothed, qTarget, a);
        }
        else _smoothed = qTarget;

        if (applyInLocalSpace) transform.localRotation = _smoothed;
        else transform.rotation = _smoothed;
    }
}
