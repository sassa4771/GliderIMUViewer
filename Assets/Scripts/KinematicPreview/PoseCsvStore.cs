// Assets/Scripts/KinematicPreview/PoseCsvStore.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_STANDALONE
using SFB; // UnityStandaloneFileBrowser
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

public class PoseCsvStore : MonoBehaviour
{
    [Header("CSV")]
    public bool useStreamingAssetsPath = true;
    public string filePath = "out_20250912-152517.csv";
    public char delimiter = ',';
    public string timeColumn  = "t_ms";   // ミリ秒
    public string dtColumn    = "dt_ms";  // t_ms が無い場合の増分
    public string rollColumn  = "roll";
    public string pitchColumn = "pitch";
    public string yawColumn   = "yaw";

    [Header("Angle units & axis map")]
    public bool autoDetectAngleUnits = true; // |max|<=6.5 をラジアン推定
    public bool anglesAreRadians = false;
    public enum Axis { X=0, Y=1, Z=2 }
    public Axis rollAxis = Axis.X;
    public Axis pitchAxis = Axis.Z;
    public Axis yawAxis = Axis.Y;
    public int rollSign = +1, pitchSign = +1, yawSign = +1;

    [Tooltip("Yaw の符号を強制反転します（入力側で逆だった場合の補正用）。")]
    public bool flipYawSign = false;

    [Header("Normalization")]
    public bool zeroAtStart = true;

    [Header("Startup")]
    public bool loadOnStart = true;

    // === 追加: IMU/加速度 ===
    [Header("Acceleration (optional)")]
    public string axColumn = "ax";
    public string ayColumn = "ay";
    public string azColumn = "az";
    public enum AccelUnit { Mps2, G }
    [Tooltip("G=『静止で1g』の比力、Mps2= m/s^2")]
    public AccelUnit accelUnit = AccelUnit.G;
    [Tooltip("true: 既に重力除去済み（linear accel）。false: 生の比力（静止で1g）")]
    public bool accelIsLinear = false;
    [Tooltip("CSV→機体座標の軸マップ/符号（必要に応じて調整）")]
    public Axis axGoesTo = Axis.X, ayGoesTo = Axis.Y, azGoesTo = Axis.Z;
    public int axSign = +1, aySign = +1, azSign = +1;

    // === 追加: 任意カラム用の符号/スケール設定 ===
    [Serializable]
    public class ColumnScale
    {
        public string columnName;     // 例: "servo1", "ax", "pitch"
        [Tooltip("1=そのまま, -1=反転, 2=2倍...")]
        public float multiplier = 1f; // -1 で反転
    }

    [Header("Per-Column Sign/Scale (optional)")]
    [Tooltip("ここに列名と倍率を列挙すると、*Signed API*で取得時に掛け算します（-1で反転）")]
    public List<ColumnScale> columnScales = new List<ColumnScale>();

    // 公開API
    public bool IsLoaded => _frames.Count > 0;
    public float Duration => _frames.Count > 0 ? _frames[_frames.Count - 1].t : 0f;
    public string CurrentPath { get; private set; }
    public string LastError  { get; private set; }

    public event Action Loaded;
    public event Action<string> LoadFailed;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void FilePicker_OpenFileDialog(string gameObjectName, string accept);
#endif

    public void OpenCsvDialog()
    {
#if UNITY_EDITOR
        string initialDir = GetLastDirOrDefault();
        string selectedPath = UnityEditor.EditorUtility.OpenFilePanel("CSVを選択", initialDir, "csv");
        if (!string.IsNullOrEmpty(selectedPath)) LoadFromAbsolutePath(selectedPath);
        else Debug.Log("[PoseCsvStore] ダイアログキャンセル");

#elif UNITY_STANDALONE
        try
        {
            string initialDir = GetLastDirOrDefault();
            var exts  = new[] { new ExtensionFilter("CSV", "csv"), new ExtensionFilter("All Files", "*") };
            var paths = StandaloneFileBrowser.OpenFilePanel("CSVを選択", initialDir, exts, false);
            string selectedPath = (paths != null && paths.Length > 0) ? paths[0] : null;
            if (!string.IsNullOrEmpty(selectedPath)) LoadFromAbsolutePath(selectedPath);
            else Debug.Log("[PoseCsvStore] ダイアログキャンセル(Standalone)");
        }
        catch (Exception ex)
        {
            LastError = $"SFB呼び出し例外: {ex.Message}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
        }

#elif UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            FilePicker_OpenFileDialog(gameObject.name, ".csv,text/csv");
            Debug.Log("[PoseCsvStore] WebGL FilePicker 呼び出し");
        }
        catch (Exception ex)
        {
            LastError = $"WebGLダイアログ失敗: {ex.Message}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
        }
#else
        LastError = "このプラットフォームではOSダイアログ未対応です。";
        LoadFailed?.Invoke(LastError);
        Debug.LogWarning($"[PoseCsvStore] {LastError}");
#endif
    }

    [Serializable] private class WebGLPickPayload { public string name; public string data; }

    public void OnWebGLFilePicked(string payloadJson)
    {
        try
        {
            var payload = JsonUtility.FromJson<WebGLPickPayload>(payloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.data))
                throw new Exception("payloadが空です");

            byte[] bytes = Convert.FromBase64String(payload.data);
            string text  = Encoding.UTF8.GetString(bytes);
            string virtName = string.IsNullOrEmpty(payload.name) ? "(webgl)" : payload.name;

            LoadFromText(text, virtName);
        }
        catch (Exception ex)
        {
            LastError = $"WebGLデコード失敗: {ex.Message}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
        }
    }

    public void OnWebGLFilePickError(string message)
    {
        LastError = $"WebGLダイアログ失敗/キャンセル: {message}";
        LoadFailed?.Invoke(LastError);
        Debug.LogWarning($"[PoseCsvStore] {LastError}");
    }

    //=== グラフ向けスナップショット ===
    public void GetSnapshot(out float[] times, out Vector3[] eulersDeg)
    {
        int n = _frames.Count;
        times = new float[n];
        eulersDeg = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = _frames[i].t;
            Quaternion q = _zero * _frames[i].q;
            var e = q.eulerAngles;
            e.x = (e.x > 180f) ? e.x - 360f : e.x;
            e.y = (e.y > 180f) ? e.y - 360f : e.y;
            e.z = (e.z > 180f) ? e.z - 360f : e.z;
            eulersDeg[i] = e;
        }
        Unwrap(ref eulersDeg);
    }

    // 指定時刻の姿勢（補間、zeroAtStart適用）
    public Quaternion EvaluateQuat(float t)
    {
        if (_frames.Count == 0) return Quaternion.identity;
        t = Mathf.Clamp(t, 0f, Duration);
        int lo = 0, hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_frames[mid].t < t) lo = mid + 1;
            else hi = mid - 1;
        }
        int i0 = Mathf.Clamp(lo - 1, 0, _frames.Count - 2);
        int i1 = i0 + 1;
        var f0 = _frames[i0]; var f1 = _frames[i1];
        float u = (f1.t > f0.t) ? Mathf.InverseLerp(f0.t, f1.t, t) : 0f;
        return _zero * Quaternion.Slerp(f0.q, f1.q, u);
    }

    // 生の姿勢（zeroAtStartを掛けない）
    public Quaternion GetRawQuatAt(float t)
    {
        if (_frames.Count == 0) return Quaternion.identity;
        t = Mathf.Clamp(t, 0f, Duration);
        int lo = 0, hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_frames[mid].t < t) lo = mid + 1;
            else hi = mid - 1;
        }
        int i0 = Mathf.Clamp(lo - 1, 0, _frames.Count - 2);
        int i1 = i0 + 1;
        var f0 = _frames[i0]; var f1 = _frames[i1];
        float u = (f1.t > f0.t) ? Mathf.InverseLerp(f0.t, f1.t, t) : 0f;
        return Quaternion.Slerp(f0.q, f1.q, u);
    }

    //=== 読み込み ===
    public void LoadFromAbsolutePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            LastError = "空のパスです";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
            return;
        }
        try
        {
            if (!File.Exists(absolutePath))
            {
                LastError = $"ファイルが存在しません: {absolutePath}";
                LoadFailed?.Invoke(LastError);
                Debug.LogError($"[PoseCsvStore] {LastError}");
                return;
            }
            string text = File.ReadAllText(absolutePath);
            CurrentPath = absolutePath;
            Debug.Log($"[PoseCsvStore] OPEN OK : path={CurrentPath}, length={text?.Length}");
            ParseCsv(text);
            try { PlayerPrefs.SetString("PoseCsvStore.lastDir", Path.GetDirectoryName(absolutePath)); } catch {}
        }
        catch (Exception ex)
        {
            LastError = $"読み込み例外: {ex.Message}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}\n{ex}");
        }
    }

    public void LoadFromText(string csvText, string virtualPathHint = null)
    {
        CurrentPath = virtualPathHint;
        Debug.Log($"[PoseCsvStore] OPEN OK : virtualPath={CurrentPath}, length={csvText?.Length}");
        ParseCsv(csvText);
    }

    public static string GetLastDirOrDefault()
    {
        var d = PlayerPrefs.GetString("PoseCsvStore.lastDir", "");
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) d = Application.persistentDataPath;
        return d;
    }

    //=== 内部 ===
    struct Frame { public float t; public Quaternion q; }
    readonly List<Frame> _frames = new List<Frame>(4096);
    Quaternion _zero = Quaternion.identity;

    // 加速度（機体座標, m/s^2）
    readonly List<Vector3> _accelsBody = new List<Vector3>(4096);

    // 全カラム原本
    List<string> _header = new List<string>();
    readonly Dictionary<string, List<float>> _numCols =
        new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> _strCols =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    // 符号/スケール適用後のキャッシュ（必要なものだけ生成）
    readonly Dictionary<string, (float m, List<float> data)> _scaledCache =
        new Dictionary<string, (float m, List<float>)>(StringComparer.OrdinalIgnoreCase);

    void Start()
    {
        if (!loadOnStart) return;
        if (useStreamingAssetsPath) StartCoroutine(LoadFromStreamingAssets());
        else LoadFromAbsolute();
    }

    System.Collections.IEnumerator LoadFromStreamingAssets()
    {
        string combined = System.IO.Path.Combine(Application.streamingAssetsPath, filePath);
        string url = (combined.Contains("://") || combined.Contains(":///"))
            ? combined
            : new System.Uri(combined).AbsoluteUri;

        using (var req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
#if UNITY_2020_3_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                LastError = $"読み込み失敗: {url} : {req.error}";
                LoadFailed?.Invoke(LastError);
                Debug.LogError($"[PoseCsvStore] {LastError}");
                yield break;
            }
            string text = req.downloadHandler.text;
            CurrentPath = url;
            Debug.Log($"[PoseCsvStore] OPEN OK : url={url}, length={text?.Length}");
            ParseCsv(text);
        }
    }

    void LoadFromAbsolute()
    {
        string path = filePath;
        if (!File.Exists(path))
        {
            LastError = $"ファイルなし: {path}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
            return;
        }
        string text = File.ReadAllText(path);
        CurrentPath = path;
        Debug.Log($"[PoseCsvStore] OPEN OK : path={CurrentPath}, length={text?.Length}");
        ParseCsv(text);
    }

    void ParseCsv(string text)
    {
        _frames.Clear();
        _accelsBody.Clear();
        _header.Clear();
        _numCols.Clear();
        _strCols.Clear();
        _scaledCache.Clear();

        var lines  = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            LastError = "CSV行不足";
            LoadFailed?.Invoke(LastError);
            Debug.LogError("[PoseCsvStore] CSV行不足");
            return;
        }

        var header = lines[0].Split(delimiter);
        for (int i = 0; i < header.Length; i++)
        {
            string h = (header[i] ?? "").Trim();
            _header.Add(h);
            _numCols[h] = new List<float>(lines.Length - 1); // まずは数値扱い
        }

        int idxT   = IndexOf(header, timeColumn);
        int idxDt  = IndexOf(header, dtColumn);
        int idxR   = IndexOf(header, rollColumn);
        int idxP   = IndexOf(header, pitchColumn);
        int idxY   = IndexOf(header, yawColumn);

        // 加速度列の位置（無くてもOK）
        int idxAX = IndexOf(header, axColumn);
        int idxAY = IndexOf(header, ayColumn);
        int idxAZ = IndexOf(header, azColumn);

        if (idxR < 0 || idxP < 0 || idxY < 0)
        {
            LastError = "roll/pitch/yaw 列が見つかりません";
            LoadFailed?.Invoke(LastError);
            Debug.LogError("[PoseCsvStore] roll/pitch/yaw 列が見つかりません");
            return;
        }

        var ci = CultureInfo.InvariantCulture;
        float maxAbs = 0f;
        var tmpAngles = new List<Vector3>();
        var tmpTimes  = new List<float>();

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split(delimiter);
            if (cols.Length < header.Length) continue;

            // 全列: 数値として試みる → 失敗なら文字列リストへ
            for (int c = 0; c < header.Length; c++)
            {
                string h = header[c];
                string s = (c < cols.Length) ? cols[c] : "";
                float fv;
                if (_numCols.ContainsKey(h))
                {
                    if (float.TryParse(s, NumberStyles.Float, ci, out fv))
                    {
                        _numCols[h].Add(fv);
                    }
                    else
                    {
                        if (!_strCols.ContainsKey(h))
                        {
                            _strCols[h] = new List<string>(lines.Length - 1);
                            for (int k = 1; k < i; k++) _strCols[h].Add("");
                        }
                        _strCols[h].Add(s);
                        while (_numCols[h].Count < i - 1) _numCols[h].Add(0f);
                        _numCols[h].Add(0f);
                    }
                }
            }

            // 角度
            if (!TryF(cols[idxR], ci, out float r)) continue;
            if (!TryF(cols[idxP], ci, out float p)) continue;
            if (!TryF(cols[idxY], ci, out float y)) continue;

            // 時刻（秒）
            float t;
            if (idxT >= 0 && TryF(cols[idxT], ci, out float tms))      t = tms * 0.001f;
            else if (idxDt >= 0 && TryF(cols[idxDt], ci, out float d)) t = (tmpTimes.Count==0)?0f: tmpTimes[tmpTimes.Count-1] + d*0.001f;
            else                                                       t = (tmpTimes.Count==0)?0f: tmpTimes[tmpTimes.Count-1] + 0.02f;

            tmpAngles.Add(new Vector3(r, p, y));
            tmpTimes.Add(t);
            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(r), Mathf.Abs(p), Mathf.Abs(y));

            // 加速度（機体座標, m/s^2 に統一）
            Vector3 aBody = Vector3.zero;
            if (idxAX >= 0 && idxAY >= 0 && idxAZ >= 0)
            {
                float ax=0, ay=0, az=0;
                TryF(cols[idxAX], ci, out ax);
                TryF(cols[idxAY], ci, out ay);
                TryF(cols[idxAZ], ci, out az);

                float[] tmp = new float[3];
                tmp[(int)axGoesTo] = axSign * ax;
                tmp[(int)ayGoesTo] = aySign * ay;
                tmp[(int)azGoesTo] = azSign * az;

                float g = (accelUnit == AccelUnit.G) ? 9.80665f : 1f;
                aBody = new Vector3(tmp[0], tmp[1], tmp[2]) * g;
            }
            _accelsBody.Add(aBody);
        }

        if (tmpTimes.Count == 0)
        {
            LastError = "有効データが0件";
            LoadFailed?.Invoke(LastError);
            Debug.LogError("[PoseCsvStore] データ空");
            return;
        }

        // t=0 揃え
        float t0 = tmpTimes[0];
        for (int i = 0; i < tmpTimes.Count; i++) tmpTimes[i] -= t0;

        bool assumeRad = anglesAreRadians || (autoDetectAngleUnits && maxAbs <= 6.5f);

        // Quaternion へ変換（軸割当＆符号）
        for (int i = 0; i < tmpAngles.Count; i++)
        {
            float r = tmpAngles[i].x, p = tmpAngles[i].y, y = tmpAngles[i].z;
            if (assumeRad) { r *= Mathf.Rad2Deg; p *= Mathf.Rad2Deg; y *= Mathf.Rad2Deg; }

            var eul = Vector3.zero;
            int signR = rollSign;
            int signP = pitchSign;
            int signY = yawSign * (flipYawSign ? -1 : 1);

            eul[(int)rollAxis]  = r * signR;
            eul[(int)pitchAxis] = p * signP;
            eul[(int)yawAxis]   = y * signY;

            _frames.Add(new Frame { t = tmpTimes[i], q = Quaternion.Euler(eul) });
        }

        _zero = (zeroAtStart && _frames.Count>0) ? Quaternion.Inverse(_frames[0].q) : Quaternion.identity;

        // 導出した時刻列（秒）を公開
        var tSeries = new List<float>(_frames.Count);
        for (int i = 0; i < _frames.Count; i++) tSeries.Add(_frames[i].t);
        _numCols["t"] = tSeries;

        LastError = null;
        Debug.Log($"[PoseCsvStore] Frames={_frames.Count}, Duration={Duration:F3}s, Units={(assumeRad?"rad":"deg")}, Path={CurrentPath}");
        Loaded?.Invoke();
    }

    // === 全カラム公開API（原本）===
    public IReadOnlyList<string> AllColumnNames => _header;
    public IReadOnlyList<string> NumericColumnNames
    {
        get
        {
            var list = new List<string>(_numCols.Keys);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
    public IReadOnlyList<string> StringColumnNames
    {
        get
        {
            var list = new List<string>(_strCols.Keys);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
    public bool TryGetNumericSeries(string columnName, out IReadOnlyList<float> series)
    {
        series = null;
        if (_numCols.TryGetValue(columnName, out var s) && s != null && s.Count > 0)
        {
            series = s;
            return true;
        }
        return false;
    }
    public bool TryGetStringSeries(string columnName, out IReadOnlyList<string> series)
    {
        series = null;
        if (_strCols.TryGetValue(columnName, out var s) && s != null && s.Count > 0)
        {
            series = s;
            return true;
        }
        return false;
    }

    /// <summary>時刻tで数値列を線形補間（原本）。列長とフレーム数が一致している前提。</summary>
    public float GetNumericAtTime(string columnName, float t)
    {
        if (!TryGetNumericSeries(columnName, out var s) || s.Count != _frames.Count) return 0f;
        if (_frames.Count == 0) return 0f;

        t = Mathf.Clamp(t, 0f, Duration);
        int lo = 0, hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_frames[mid].t < t) lo = mid + 1;
            else hi = mid - 1;
        }
        int i0 = Mathf.Clamp(lo - 1, 0, _frames.Count - 2);
        int i1 = i0 + 1;

        float t0 = _frames[i0].t, t1 = _frames[i1].t;
        float u = (t1 > t0) ? Mathf.InverseLerp(t0, t1, t) : 0f;
        return Mathf.Lerp(s[i0], s[i1], u);
    }

    // === 追加: 符号/スケール適用API（Signed系）===
    /// <summary>列名に対応する倍率（見つからなければ 1）</summary>
    public float GetMultiplier(string columnName)
    {
        if (columnScales == null) return 1f;
        for (int i = 0; i < columnScales.Count; i++)
        {
            var cs = columnScales[i];
            if (!string.IsNullOrEmpty(cs.columnName) &&
                string.Equals(cs.columnName.Trim(), columnName, StringComparison.OrdinalIgnoreCase))
                return cs.multiplier;
        }
        return 1f;
    }

    /// <summary>倍率を適用した数値列（必要なら内部でキャッシュ生成）</summary>
    public bool TryGetNumericSeriesSigned(string columnName, out IReadOnlyList<float> seriesSigned)
    {
        seriesSigned = null;
        if (!TryGetNumericSeries(columnName, out var raw)) return false;

        float m = GetMultiplier(columnName);
        if (Mathf.Approximately(m, 1f))
        {
            seriesSigned = raw; // 倍率1なら原本をそのまま返す
            return true;
        }

        if (_scaledCache.TryGetValue(columnName, out var cached) &&
            Mathf.Approximately(cached.m, m) &&
            cached.data != null && cached.data.Count == raw.Count)
        {
            seriesSigned = cached.data;
            return true;
        }

        // キャッシュ作成
        var scaled = new List<float>(raw.Count);
        for (int i = 0; i < raw.Count; i++) scaled.Add(raw[i] * m);
        _scaledCache[columnName] = (m, scaled);
        seriesSigned = scaled;
        return true;
    }

    /// <summary>倍率を適用した時刻補間値（Signed）</summary>
    public float GetNumericAtTimeSigned(string columnName, float t)
    {
        float m = GetMultiplier(columnName);
        float v = GetNumericAtTime(columnName, t);
        return v * m;
    }

    // === IMUから初速推定 ===
    public bool HasAcceleration => _accelsBody.Count == _frames.Count && _frames.Count > 0;

    public Vector3 EstimateInitialVelocity(float tStart, float tEnd)
    {
        if (!HasAcceleration) return Vector3.zero;
        tStart = Mathf.Clamp(tStart, 0f, Duration);
        tEnd   = Mathf.Clamp(tEnd,   tStart + 1e-4f, Duration);

        int lo0 = 0, hi0 = _frames.Count - 1;
        while (lo0 <= hi0)
        {
            int mid = (lo0 + hi0) >> 1;
            if (_frames[mid].t < tStart) lo0 = mid + 1;
            else hi0 = mid - 1;
        }
        int i0 = Mathf.Clamp(lo0, 0, _frames.Count - 2);

        int lo1 = 0, hi1 = _frames.Count - 1;
        while (lo1 <= hi1)
        {
            int mid = (lo1 + hi1) >> 1;
            if (_frames[mid].t < tEnd) lo1 = mid + 1;
            else hi1 = mid - 1;
        }
        int i1 = Mathf.Clamp(lo1, 1, _frames.Count - 1);

        Vector3 v = Vector3.zero;
        for (int i = i0; i < i1; i++)
        {
            float ta = _frames[i].t, tb = _frames[i + 1].t;
            float dt = Mathf.Max(1e-5f, tb - ta);
            Quaternion qWb = _frames[i].q;
            Vector3 aWorld = qWb * _accelsBody[i];

            if (!accelIsLinear)
            {
                aWorld += new Vector3(0f, -9.81f, 0f);
            }
            v += aWorld * dt;
        }
        return v;
    }

    // helpers
    static int IndexOf(string[] arr, string name)
    {
        for (int i = 0; i < arr.Length; i++)
            if (string.Equals(arr[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    static bool TryF(string s, IFormatProvider ci, out float v)
    {
        if (float.TryParse(s, NumberStyles.Float, ci, out v)) return true;
        s = s.Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
    static void Unwrap(ref Vector3[] arr)
    {
        float ox=0, oy=0, oz=0;
        for (int i = 1; i < arr.Length; i++)
        {
            ox += WrapDelta(arr[i-1].x, arr[i].x); arr[i].x += ox;
            oy += WrapDelta(arr[i-1].y, arr[i].y); arr[i].y += oy;
            oz += WrapDelta(arr[i-1].z, arr[i].z); arr[i].z += oz;
        }
        static float WrapDelta(float prev,float curr){ float d=curr-prev; if(d>180f) return -360f; if(d<-180f) return 360f; return 0f; }
    }
}
