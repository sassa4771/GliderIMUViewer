using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
[RequireComponent(typeof(CanvasRenderer))]
public class PoseGraphView : Graphic
{
    [Header("Refs")]
    public PoseCsvStore store;
    public PosePlaybackController controller;

    [Header("Series")]
    public bool showRoll = true, showYaw = true, showPitch = true;
    [Range(0.5f, 10f)] public float thickness = 2f;
    public Color rollColor  = new Color(0.90f, 0.25f, 0.25f, 1f);
    public Color yawColor = new Color(0.25f, 0.85f, 0.35f, 1f);
    public Color pitchColor   = new Color(0.25f, 0.45f, 0.95f, 1f);

    [Header("Axes")]
    public bool autoYRange = true;
    public Vector2 fixedYRange = new Vector2(-90f, 90f);
    public int maxPointsPerLine = 2000;

    [Header("Cursor")]
    public bool drawCursor = true;
    public float cursorWidth = 2f;
    public Color cursorColor = new Color(1,1,1,0.9f);

    float[] _times;            // s
    Vector3[] _euls;           // deg (unwrap済)
    Vector2 _xRange;           // (0, Duration)
    Vector2 _yRange;           // (ymin, ymax)
    float _lastCursorX = -1f;

    protected override void OnEnable()
    {
        base.OnEnable();
        if (store) store.Loaded += OnStoreLoaded;
        if (store && store.IsLoaded) OnStoreLoaded();
    }
    protected override void OnDisable()
    {
        base.OnDisable();
        if (store) store.Loaded -= OnStoreLoaded;
    }

    void OnStoreLoaded()
    {
        store.GetSnapshot(out _times, out _euls);
        _xRange = new Vector2(0f, Mathf.Max(0.0001f, store.Duration));
        if (autoYRange) GetYMinMax(out _yRange.x, out _yRange.y);
        else _yRange = fixedYRange;
        SetVerticesDirty();
    }

    void Update()
    {
        if (!drawCursor || controller == null || _times == null) return;
        float u = Mathf.InverseLerp(_xRange.x, _xRange.y, Mathf.Clamp(controller.TimeSec, _xRange.x, _xRange.y));
        float cursorX = u * rectTransform.rect.width;
        if (Mathf.Abs(cursorX - _lastCursorX) > 0.5f)
        {
            _lastCursorX = cursorX;
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var rect = rectTransform.rect;
        if (_times == null || _times.Length < 2 || rect.width < 1f || rect.height < 1f) return;

        var series = BuildSeries(rect);
        int idx = 0;
        if (showRoll)  DrawPolyline(vh, series[idx++], rollColor,  thickness);
        if (showYaw) DrawPolyline(vh, series[idx++], yawColor, thickness);
        if (showPitch)   DrawPolyline(vh, series[idx++], pitchColor,   thickness);

        if (drawCursor && controller != null)
        {
            float u = Mathf.InverseLerp(_xRange.x, _xRange.y, Mathf.Clamp(controller.TimeSec, _xRange.x, _xRange.y));
            float x = u * rect.width;
            DrawVLine(vh, x, rect.height, cursorWidth, cursorColor);
        }
    }

    List<List<Vector2>> BuildSeries(Rect rect)
    {
        var res = new List<List<Vector2>>(3);
        int nTotal = _times.Length;
        int stride = Mathf.Max(1, Mathf.CeilToInt((float)nTotal / Mathf.Max(1, maxPointsPerLine)));
        List<Vector2> toList(int axis)
        {
            var lst = new List<Vector2>((nTotal + stride - 1) / stride);
            for (int i = 0; i < nTotal; i += stride)
            {
                float ux = Mathf.InverseLerp(_xRange.x, _xRange.y, _times[i]);
                float uy = Mathf.InverseLerp(_yRange.x, _yRange.y, axis==0? _euls[i].x : axis==1? _euls[i].y : _euls[i].z);
                lst.Add(new Vector2(ux * rect.width, uy * rect.height));
            }
            return lst;
        }
        if (showRoll)  res.Add(toList(0));
        if (showYaw) res.Add(toList(1));
        if (showPitch)   res.Add(toList(2));
        return res;
    }

    void GetYMinMax(out float mn, out float mx)
    {
        mn = +1e9f; mx = -1e9f;
        for (int i = 0; i < _euls.Length; i++)
        {
            mn = Mathf.Min(mn, _euls[i].x, _euls[i].y, _euls[i].z);
            mx = Mathf.Max(mx, _euls[i].x, _euls[i].y, _euls[i].z);
        }
        float pad = (mx - mn) * 0.1f + 1f;
        mn -= pad; mx += pad;
        if (mx - mn < 1e-6f) { mn -= 1f; mx += 1f; }
    }

    static void DrawPolyline(VertexHelper vh, List<Vector2> pts, Color col, float thick)
    {
        if (pts == null || pts.Count < 2) return;
        float half = thick * 0.5f;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 p0 = pts[i], p1 = pts[i+1];
            if ((p1 - p0).sqrMagnitude < 1e-8f) continue;
            Vector2 dir = (p1 - p0).normalized;
            Vector2 n = new Vector2(-dir.y, dir.x) * half;
            Vector3 v0 = p0 - n, v1 = p0 + n, v2 = p1 + n, v3 = p1 - n;
            int baseIndex = vh.currentVertCount;
            AddVert(vh, v0, col); AddVert(vh, v1, col); AddVert(vh, v2, col); AddVert(vh, v3, col);
            vh.AddTriangle(baseIndex, baseIndex+1, baseIndex+2);
            vh.AddTriangle(baseIndex, baseIndex+2, baseIndex+3);
        }
    }
    static void DrawVLine(VertexHelper vh, float x, float h, float w, Color col)
    {
        float half = w * 0.5f;
        Vector3 v0 = new Vector3(x - half, 0f, 0f);
        Vector3 v1 = new Vector3(x + half, 0f, 0f);
        Vector3 v2 = new Vector3(x + half, h, 0f);
        Vector3 v3 = new Vector3(x - half, h, 0f);
        int baseIndex = vh.currentVertCount;
        AddVert(vh, v0, col); AddVert(vh, v1, col); AddVert(vh, v2, col); AddVert(vh, v3, col);
        vh.AddTriangle(baseIndex, baseIndex+1, baseIndex+2);
        vh.AddTriangle(baseIndex, baseIndex+2, baseIndex+3);
    }
    static void AddVert(VertexHelper vh, Vector3 pos, Color col)
    {
        var v = UIVertex.simpleVert; v.position = pos; v.color = col; vh.AddVert(v);
    }
}
