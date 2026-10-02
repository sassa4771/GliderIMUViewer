// Assets/Scripts/RealtimePreview/RealtimePoseStore.cs
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 受信した roll/pitch/yaw を Unity の姿勢に変換して保持する（PoseCsvStore のリアルタイム版）。
/// 最新の姿勢・グラフ用の履歴・受信統計・LOG 行を提供する。
/// </summary>
[DefaultExecutionOrder(-90)]
public class RealtimePoseStore : MonoBehaviour
{
    [Header("Source")]
    public UdpTelemetryReceiver receiver;

    [Header("Field names")]
    public string rollField  = "roll";
    public string pitchField = "pitch";
    public string yawField   = "yaw";

    [Header("Angle units & axis map")]
    public bool anglesAreRadians = false;

    [Tooltip("roll / pitch / yaw を Unity のどの軸まわりの回転にするかと、その符号。\n" +
             "機体モデルは胴体が Z 軸（機首が -Z）、翼幅が X 軸、上が Y 軸。")]
    public AttitudeAxisMap axisMap = AttitudeAxisMap.ImuXTowardTail;

    public enum ComposeOrder { YawPitchRoll, UnityEuler }
    [Tooltip("YawPitchRoll: yaw→pitch→roll の順に合成（Madgwick など一般的な航空機オイラー角の定義どおり）。\n" +
             "UnityEuler: Quaternion.Euler で合成（KinematicPreview の PoseCsvStore と同じ結果）。")]
    public ComposeOrder composeOrder = ComposeOrder.YawPitchRoll;

    /// <summary>軸の割当のプリセット（Custom は Inspector で個別に設定した状態）</summary>
    public enum AxisPreset
    {
        ImuXTowardTail,     // IMU の +X が機尾向き（現行ファーム: 発射時に ax が負になる）
        ImuXTowardNose,     // IMU の +X が機首向き
        KinematicPreview,   // KinematicPreview シーンと同じ見え方（割当も合成順も PoseCsvStore と同一）
        Custom,
    }

    public enum ZeroMode
    {
        None,       // 補正なし（受信値のまま）
        YawOnly,    // 機首方位(yaw)だけを 0 に合わせる。roll/pitch は受信値のまま
        Level,      // YawOnly に加えて、ゼロ点を取ったときの傾きを「水平」とみなす（IMU の取り付けの傾きを打ち消す）
        Relative,   // ゼロ点を取ったときの姿勢からの相対回転（PoseCsvStore の zeroAtStart と同じ計算）
    }

    [Header("Zero")]
    [Tooltip("None: 補正なし\n" +
             "YawOnly: 機首方位だけ 0 に合わせる（roll・pitch は受信値のまま）\n" +
             "Level: 機体を水平に置いて Zero を押すと、そのときの傾きを水平とみなす（IMU の取り付けの傾きを打ち消す）\n" +
             "Relative: ゼロ点の姿勢からの相対回転（KinematicPreview の zeroAtStart と同じ計算）")]
    public ZeroMode zeroMode = ZeroMode.YawOnly;
    [Tooltip("最初のサンプルでゼロ点を取る（機体の再起動を検出したときは、方位のゼロ点だけ取り直す）")]
    public bool zeroOnFirstSample = true;

    [Header("History (graph)")]
    [Tooltip("履歴に保持するサンプル数（グラフの時間幅 × 受信レート より多くする）")]
    [Min(16)] public int historyCapacity = 8192;
    [Tooltip("t_ms があるときは機体側の時刻でグラフの横軸を刻む（まとめて届いても等間隔になる）")]
    public bool preferDeviceTime = true;
    [Tooltip("この秒数以上あいたら、グラフの線をつながない")]
    public float gapBreakSeconds = 0.5f;

    [Header("Sanity checks")]
    [Tooltip("roll/pitch/yaw の絶対値がこれ[deg]を超えるサンプルは、フィールドの対応付け違いとみなして無視する（0 で無効）")]
    public float maxAbsAngle = 3600f;
    [Tooltip("名前付きの形式（DAT / JSON / key:value）が届いている間は、位置で対応付ける形式（OSC / 数値CSV）を無視する。\n" +
             "同じデータを2つの形式で同時に受けても、表示がちらつかないようにするため。")]
    public bool preferNamedFormats = true;

    [Header("Status")]
    [Tooltip("この秒数データが来なければ「途絶」とみなす")]
    public float staleAfterSeconds = 1.0f;
    [Tooltip("周期的に流れてくる LOG（この接頭辞で始まるもの）は LastEventLog を上書きしない")]
    public string[] periodicLogPrefixes = { "Param:" };

    // === 公開API ===
    public bool HasPose { get; private set; }
    /// <summary>モデルに適用する姿勢（ゼロ点適用後）</summary>
    public Quaternion Rotation { get; private set; } = Quaternion.identity;
    /// <summary>受信した roll/pitch/yaw [deg]</summary>
    public Vector3 ReceivedRpy { get; private set; }
    /// <summary>表示している姿勢の roll/pitch/yaw [deg]（ゼロ点適用後。ZeroMode.None のときは受信値そのまま）</summary>
    public Vector3 DisplayRpy { get; private set; }

    public bool Paused { get; private set; }
    public long SampleCount { get; private set; }
    public long LostCount { get; private set; }
    public double LastSampleTime { get; private set; } = double.NegativeInfinity;
    /// <summary>最後に姿勢として採用したサンプルの形式</summary>
    public TelemetryWireFormat LastSampleFormat { get; private set; } = TelemetryWireFormat.Unknown;
    public int ZeroCount { get; private set; }
    /// <summary>機体側の時刻(t_ms)が戻った回数（機体の再起動）</summary>
    public int RestartCount { get; private set; }
    /// <summary>角度が範囲外で無視したサンプル数と、その最後の値・時刻</summary>
    public long RejectedCount { get; private set; }
    public Vector3 LastRejectedRpy { get; private set; }
    public double LastRejectedTime { get; private set; } = double.NegativeInfinity;
    /// <summary>名前付きの形式を優先して無視した OSC / 数値CSV のサンプル数</summary>
    public long ShadowedCount { get; private set; }

    public string LastLog { get; private set; }
    public double LastLogTime { get; private set; } = double.NegativeInfinity;
    public string LastEventLog { get; private set; }
    public double LastEventLogTime { get; private set; } = double.NegativeInfinity;

    public event Action PoseUpdated;
    public event Action Zeroed;
    public event Action<string> LogReceived;

    /// <summary>グラフの右端にする「いま」（受信時計の秒）。一時停止中は止めた時刻のまま。</summary>
    public double DisplayNow => Paused ? _pausedAt : UdpTelemetryReceiver.Now;

    /// <summary>最後のサンプルからの経過秒（未受信なら +∞）</summary>
    public double SecondsSinceLastSample => UdpTelemetryReceiver.Now - LastSampleTime;
    public bool IsStale => !HasPose || SecondsSinceLastSample > staleAfterSeconds;

    // === 内部 ===
    const long MaxSeqGap = 1000;                 // これより大きい連番の飛びは、欠落ではなく送信側の切り替わりとみなす
    const int MaxLogLength = 160;
    const double RestartBackstepMs = 1000.0;     // これ以上 t_ms が戻ったら再起動とみなす（順序入れ替わり程度では反応しない）

    Vector3 _rpy;                        // 最新の受信値 [deg]（来なかった成分は前回値）
    bool _zeroPending;                   // 次のサンプルでゼロ点を取る
    bool _zeroYawPending;                // 次のサンプルで方位のゼロ点だけ取り直す（傾きの補正は保つ）
    Vector3 _zeroRpy;                    // ゼロ点を取ったときの受信値 [deg]
    double _pausedAt;

    bool _hasLastSeq;
    long _lastSeq;
    bool _hasLastDeviceMs;
    double _lastDeviceMs;
    double _lastNamedTime = double.NegativeInfinity;

    // 受信レート計測用（受信時刻のリング）
    const int RateWindow = 256;
    readonly double[] _arrivals = new double[RateWindow];
    int _arrivalHead, _arrivalCount;

    // 機体時刻 → 受信時計への対応付け
    bool _devAnchorValid;
    double _devAnchorDev, _devAnchorArrival, _lastDev;

    // 履歴（リング）
    double[] _hTime;
    Vector3[] _hRpy;
    bool[] _hBreak;
    int _hHead, _hCount;
    long _hSerial;                       // これまでに積んだ総数
    bool _breakNext = true;
    double _lastHistTime = double.NegativeInfinity;

    readonly Dictionary<string, float> _latest = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _logByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    void Awake() { EnsureHistory(); }

    void OnEnable()
    {
        EnsureHistory();
        if (receiver) receiver.MessageReceived += OnMessage;
    }

    void OnDisable()
    {
        if (receiver) receiver.MessageReceived -= OnMessage;
    }

    void OnValidate()
    {
        axisMap.rollSign  = AttitudeMath.Sgn(axisMap.rollSign);
        axisMap.pitchSign = AttitudeMath.Sgn(axisMap.pitchSign);
        axisMap.yawSign   = AttitudeMath.Sgn(axisMap.yawSign);
        if (!axisMap.IsValid)
            Debug.LogWarning("[RealtimePoseStore] Axis Map: roll / pitch / yaw は互いに異なる軸に割り当ててください。", this);
        else if (!axisMap.IsRigid)
            Debug.LogWarning("[RealtimePoseStore] Axis Map: この軸と符号の組み合わせは実在する取り付け方に対応しません" +
                             "（どれか1つの角度が実機と逆向きに動きます）。符号を1つ反転してください。", this);
        else if (axisMap.yawAxis != AttitudeAxis.Y || axisMap.yawSign > 0)
            Debug.LogWarning("[RealtimePoseStore] Axis Map: 上が +Y の機体モデルでは、yaw は Y 軸・符号 -1 にしてください" +
                             "（それ以外では、機首方位の変化が傾きとして表示されるか、裏返しの取り付けとして扱われます）。", this);
    }

    void EnsureHistory()
    {
        int cap = Mathf.Max(16, historyCapacity);
        if (_hTime != null && _hTime.Length == cap) return;
        _hTime = new double[cap];
        _hRpy = new Vector3[cap];
        _hBreak = new bool[cap];
        _hHead = 0; _hCount = 0;
        _breakNext = true;
    }

    //=== 操作 ===

    /// <summary>現在の姿勢をゼロ点にする（最新サンプルがあれば即時、なければ次のサンプルで）。</summary>
    public void Zero()
    {
        if (!HasPose) { _zeroPending = true; return; }
        CaptureZero(false);
        Recompute();
    }

    public void SetZeroMode(ZeroMode mode)
    {
        if (zeroMode == mode) return;
        zeroMode = mode;
        Zero();
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        if (paused) _pausedAt = UdpTelemetryReceiver.Now;
        Paused = paused;
        if (!paused) _breakNext = true;     // 再開後は線をつながない
    }

    public void ClearHistory()
    {
        _hHead = 0; _hCount = 0;
        _hSerial++;
        _breakNext = true;
    }

    /// <summary>現在の軸割当・合成順がどのプリセットに当たるか。</summary>
    public AxisPreset CurrentPreset
    {
        get
        {
            if (composeOrder == ComposeOrder.YawPitchRoll)
            {
                if (axisMap.Equals(AttitudeAxisMap.ImuXTowardTail)) return AxisPreset.ImuXTowardTail;
                if (axisMap.Equals(AttitudeAxisMap.ImuXTowardNose)) return AxisPreset.ImuXTowardNose;
            }
            else if (axisMap.Equals(AttitudeAxisMap.KinematicPreview)) return AxisPreset.KinematicPreview;
            return AxisPreset.Custom;
        }
    }

    /// <summary>軸割当をプリセットに切り替える（Custom を渡したときは何もしない）。</summary>
    public void ApplyPreset(AxisPreset preset)
    {
        switch (preset)
        {
            case AxisPreset.ImuXTowardTail:
                axisMap = AttitudeAxisMap.ImuXTowardTail; composeOrder = ComposeOrder.YawPitchRoll; break;
            case AxisPreset.ImuXTowardNose:
                axisMap = AttitudeAxisMap.ImuXTowardNose; composeOrder = ComposeOrder.YawPitchRoll; break;
            case AxisPreset.KinematicPreview:
                axisMap = AttitudeAxisMap.KinematicPreview; composeOrder = ComposeOrder.UnityEuler; break;
            default: return;
        }
        if (!HasPose) return;
        Recompute();
        _breakNext = true;
        Zeroed?.Invoke();       // 表示側は補間せずに切り替える
    }

    [ContextMenu("Axis preset: IMU +X toward tail")]
    void PresetTail() { ApplyPreset(AxisPreset.ImuXTowardTail); }
    [ContextMenu("Axis preset: IMU +X toward nose")]
    void PresetNose() { ApplyPreset(AxisPreset.ImuXTowardNose); }
    [ContextMenu("Axis preset: same as KinematicPreview")]
    void PresetKinematicPreview() { ApplyPreset(AxisPreset.KinematicPreview); }

    //=== 受信 ===

    void OnMessage(TelemetryMessage m, double arrival)
    {
        switch (m.kind)
        {
            case TelemetryMessageKind.Log:    OnLog(m.text, arrival); break;
            case TelemetryMessageKind.Sample: OnSample(m, arrival); break;
        }
    }

    void OnLog(string text, double arrival)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = Sanitize(text);
        if (text.Length == 0) return;
        LastLog = text;
        LastLogTime = arrival;

        // "mode:Manual" "Param:Pp=2.00,..." のような「キー:値」の LOG は、キーごとに最新を覚えておく
        int colon = text.IndexOf(':');
        if (colon > 0 && colon <= 24) _logByKey[text.Substring(0, colon).Trim()] = text.Substring(colon + 1).Trim();

        bool periodic = false;
        if (periodicLogPrefixes != null)
            for (int i = 0; i < periodicLogPrefixes.Length && !periodic; i++)
                periodic = !string.IsNullOrEmpty(periodicLogPrefixes[i]) &&
                           text.StartsWith(periodicLogPrefixes[i], StringComparison.OrdinalIgnoreCase);
        if (!periodic) { LastEventLog = text; LastEventLogTime = arrival; }

        LogReceived?.Invoke(text);
    }

    /// <summary>"mode:Manual" のような LOG の「値」側を、キー（"mode"）で取り出す。</summary>
    public bool TryGetLog(string key, out string value) { return _logByKey.TryGetValue(key, out value); }

    /// <summary>最新サンプルに含まれていた任意の値（サーボ角など）</summary>
    public bool TryGetValue(string name, out float value)
    {
        if (string.IsNullOrEmpty(name)) { value = 0f; return false; }
        return _latest.TryGetValue(name, out value);
    }

    void OnSample(TelemetryMessage m, double arrival)
    {
        bool hasR = m.TryGet(rollField,  out float r) && IsFinite(r);
        bool hasP = m.TryGet(pitchField, out float p) && IsFinite(p);
        bool hasY = m.TryGet(yawField,   out float y) && IsFinite(y);
        bool any = hasR || hasP || hasY;
        float scale = anglesAreRadians ? Mathf.Rad2Deg : 1f;

        if (any)
        {
            // 同じデータが2つの形式で届いているとき（例: ビューアの OSC 出力と生の行の転送）は、名前付きの方だけ使う
            bool named = m.format == TelemetryWireFormat.Dat || m.format == TelemetryWireFormat.Json ||
                         m.format == TelemetryWireFormat.KeyValue;
            if (named) _lastNamedTime = arrival;
            else if (preferNamedFormats && arrival - _lastNamedTime < 1.0)
            {
                ShadowedCount++;
                return;
            }

            // 角度としてあり得ない値は、送信側のフィールド順の違い（seq や t_ms を角度として読んでいる等）とみなして無視する
            if (maxAbsAngle > 0f &&
                ((hasR && Mathf.Abs(r * scale) > maxAbsAngle) ||
                 (hasP && Mathf.Abs(p * scale) > maxAbsAngle) ||
                 (hasY && Mathf.Abs(y * scale) > maxAbsAngle)))
            {
                RejectedCount++;
                LastRejectedRpy = new Vector3(hasR ? r * scale : 0f, hasP ? p * scale : 0f, hasY ? y * scale : 0f);
                LastRejectedTime = arrival;
                return;
            }
        }

        // 受信統計は一時停止中も数える
        if (any)
        {
            SampleCount++;
            LastSampleTime = arrival;
            LastSampleFormat = m.format;
            _arrivals[_arrivalHead] = arrival;
            _arrivalHead = (_arrivalHead + 1) % RateWindow;
            if (_arrivalCount < RateWindow) _arrivalCount++;
        }
        if (m.hasSeq)
        {
            // 連番の飛びを数える（番号が戻った・大きく飛んだ場合は送信側のリセットとみなして数えない）
            long gap = m.seq - _lastSeq;
            if (_hasLastSeq && gap > 1 && gap <= MaxSeqGap) LostCount += gap - 1;
            _lastSeq = m.seq;
            _hasLastSeq = true;
        }
        if (m.hasDeviceTimeMs)
        {
            // 機体側の時刻が大きく戻った = 機体（姿勢推定）が再起動した → 方位の基準が変わるので、方位のゼロ点を取り直す。
            // 傾きの補正（Level）は機体への取り付けで決まるものなので、そのまま保つ。
            if (_hasLastDeviceMs && m.deviceTimeMs < _lastDeviceMs - RestartBackstepMs)
            {
                RestartCount++;
                if (zeroOnFirstSample && HasPose) _zeroYawPending = true;
            }
            _lastDeviceMs = m.deviceTimeMs;
            _hasLastDeviceMs = true;
        }

        if (Paused) return;

        foreach (var kv in m.values) _latest[kv.Key] = kv.Value;
        if (!any) return;

        // 来なかった成分は前回値のまま
        if (hasR) _rpy.x = r * scale;
        if (hasP) _rpy.y = p * scale;
        if (hasY) _rpy.z = y * scale;

        ReceivedRpy = _rpy;
        if (!HasPose)
        {
            HasPose = true;
            if (zeroOnFirstSample) _zeroPending = true;
        }
        if (_zeroPending) CaptureZero(false);
        else if (_zeroYawPending) CaptureZero(true);

        Recompute();
        AppendHistory(HistoryTime(m, arrival));
        PoseUpdated?.Invoke();
    }

    void CaptureZero(bool yawOnly)
    {
        _zeroPending = false;
        _zeroYawPending = false;
        if (yawOnly) _zeroRpy.z = _rpy.z;
        else _zeroRpy = _rpy;
        _breakNext = true;      // ゼロ点の前後は線をつながない
        ZeroCount++;
        Zeroed?.Invoke();
    }

    // 最新の受信値とゼロ点から、表示用の姿勢と角度を求める
    void Recompute()
    {
        switch (zeroMode)
        {
            case ZeroMode.None:
                // 受信値をそのまま見せる（yaw が 0..360 で届くなら 0..360 のまま）
                Rotation = Compose(_rpy.x, _rpy.y, _rpy.z);
                DisplayRpy = _rpy;
                break;
            case ZeroMode.YawOnly:
            {
                // yaw を引いてから合成する（ファームが足している +180 のオフセットもここで消える）
                float yaw = AttitudeMath.Wrap180(_rpy.z - _zeroRpy.z);
                Rotation = Compose(_rpy.x, _rpy.y, yaw);
                DisplayRpy = new Vector3(AttitudeMath.Wrap180(_rpy.x), AttitudeMath.Wrap180(_rpy.y), yaw);
                break;
            }
            case ZeroMode.Level:
            {
                // 方位は YawOnly と同じ。ゼロ点での傾き (roll0, pitch0) は「機体に対する IMU の傾き」とみなし、機体側（右側）から打ち消す。
                // こうしておくと、機首の向きが変わっても補正がずれない。
                float yaw = AttitudeMath.Wrap180(_rpy.z - _zeroRpy.z);
                Rotation = Compose(_rpy.x, _rpy.y, yaw) * Quaternion.Inverse(Compose(_zeroRpy.x, _zeroRpy.y, 0f));
                DisplayRpy = Decompose(Rotation);
                break;
            }
            default:
                Rotation = Quaternion.Inverse(Compose(_zeroRpy.x, _zeroRpy.y, _zeroRpy.z)) * Compose(_rpy.x, _rpy.y, _rpy.z);
                DisplayRpy = Decompose(Rotation);
                break;
        }
    }

    //=== 姿勢の合成 / 分解 ===

    /// <summary>roll/pitch/yaw [deg] → Unity の回転（軸割当と符号、合成順を適用）</summary>
    public Quaternion Compose(float rollDeg, float pitchDeg, float yawDeg)
    {
        return composeOrder == ComposeOrder.UnityEuler
            ? AttitudeMath.ComposeLegacy(rollDeg, pitchDeg, yawDeg, axisMap)
            : AttitudeMath.ComposeAerospace(rollDeg, pitchDeg, yawDeg, axisMap);
    }

    /// <summary>Compose の逆変換: Unity の回転 → roll/pitch/yaw [deg]（±180）。</summary>
    public Vector3 Decompose(Quaternion q)
    {
        return composeOrder == ComposeOrder.UnityEuler
            ? AttitudeMath.DecomposeLegacy(q, axisMap)
            : AttitudeMath.DecomposeAerospace(q, axisMap);
    }

    static bool IsFinite(float v) { return !(float.IsNaN(v) || float.IsInfinity(v)); }

    // 送信側から来た文字列を画面に出せる形にする（UI のフォントは英数字のみ。制御文字・非 ASCII は '?'、長すぎる行は切る）
    static string Sanitize(string text)
    {
        text = text.Trim();
        bool clean = text.Length <= MaxLogLength;
        for (int i = 0; clean && i < text.Length; i++) clean = text[i] >= ' ' && text[i] <= '~';
        if (clean) return text;

        int n = Mathf.Min(text.Length, MaxLogLength);
        var sb = new StringBuilder(n + 3);
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            sb.Append(c >= ' ' && c <= '~' ? c : (c == (char)9 ? ' ' : '?'));
        }
        if (text.Length > n) sb.Append("...");
        return sb.ToString();
    }

    //=== 受信レート ===

    /// <summary>直近1秒間の受信レート [Hz]</summary>
    public float RateHz
    {
        get
        {
            if (_arrivalCount < 2) return 0f;
            double now = UdpTelemetryReceiver.Now;
            double newest = 0, oldest = 0;
            int n = 0;
            for (int i = 0; i < _arrivalCount; i++)
            {
                double t = _arrivals[(_arrivalHead - 1 - i + RateWindow * 2) % RateWindow];
                if (now - t > 1.0) break;
                if (n == 0) newest = t;
                oldest = t;
                n++;
            }
            if (n < 2 || newest <= oldest) return n;       // 1秒以内に n 件
            return (float)((n - 1) / (newest - oldest));
        }
    }

    //=== 履歴 ===

    // グラフ横軸の時刻（受信時計の秒）。t_ms があれば機体側の刻みを使い、時計のずれ・リセットは付け直す。
    double HistoryTime(TelemetryMessage m, double arrival)
    {
        double t = arrival;
        if (preferDeviceTime && m.hasDeviceTimeMs)
        {
            double dev = m.deviceTimeMs * 0.001;
            if (_devAnchorValid && dev >= _lastDev)
            {
                double mapped = _devAnchorArrival + (dev - _devAnchorDev);
                double err = arrival - mapped;
                if (Math.Abs(err) <= 1.0)
                {
                    // 受信時計にゆっくり追従させる（時計の進みの差や遅延の変化を吸収）
                    _devAnchorArrival += err * 0.02;
                    t = mapped + err * 0.02;
                }
                else _devAnchorValid = false;                   // 大きくずれた → 付け直す
            }
            else _devAnchorValid = false;                       // 初回、または機体時刻が戻った（再起動など）

            if (!_devAnchorValid)
            {
                _devAnchorValid = true;
                _devAnchorDev = dev;
                _devAnchorArrival = arrival;
                t = arrival;
            }
            _lastDev = dev;
        }
        else _devAnchorValid = false;

        if (t < _lastHistTime) t = _lastHistTime;               // 単調増加を保証
        return t;
    }

    void AppendHistory(double t)
    {
        EnsureHistory();
        if (t - _lastHistTime > gapBreakSeconds) _breakNext = true;
        _lastHistTime = t;

        _hTime[_hHead] = t;
        _hRpy[_hHead] = DisplayRpy;
        _hBreak[_hHead] = _breakNext;
        _breakNext = false;
        _hHead = (_hHead + 1) % _hTime.Length;
        if (_hCount < _hTime.Length) _hCount++;
        _hSerial++;
    }

    /// <summary>履歴の件数（古い順に index 0 .. HistoryCount-1）</summary>
    public int HistoryCount => _hCount;
    /// <summary>履歴が変わるたびに増える（再描画の判定用）</summary>
    public long HistorySerial => _hSerial;
    /// <summary>index 0 のサンプルの通し番号（間引きを安定させる用）</summary>
    public long HistoryFirstSerial => _hSerial - _hCount;
    public double HistoryNewestTime => _hCount > 0 ? GetHistoryTime(_hCount - 1) : 0.0;

    public double GetHistoryTime(int index)
    {
        return _hTime[(_hHead - _hCount + index + _hTime.Length * 2) % _hTime.Length];
    }

    /// <summary>index 番目（古い順）の履歴。rpy はそのとき表示していた roll/pitch/yaw [deg]（DisplayRpy）。</summary>
    public void GetHistory(int index, out double time, out Vector3 rpy, out bool breakBefore)
    {
        int i = (_hHead - _hCount + index + _hTime.Length * 2) % _hTime.Length;
        time = _hTime[i];
        rpy = _hRpy[i];
        breakBefore = _hBreak[i];
    }
}
