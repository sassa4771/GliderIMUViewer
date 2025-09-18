using UnityEngine;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
public class OrbitAroundTarget : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                    // 追従先（中心）
    public Vector3 targetOffset = Vector3.zero; // 例: モデル中心が足元の時に (0,身長*0.5,0)
    public bool followTarget = true;            // ターゲットが動く場合も中心を追従

    [Tooltip("ドラッグ開始はターゲット上からのみ許可（Collider必須）")]
    public bool requirePointerOnTargetToStart = false;
    public LayerMask pointerHitMask = ~0;       // クリック検出するレイヤー（通常はDefaultでOK）

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

    [Header("Framing (optional)")]
    [Tooltip("初期化時にRenderer Boundsを使って距離を自動調整")]
    public bool fitOnStart = true;
    [Tooltip("フレーミング時の余白係数 (>1でゆとり)")]
    public float fitMargin = 1.2f;

    float _yawDeg, _pitchDeg, _distTarget, _yawTarget, _pitchTarget;
    bool _dragging = false;
    Camera _cam;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        if (_cam == null) _cam = Camera.main;

        if (target == null)
        {
            Debug.LogWarning("[OrbitAroundTarget] Target が未設定です。");
        }
    }

    void Start()
    {
        Vector3 pivot = GetPivot();
        Vector3 dir = transform.position - pivot;
        float d = dir.magnitude;
        if (d < 1e-3f) d = distance;

        if (fitOnStart) TryFitToTarget(out d);

        distance = _distTarget = Mathf.Clamp(d, minDistance, maxDistance);

        _yawDeg   = _yawTarget   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        _pitchDeg = _pitchTarget = Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
        _pitchDeg = _pitchTarget = Mathf.Clamp(_pitchDeg, minPitch, maxPitch);
        ApplyImmediate();
    }

    void Update()
    {
        bool pointerOverUI = EventSystem.current && EventSystem.current.IsPointerOverGameObject();

        // --- マウスドラッグ開始/終了管理（必要ならターゲット上クリックでのみ開始） ---
        if (!pointerOverUI)
        {
            if (Input.GetMouseButtonDown(dragMouseButton))
            {
                _dragging = !requirePointerOnTargetToStart || PointerOverTarget(Input.mousePosition);
            }
            if (Input.GetMouseButtonUp(dragMouseButton))
                _dragging = false;
        }
        else
        {
            // UI上で押したらドラッグ開始しない
            if (Input.GetMouseButtonDown(dragMouseButton)) _dragging = false;
        }

        // --- 回転（マウス） ---
        if (_dragging && Input.GetMouseButton(dragMouseButton))
        {
            float dx = Input.GetAxis("Mouse X");
            float dy = Input.GetAxis("Mouse Y");
            _yawTarget   += dx * yawSpeed * 100f * Time.deltaTime;
            _pitchTarget -= dy * pitchSpeed * 100f * Time.deltaTime;
            _pitchTarget  = Mathf.Clamp(_pitchTarget, minPitch, maxPitch);
        }

        // --- ズーム（ホイール） ---
        if (!pointerOverUI)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 1e-4f)
                _distTarget = Mathf.Clamp(_distTarget - scroll * wheelZoomSpeed, minDistance, maxDistance);
        }

        // --- タッチ操作 ---
        HandleTouch(pointerOverUI);

        // --- スムージング ---
        float rotA = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, rotDamping));
        float posA = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, moveDamping));

        _yawDeg   = Mathf.LerpAngle(_yawDeg,   _yawTarget,   rotA);
        _pitchDeg = Mathf.Lerp(_pitchDeg, _pitchTarget, rotA);
        distance  = Mathf.Lerp(distance,  _distTarget,  posA);

        // --- ターゲット追従 & 反映 ---
        Vector3 pivotNow = GetPivot(); // 動くターゲットでもOK
        Quaternion rot = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
        Vector3 pos = pivotNow + rot * new Vector3(0, 0, -distance);
        transform.SetPositionAndRotation(pos, rot);
    }

    void HandleTouch(bool pointerOverUI)
    {
        if (Input.touchCount <= 0) return;

        // どれかの指がUI上なら全体をブロック
        for (int i = 0; i < Input.touchCount; i++)
            if (EventSystem.current && EventSystem.current.IsPointerOverGameObject(Input.touches[i].fingerId))
                return;

        if (Input.touchCount == 1)
        {
            var t = Input.GetTouch(0);

            if (t.phase == TouchPhase.Began)
            {
                _dragging = !requirePointerOnTargetToStart || PointerOverTarget(t.position);
            }
            else if (t.phase == TouchPhase.Moved && _dragging)
            {
                _yawTarget   += t.deltaPosition.x * yawSpeed * 0.02f;
                _pitchTarget -= t.deltaPosition.y * pitchSpeed * 0.02f;
                _pitchTarget  = Mathf.Clamp(_pitchTarget, minPitch, maxPitch);
            }
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
            {
                _dragging = false;
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

    Vector3 GetPivot()
    {
        if (target != null)
        {
            if (followTarget) return target.position + target.TransformVector(targetOffset);
            // followTarget=false の時は開始位置基準の固定中心にしたければ別管理だが、
            // ここでは target の現在位置＋オフセットを採用
            return target.position + target.TransformVector(targetOffset);
        }
        return targetOffset; // target未設定時は原点相当（オフセットのみ）
    }

    bool PointerOverTarget(Vector2 screenPos)
    {
        if (_cam == null || target == null) return false;

        Ray ray = _cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out var hit, 1000f, pointerHitMask))
        {
            // ターゲット自身または子孫に当たったらOK
            return hit.transform == target || hit.transform.IsChildOf(target);
        }
        return false;
    }

    void ApplyImmediate()
    {
        Quaternion rot = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
        Vector3 pos = GetPivot() + rot * new Vector3(0, 0, -distance);
        transform.SetPositionAndRotation(pos, rot);
    }

    public void SetTarget(Transform t, bool doFit = true)
    {
        target = t;
        if (doFit) TryFitToTarget(out _distTarget);
    }

    public void ReframeToTarget()
    {
        if (TryFitToTarget(out float d))
        {
            _distTarget = Mathf.Clamp(d, minDistance, maxDistance);
        }
    }

    bool TryFitToTarget(out float fittedDistance)
    {
        fittedDistance = distance;
        if (target == null) return false;

        // 子孫を含むRenderer Boundsを取得
        var rends = target.GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0) return false;

        Bounds b = new Bounds(rends[0].bounds.center, Vector3.zero);
        foreach (var r in rends) b.Encapsulate(r.bounds);
        float radius = b.extents.magnitude;

        // 投影サイズから距離を見積もり（簡易）：垂直画角ベース
        Camera cam = _cam != null ? _cam : Camera.main;
        if (cam == null) return false;

        float vfovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float halfH = Mathf.Tan(vfovRad * 0.5f);
        // 画面に納めるための距離（だいたい）：radius / (sin(theta)) を単純化
        float d = (radius * fitMargin) / Mathf.Max(1e-3f, halfH);
        fittedDistance = Mathf.Clamp(d, minDistance, maxDistance);
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Vector3 p = GetPivot();
        Gizmos.DrawWireSphere(p, 0.05f);
        Gizmos.DrawLine(p, p + Vector3.up * 0.3f);
    }
#endif
}
