using UnityEngine;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
public class OrbitAroundOrigin : MonoBehaviour
{
    [Header("Pivot / Target")]
    public Vector3 target = Vector3.zero;

    [Header("Rotation (drag)")]
    public int dragMouseButton = 1;             // 0=左, 1=右, 2=中
    public float yawSpeed   = 0.2f;
    public float pitchSpeed = 0.2f;
    [Range(-89f, 0f)] public float minPitch = -85f;
    [Range(0f, 89f)]  public float maxPitch =  85f;

    [Header("Zoom")]
    public float distance = 5f;
    public float minDistance = 1.0f;
    public float maxDistance = 20.0f;
    public float wheelZoomSpeed = 2.0f;
    public float pinchZoomSpeed = 0.01f;

    [Header("Smoothing")]
    public float moveDamping = 0.12f;
    public float rotDamping  = 0.10f;

    float _yawDeg, _pitchDeg, _distTarget, _yawTarget, _pitchTarget;

    void Start()
    {
        Vector3 dir = (transform.position - target);
        float d = dir.magnitude;
        if (d < 1e-3f) d = distance;
        distance = _distTarget = Mathf.Clamp(d, minDistance, maxDistance);

        _yawDeg   = _yawTarget   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        _pitchDeg = _pitchTarget = Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
        _pitchDeg = _pitchTarget = Mathf.Clamp(_pitchDeg, minPitch, maxPitch);
        ApplyImmediate();
    }

    void Update()
    {
        bool pointerOverUI = EventSystem.current && EventSystem.current.IsPointerOverGameObject();

        if (!pointerOverUI && Input.GetMouseButton(dragMouseButton))
        {
            float dx = Input.GetAxis("Mouse X");
            float dy = Input.GetAxis("Mouse Y");
            _yawTarget   += dx * yawSpeed * 100f * Time.deltaTime;
            _pitchTarget -= dy * pitchSpeed * 100f * Time.deltaTime;
            _pitchTarget  = Mathf.Clamp(_pitchTarget, minPitch, maxPitch);
        }

        if (!pointerOverUI)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 1e-4f)
                _distTarget = Mathf.Clamp(_distTarget - scroll * wheelZoomSpeed, minDistance, maxDistance);
        }

        if (Input.touchCount > 0)
        {
            bool touchOverUI = false;
            for (int i = 0; i < Input.touchCount; i++)
                if (EventSystem.current && EventSystem.current.IsPointerOverGameObject(Input.touches[i].fingerId))
                    touchOverUI = true;

            if (!touchOverUI)
            {
                if (Input.touchCount == 1)
                {
                    var t = Input.GetTouch(0);
                    if (t.phase == TouchPhase.Moved)
                    {
                        _yawTarget   += t.deltaPosition.x * yawSpeed * 0.02f;
                        _pitchTarget -= t.deltaPosition.y * pitchSpeed * 0.02f;
                        _pitchTarget  = Mathf.Clamp(_pitchTarget, minPitch, maxPitch);
                    }
                }
                else if (Input.touchCount >= 2)
                {
                    var t0 = Input.GetTouch(0);
                    var t1 = Input.GetTouch(1);
                    if (t0.phase == TouchPhase.Moved || t1.phase == TouchPhase.Moved)
                    {
                        float prev = (t0.position - t0.deltaPosition - (t1.position - t1.deltaPosition)).magnitude;
                        float curr = (t0.position - t1.position).magnitude;
                        float delta = curr - prev;
                        _distTarget = Mathf.Clamp(_distTarget - delta * pinchZoomSpeed, minDistance, maxDistance);
                    }
                }
            }
        }

        float rotA = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, rotDamping));
        float posA = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, moveDamping));

        _yawDeg   = Mathf.LerpAngle(_yawDeg,   _yawTarget,   rotA);
        _pitchDeg = Mathf.Lerp(_pitchDeg, _pitchTarget, rotA);
        distance  = Mathf.Lerp(distance,  _distTarget,  posA);

        Quaternion rot = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
        Vector3 pos = target + rot * new Vector3(0, 0, -distance);
        transform.SetPositionAndRotation(pos, rot);
    }

    void ApplyImmediate()
    {
        Quaternion rot = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
        Vector3 pos = target + rot * new Vector3(0, 0, -distance);
        transform.SetPositionAndRotation(pos, rot);
    }
}
