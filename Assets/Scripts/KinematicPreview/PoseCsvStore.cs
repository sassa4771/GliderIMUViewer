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

    [Header("Normalization")]
    public bool zeroAtStart = true;       // 先頭姿勢を基準ゼロに

    [Header("Startup")]
    public bool loadOnStart = true;       // 起動時に自動読み込みするか

    // 公開API
    public bool IsLoaded => _frames.Count > 0;
    public float Duration => _frames.Count > 0 ? _frames[_frames.Count - 1].t : 0f;
    public string CurrentPath { get; private set; }  // 実際に読み込んだパス/URL
    public string LastError  { get; private set; }   // 直近のエラー

    public event Action Loaded;                 // 成功
    public event Action<string> LoadFailed;     // 失敗（理由）

    //=== 追加：プラットフォームに応じたファイルダイアログ ===
#if UNITY_WEBGL && !UNITY_EDITOR
    // WebGL の JS プラグイン（FilePicker.jslib）とのブリッジ
    [DllImport("__Internal")] private static extern void FilePicker_OpenFileDialog(string gameObjectName, string accept);
#endif

    /// <summary>OS/ブラウザのダイアログを開いてCSVを読み込む（Windows/Mac/Linux/WebGL対応）</summary>
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
            // ブラウザのファイル入力を開く（.csv と text/csv を許可）
            FilePicker_OpenFileDialog(gameObject.name, ".csv,text/csv");
            Debug.Log("[PoseCsvStore] WebGL FilePicker を呼び出しました");
        }
        catch (Exception ex)
        {
            LastError = $"WebGLダイアログ呼び出し失敗: {ex.Message}";
            LoadFailed?.Invoke(LastError);
            Debug.LogError($"[PoseCsvStore] {LastError}");
        }
#else
        LastError = "このプラットフォームではOSダイアログ未対応です。";
        LoadFailed?.Invoke(LastError);
        Debug.LogWarning($"[PoseCsvStore] {LastError}");
#endif
    }

    //=== 追加：WebGLからのコールバック（.jslib が SendMessage してくる） ===
    [Serializable] private class WebGLPickPayload { public string name; public string data; } // data = Base64

    // 成功：JSON { name, data(base64) }
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

    // 失敗/キャンセル：文字列メッセージ
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
            var e = q.eulerAngles; // 0..360 → -180..180
            e.x = (e.x > 180f) ? e.x - 360f : e.x;
            e.y = (e.y > 180f) ? e.y - 360f : e.y;
            e.z = (e.z > 180f) ? e.z - 360f : e.z;
            eulersDeg[i] = e;
        }
        Unwrap(ref eulersDeg);
    }

    // 指定時刻の姿勢（補間）
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

    //=== 外部から読み込み（パス/テキスト/URL等） ===
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

    void Start()
    {
        if (!loadOnStart) return;
        if (useStreamingAssetsPath) StartCoroutine(LoadFromStreamingAssets());
        else LoadFromAbsolute();
    }

    // StreamingAssets → 常に URI 化してから取得
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

        var lines  = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            LastError = "CSV行不足";
            LoadFailed?.Invoke(LastError);
            Debug.LogError("[PoseCsvStore] CSV行不足");
            return;
        }

        var header = lines[0].Split(delimiter);
        int idxT   = IndexOf(header, timeColumn);
        int idxDt  = IndexOf(header, dtColumn);
        int idxR   = IndexOf(header, rollColumn);
        int idxP   = IndexOf(header, pitchColumn);
        int idxY   = IndexOf(header, yawColumn);
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

            if (!TryF(cols[idxR], ci, out float r)) continue;
            if (!TryF(cols[idxP], ci, out float p)) continue;
            if (!TryF(cols[idxY], ci, out float y)) continue;

            float t;
            if (idxT >= 0 && TryF(cols[idxT], ci, out float tms))      t = tms * 0.001f;
            else if (idxDt >= 0 && TryF(cols[idxDt], ci, out float d)) t = (tmpTimes.Count==0)?0f: tmpTimes[^1] + d*0.001f;
            else                                                       t = (tmpTimes.Count==0)?0f: tmpTimes[^1] + 0.02f;

            tmpAngles.Add(new Vector3(r, p, y));
            tmpTimes.Add(t);
            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(r), Mathf.Abs(p), Mathf.Abs(y));
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
            eul[(int)rollAxis]  = r * rollSign;
            eul[(int)pitchAxis] = p * pitchSign;
            eul[(int)yawAxis]   = y * yawSign;

            _frames.Add(new Frame { t = tmpTimes[i], q = Quaternion.Euler(eul) });
        }

        _zero = (zeroAtStart && _frames.Count>0) ? Quaternion.Inverse(_frames[0].q) : Quaternion.identity;

        LastError = null;
        Debug.Log($"[PoseCsvStore] Frames={_frames.Count}, Duration={Duration:F3}s, Units={(assumeRad?"rad":"deg")}, Path={CurrentPath}");
        Loaded?.Invoke();
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
    static void Unwrap(ref Vector3[] arr) // ±180°跨ぎの連続化
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
