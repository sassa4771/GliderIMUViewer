using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// RealtimePoseStore の履歴を、右端が「いま」のスクロールグラフとして描く（PoseGraphView のリアルタイム版）。
/// データが途絶えると、その分だけ右側に空白ができていく。
/// </summary>
[RequireComponent(typeof(RectTransform))]
[RequireComponent(typeof(CanvasRenderer))]
public class RealtimeGraphView : Graphic
{
    [Header("Refs")]
    public RealtimePoseStore store;

    [Header("Series")]
    public bool showRoll = true, showYaw = true, showPitch = true;
    [Range(0.5f, 10f)] public float thickness = 2f;
    public Color rollColor  = new Color(0.90f, 0.25f, 0.25f, 1f);
    public Color yawColor   = new Color(0.25f, 0.85f, 0.35f, 1f);
    public Color pitchColor = new Color(0.25f, 0.45f, 0.95f, 1f);

    [Header("Axes")]
    [Tooltip("表示する時間幅[s]（右端が現在）")]
    [Min(1f)] public float windowSeconds = 30f;
    public bool autoYRange = true;
    public Vector2 fixedYRange = new Vector2(-90f, 90f);
    [Tooltip("自動レンジの最小幅[deg]")]
    [Min(1f)] public float minAutoSpan = 20f;
    public int maxPointsPerLine = 1500;

    [Header("Grid")]
    public bool drawZeroLine = true;
    public Color zeroLineColor = new Color(0f, 0f, 0f, 0.45f);
    [Tooltip("縦の目盛り線の間隔[s]（0 で非表示）")]
    [Min(0f)] public float timeGridSeconds = 5f;
    public Color gridColor = new Color(0f, 0f, 0f, 0.12f);

    [Header("Labels (optional)")]
    public TMP_Text yMaxLabel;
    public TMP_Text yMinLabel;

    const int MaxVerts = 64000;             // VertexHelper の上限(65000)より少し手前で止める
    const float RangeShrinkTime = 0.6f;     // 自動レンジを狭めるときの時定数[s]
    const float WrapJump = 180f;            // 隣り合う点がこれ以上離れていたら ±180 の折り返しとみなし、線をつながない

    float _yMin = -10f, _yMax = 10f;
    bool _rangeValid, _rangeAnimating;
    long _lastSerial = -1;
    double _rightEdge;                      // グラフ右端の時刻（受信時計の秒）
    int _settingsHash;
    int _lastMaxLabel = int.MinValue, _lastMinLabel = int.MinValue;

    protected override void OnEnable()
    {
        base.OnEnable();
        _lastSerial = -1;
        _rangeValid = false;
    }

    void Update()
    {
        if (store == null) return;
        if (canvasRenderer.GetInheritedAlpha() <= 0f) return;      // UI を隠している間は描き直さない

        bool dirty = false;
        if (store.HistorySerial != _lastSerial)
        {
            _lastSerial = store.HistorySerial;
            dirty = true;
        }

        // 右端は「いま」。1ピクセル分進むごとに描き直す（細かく動かすと線がちらつくので、ピクセル単位に丸める）
        double right = SnappedNow();
        if (right != _rightEdge) { _rightEdge = right; dirty = true; }

        int hash = SettingsHash();
        if (hash != _settingsHash) { _settingsHash = hash; dirty = true; }

        if (dirty || _rangeAnimating) dirty |= UpdateRange();
        if (dirty)
        {
            SetVerticesDirty();
            UpdateLabels();
        }
    }

    double SnappedNow()
    {
        double now = store.DisplayNow;
        float scale = canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
        double pixels = Math.Max(1.0, rectTransform.rect.width * scale);
        double step = windowSeconds / pixels;                       // 1ピクセルあたりの秒数
        return Math.Ceiling(now / step) * step;
    }

    int SettingsHash()
    {
        unchecked
        {
            int h = (showRoll ? 1 : 0) | (showPitch ? 2 : 0) | (showYaw ? 4 : 0) | (autoYRange ? 8 : 0) | (drawZeroLine ? 16 : 0);
            h = h * 31 + windowSeconds.GetHashCode();
            h = h * 31 + thickness.GetHashCode();
            h = h * 31 + fixedYRange.GetHashCode();
            h = h * 31 + timeGridSeconds.GetHashCode();
            return h;
        }
    }

    // 表示中のデータに合わせて縦軸レンジを決める。広げるのは即時、狭めるのはゆっくり。
    bool UpdateRange()
    {
        float lo, hi;
        if (!autoYRange)
        {
            lo = fixedYRange.x; hi = fixedYRange.y;
            if (hi - lo < 1e-3f) hi = lo + 1f;
            _rangeAnimating = false;
        }
        else
        {
            if (!VisibleMinMax(out float mn, out float mx)) { mn = 0f; mx = 0f; }
            float pad = (mx - mn) * 0.1f + 1f;
            lo = mn - pad; hi = mx + pad;
            if (hi - lo < minAutoSpan)
            {
                float c = (hi + lo) * 0.5f;
                lo = c - minAutoSpan * 0.5f; hi = c + minAutoSpan * 0.5f;
            }
            if (_rangeValid)
            {
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / RangeShrinkTime);
                float targetLo = lo, targetHi = hi;
                if (lo > _yMin) lo = Mathf.Lerp(_yMin, lo, k);
                if (hi < _yMax) hi = Mathf.Lerp(_yMax, hi, k);
                _rangeAnimating = Mathf.Abs(lo - targetLo) > 0.05f || Mathf.Abs(hi - targetHi) > 0.05f;
            }
        }

        bool changed = !_rangeValid || !Mathf.Approximately(lo, _yMin) || !Mathf.Approximately(hi, _yMax);
        _yMin = lo; _yMax = hi;
        _rangeValid = true;
        return changed;
    }

    bool VisibleMinMax(out float mn, out float mx)
    {
        mn = float.MaxValue; mx = float.MinValue;
        int n = store.HistoryCount;
        if (n == 0) return false;
        for (int i = FirstVisibleIndex(n); i < n; i++)
        {
            store.GetHistory(i, out _, out Vector3 rpy, out _);
            if (showRoll)  { mn = Mathf.Min(mn, rpy.x); mx = Mathf.Max(mx, rpy.x); }
            if (showPitch) { mn = Mathf.Min(mn, rpy.y); mx = Mathf.Max(mx, rpy.y); }
            if (showYaw)   { mn = Mathf.Min(mn, rpy.z); mx = Mathf.Max(mx, rpy.z); }
        }
        return mn <= mx;
    }

    // 時間窓に入る最初のサンプル（履歴は時刻の昇順）
    int FirstVisibleIndex(int n)
    {
        double tStart = _rightEdge - windowSeconds;
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (store.GetHistoryTime(mid) < tStart) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    void UpdateLabels()
    {
        int mx = Mathf.RoundToInt(_yMax), mn = Mathf.RoundToInt(_yMin);
        if (yMaxLabel && mx != _lastMaxLabel) { _lastMaxLabel = mx; yMaxLabel.SetText("{0}", mx); }
        if (yMinLabel && mn != _lastMinLabel) { _lastMinLabel = mn; yMinLabel.SetText("{0}", mn); }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = rectTransform.rect;
        if (rect.width < 1f || rect.height < 1f) return;

        // 目盛り線（「何秒前」の位置に固定）とゼロ線
        if (timeGridSeconds > 0f)
        {
            int lines = Mathf.Min(120, Mathf.CeilToInt(windowSeconds / timeGridSeconds) - 1);
            for (int i = 1; i <= lines; i++)
            {
                float x = rect.xMax - rect.width * (i * timeGridSeconds / windowSeconds);
                AddQuad(vh, new Vector2(x - 0.5f, rect.yMin), new Vector2(x + 0.5f, rect.yMax), gridColor);
            }
        }
        if (drawZeroLine && _yMin < 0f && _yMax > 0f)
        {
            float y = rect.yMin + rect.height * Mathf.InverseLerp(_yMin, _yMax, 0f);
            AddQuad(vh, new Vector2(rect.xMin, y - 0.5f), new Vector2(rect.xMax, y + 0.5f), zeroLineColor);
        }

        if (store == null) return;
        int n = store.HistoryCount;
        if (n < 2) return;

        int series = (showRoll ? 1 : 0) + (showYaw ? 1 : 0) + (showPitch ? 1 : 0);
        if (series == 0) return;

        int first = FirstVisibleIndex(n);
        if (first > 0) first--;                 // 左端をまたぐ線分のために1つ前から
        int budget = (MaxVerts - vh.currentVertCount) / (4 * series);
        int maxPts = Mathf.Clamp(maxPointsPerLine, 16, Mathf.Max(16, budget));
        int stride = Mathf.Max(1, Mathf.CeilToInt((n - first) / (float)maxPts));

        if (showRoll)  DrawSeries(vh, rect, 0, rollColor,  first, n, stride);
        if (showYaw)   DrawSeries(vh, rect, 2, yawColor,   first, n, stride);
        if (showPitch) DrawSeries(vh, rect, 1, pitchColor, first, n, stride);
    }

    void DrawSeries(VertexHelper vh, Rect rect, int comp, Color col, int first, int n, int stride)
    {
        double tStart = _rightEdge - windowSeconds;
        long serial0 = store.HistoryFirstSerial;
        float half = thickness * 0.5f;
        Color32 c32 = col;

        bool havePrev = false, pendingBreak = false;
        Vector2 prev = default;
        float prevValue = 0f;
        for (int i = first; i < n; i++)
        {
            store.GetHistory(i, out double t, out Vector3 rpy, out bool brk);
            if (brk) pendingBreak = true;

            // 間引き: 通し番号で選ぶので、スクロールしても選ばれる点が変わらない（ちらつかない）
            bool keep = i == first || i == n - 1 || brk || (serial0 + i) % stride == 0;
            if (!keep) continue;

            float value = rpy[comp];
            float u = Mathf.Min(1f, (float)((t - tStart) / windowSeconds));
            float v = Mathf.InverseLerp(_yMin, _yMax, value);
            var pt = new Vector2(rect.xMin + u * rect.width, rect.yMin + v * rect.height);

            // ±180 の折り返しをまたぐ線は引かない
            if (havePrev && Mathf.Abs(value - prevValue) > WrapJump) pendingBreak = true;

            if (havePrev && !pendingBreak && pt.x >= rect.xMin)
            {
                Vector2 a = prev;
                if (a.x < rect.xMin && pt.x > a.x)
                    a = Vector2.Lerp(a, pt, (rect.xMin - a.x) / (pt.x - a.x));   // 左端で切る
                if (vh.currentVertCount + 4 > MaxVerts) return;
                AddSegment(vh, a, pt, half, c32);
            }
            prev = pt;
            prevValue = value;
            havePrev = true;
            pendingBreak = false;
        }
    }

    static void AddSegment(VertexHelper vh, Vector2 p0, Vector2 p1, float half, Color32 col)
    {
        Vector2 d = p1 - p0;
        if (d.sqrMagnitude < 1e-8f) return;
        d.Normalize();
        Vector2 nrm = new Vector2(-d.y, d.x) * half;
        int baseIndex = vh.currentVertCount;
        AddVert(vh, p0 - nrm, col); AddVert(vh, p0 + nrm, col); AddVert(vh, p1 + nrm, col); AddVert(vh, p1 - nrm, col);
        vh.AddTriangle(baseIndex, baseIndex + 1, baseIndex + 2);
        vh.AddTriangle(baseIndex, baseIndex + 2, baseIndex + 3);
    }

    static void AddQuad(VertexHelper vh, Vector2 min, Vector2 max, Color32 col)
    {
        int baseIndex = vh.currentVertCount;
        AddVert(vh, new Vector2(min.x, min.y), col); AddVert(vh, new Vector2(min.x, max.y), col);
        AddVert(vh, new Vector2(max.x, max.y), col); AddVert(vh, new Vector2(max.x, min.y), col);
        vh.AddTriangle(baseIndex, baseIndex + 1, baseIndex + 2);
        vh.AddTriangle(baseIndex, baseIndex + 2, baseIndex + 3);
    }

    static void AddVert(VertexHelper vh, Vector2 pos, Color32 col)
    {
        vh.AddVert(new Vector3(pos.x, pos.y, 0f), col, new Vector4(0.5f, 0.5f, 0f, 0f));
    }
}
