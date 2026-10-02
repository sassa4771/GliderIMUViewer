using System;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// リアルタイム表示シーンの UI: 受信状態・角度の数値・LOG の表示と、一時停止 / ゼロ点 / ポート変更の操作。
/// </summary>
public class RealtimeHud : MonoBehaviour
{
    [Header("Refs")]
    public UdpTelemetryReceiver receiver;
    public RealtimePoseStore store;

    [Header("Status")]
    public TMP_Text statusLabel;        // LIVE / WAITING / NO DATA / ERROR
    public TMP_Text infoLabel;          // 受信数など
    public TMP_Text detailLabel;        // 機体からの LOG・サーボ角など（複数行）

    [Header("Angles")]
    public TMP_Text rollLabel;
    public TMP_Text pitchLabel;
    public TMP_Text yawLabel;

    [Header("Graph")]
    public RealtimeGraphView graph;
    [Tooltip("ラベルをクリックすると、その線の表示/非表示を切り替える")]
    public Button rollToggle;
    public Button pitchToggle;
    public Button yawToggle;

    [Header("Controls")]
    public Button pauseButton;
    public TMP_Text pauseButtonText;
    public string labelPause = "Pause";
    public string labelResume = "Resume";
    public Button zeroButton;
    public Button zeroModeButton;
    public TMP_Text zeroModeButtonText;
    public Button axisPresetButton;         // IMU の取り付け向き（軸割当のプリセット）を切り替える
    public TMP_Text axisPresetButtonText;
    public TMP_InputField portInput;
    public Button portApplyButton;

    [Header("Keys")]
    public KeyCode pauseKey = KeyCode.Space;
    public KeyCode zeroKey = KeyCode.Z;

    [Header("Display")]
    [Tooltip("受信データに含まれていれば表示するフィールド名（サーボ角など）")]
    public string[] extraFields = { "s0", "s1", "s2", "sv1", "sv3" };
    [Min(0.02f)] public float refreshInterval = 0.1f;

    // 暗い帯の上に出すので明るめの色
    const string ColLive = "#7CFF8A", ColWait = "#FFD84A", ColError = "#FF7B6B", ColIdle = "#D8D8D8";
    const string AxisPresetPrefKey = "RealtimePreview.axisPreset";
    const double ProblemWindowSeconds = 2.0;    // この秒数以内に起きた「読めなかった」だけを原因として表示する

    readonly StringBuilder _sb = new StringBuilder(256);
    float _nextRefresh;
    string _localAddresses = "";

    // 登録は OnEnable で行う（再生中にスクリプトを再コンパイルすると、コードから足したリスナーは消えるため）
    void OnEnable()
    {
        if (receiver) receiver.StateChanged += OnReceiverStateChanged;
        if (pauseButton)      pauseButton.onClick.AddListener(OnPauseClicked);
        if (zeroButton)       zeroButton.onClick.AddListener(OnZeroClicked);
        if (zeroModeButton)   zeroModeButton.onClick.AddListener(OnZeroModeClicked);
        if (axisPresetButton) axisPresetButton.onClick.AddListener(OnAxisPresetClicked);
        if (portApplyButton)  portApplyButton.onClick.AddListener(OnPortApplyClicked);
        if (portInput)        portInput.onSubmit.AddListener(OnPortSubmitted);
        if (rollToggle)       rollToggle.onClick.AddListener(ToggleRoll);
        if (pitchToggle)      pitchToggle.onClick.AddListener(TogglePitch);
        if (yawToggle)        yawToggle.onClick.AddListener(ToggleYaw);
    }

    void OnDisable()
    {
        if (receiver) receiver.StateChanged -= OnReceiverStateChanged;
        if (pauseButton)      pauseButton.onClick.RemoveListener(OnPauseClicked);
        if (zeroButton)       zeroButton.onClick.RemoveListener(OnZeroClicked);
        if (zeroModeButton)   zeroModeButton.onClick.RemoveListener(OnZeroModeClicked);
        if (axisPresetButton) axisPresetButton.onClick.RemoveListener(OnAxisPresetClicked);
        if (portApplyButton)  portApplyButton.onClick.RemoveListener(OnPortApplyClicked);
        if (portInput)        portInput.onSubmit.RemoveListener(OnPortSubmitted);
        if (rollToggle)       rollToggle.onClick.RemoveListener(ToggleRoll);
        if (pitchToggle)      pitchToggle.onClick.RemoveListener(TogglePitch);
        if (yawToggle)        yawToggle.onClick.RemoveListener(ToggleYaw);
    }

    void OnPortSubmitted(string _) { OnPortApplyClicked(); }
    void ToggleRoll()  { ToggleSeries(0); }
    void TogglePitch() { ToggleSeries(1); }
    void ToggleYaw()   { ToggleSeries(2); }

    void Start()
    {
        _localAddresses = LocalIPv4Addresses();
#if !UNITY_EDITOR
        // ビルド版では、前回ボタンで選んだ取り付け向きを引き継ぐ（Editor では Inspector の設定を使う）
        if (store && PlayerPrefs.HasKey(AxisPresetPrefKey))
            store.ApplyPreset((RealtimePoseStore.AxisPreset)PlayerPrefs.GetInt(AxisPresetPrefKey));
#endif
        ShowPort();
        Refresh();
    }

    void OnReceiverStateChanged()
    {
        if (portInput && !portInput.isFocused) ShowPort();
        _nextRefresh = 0f;
    }

    // 入力欄には、実際に待ち受けているポートを出す
    void ShowPort()
    {
        if (!portInput || !receiver) return;
        int p = receiver.IsListening ? receiver.ListeningPort : receiver.port;
        portInput.SetTextWithoutNotify(p.ToString(CultureInfo.InvariantCulture));
    }

    void Update()
    {
        // 入力欄に文字を打っている間はショートカットを無効にする
        if (!(portInput && portInput.isFocused))
        {
            if (pauseKey != KeyCode.None && Input.GetKeyDown(pauseKey)) OnPauseClicked();
            if (zeroKey != KeyCode.None && Input.GetKeyDown(zeroKey)) OnZeroClicked();
        }

        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }
    }

    //=== 操作 ===

    void OnPauseClicked()
    {
        Deselect();
        if (store == null) return;
        store.SetPaused(!store.Paused);
        _nextRefresh = 0f;
    }

    void OnZeroClicked()
    {
        Deselect();
        if (store != null) store.Zero();
    }

    void OnZeroModeClicked()
    {
        Deselect();
        if (store == null) return;
        switch (store.zeroMode)
        {
            case RealtimePoseStore.ZeroMode.YawOnly: store.SetZeroMode(RealtimePoseStore.ZeroMode.Level); break;
            case RealtimePoseStore.ZeroMode.Level:   store.SetZeroMode(RealtimePoseStore.ZeroMode.None); break;
            default:                                 store.SetZeroMode(RealtimePoseStore.ZeroMode.YawOnly); break;
        }
        _nextRefresh = 0f;
    }

    void ToggleSeries(int which)
    {
        Deselect();
        if (graph == null) return;
        if (which == 0) graph.showRoll = !graph.showRoll;
        else if (which == 1) graph.showPitch = !graph.showPitch;
        else graph.showYaw = !graph.showYaw;
        _nextRefresh = 0f;
    }

    void OnAxisPresetClicked()
    {
        Deselect();
        if (store == null) return;
        RealtimePoseStore.AxisPreset next;
        switch (store.CurrentPreset)
        {
            case RealtimePoseStore.AxisPreset.ImuXTowardTail: next = RealtimePoseStore.AxisPreset.ImuXTowardNose; break;
            case RealtimePoseStore.AxisPreset.ImuXTowardNose: next = RealtimePoseStore.AxisPreset.KinematicPreview; break;
            default:                                          next = RealtimePoseStore.AxisPreset.ImuXTowardTail; break;
        }
        store.ApplyPreset(next);
#if !UNITY_EDITOR
        PlayerPrefs.SetInt(AxisPresetPrefKey, (int)next);
        PlayerPrefs.Save();
#endif
        _nextRefresh = 0f;
    }

    void OnPortApplyClicked()
    {
        Deselect();
        if (receiver == null || portInput == null) return;
        if (!int.TryParse(portInput.text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
        {
            ShowPort();
            return;
        }
        receiver.ChangePort(p);
        _nextRefresh = 0f;
    }

    // クリックしたボタンが選択されたままだと、Enter / Space で再度押されてしまうので外す
    static void Deselect()
    {
        var es = EventSystem.current;
        if (es != null && !es.alreadySelecting) es.SetSelectedGameObject(null);
    }

    //=== 表示 ===

    void Refresh()
    {
        if (pauseButtonText && store) pauseButtonText.text = store.Paused ? labelResume : labelPause;
        if (zeroModeButtonText && store) zeroModeButtonText.text = ZeroModeLabel(store.zeroMode);
        if (axisPresetButtonText && store) axisPresetButtonText.text = AxisPresetLabel(store.CurrentPreset);

        if (statusLabel) { BuildStatus(_sb); statusLabel.SetText(_sb); }
        if (infoLabel)   { BuildInfo(_sb);   infoLabel.SetText(_sb); }
        if (detailLabel) { BuildDetail(_sb); detailLabel.SetText(_sb); }

        bool has = store != null && store.HasPose;
        Vector3 rpy = has ? store.DisplayRpy : Vector3.zero;
        SetAngle(rollLabel,  "Roll ",  has, rpy.x, graph == null || graph.showRoll);
        SetAngle(pitchLabel, "Pitch ", has, rpy.y, graph == null || graph.showPitch);
        SetAngle(yawLabel,   "Yaw ",   has, rpy.z, graph == null || graph.showYaw);
    }

    void SetAngle(TMP_Text label, string name, bool has, float value, bool shown)
    {
        if (!label) return;
        _sb.Clear();
        _sb.Append(name);
        if (has) _sb.Append(value.ToString("0.0", CultureInfo.InvariantCulture));
        else _sb.Append("--");
        label.SetText(_sb);
        label.alpha = shown ? 1f : 0.35f;       // グラフで非表示にしている線のラベルは薄くする
    }

    static string ZeroModeLabel(RealtimePoseStore.ZeroMode m)
    {
        switch (m)
        {
            case RealtimePoseStore.ZeroMode.YawOnly:  return "Zero: Yaw";
            case RealtimePoseStore.ZeroMode.Level:    return "Zero: Level";
            case RealtimePoseStore.ZeroMode.Relative: return "Zero: Pose";
            default:                                  return "Zero: Off";
        }
    }

    static string AxisPresetLabel(RealtimePoseStore.AxisPreset p)
    {
        switch (p)
        {
            case RealtimePoseStore.AxisPreset.ImuXTowardTail:   return "IMU X: tail";
            case RealtimePoseStore.AxisPreset.ImuXTowardNose:   return "IMU X: nose";
            case RealtimePoseStore.AxisPreset.KinematicPreview: return "Legacy map";
            default:                                            return "Custom map";
        }
    }

    void BuildStatus(StringBuilder sb)
    {
        sb.Clear();
        if (receiver == null || store == null) { Tag(sb, ColError, "NOT CONFIGURED"); return; }

        if (!receiver.IsListening)
        {
            if (!string.IsNullOrEmpty(receiver.LastError)) { Tag(sb, ColError, "ERROR"); sb.Append("  ").Append(receiver.LastError); }
            else Tag(sb, ColIdle, "STOPPED");
            return;
        }

        if (store.Paused)
        {
            Tag(sb, ColIdle, "PAUSED");
            sb.Append("  UDP port ").Append(receiver.ListeningPort);
        }
        else if (!store.HasPose)
        {
            if (receiver.DatagramCount == 0)
            {
                Tag(sb, ColWait, "WAITING");
                sb.Append("  UDP port ").Append(receiver.ListeningPort);
                if (_localAddresses.Length > 0) sb.Append("   this PC: ").Append(_localAddresses);
            }
            else
            {
                // 何か届いているが姿勢として読めていない → 理由を出して、送信側を直してもらう
                Tag(sb, ColError, "NO ATTITUDE");
                sb.Append("  from ").Append(receiver.LastSender).Append(": ");
                if (!AppendProblem(sb)) sb.Append("no roll / pitch / yaw in the received data");
            }
        }
        else if (store.IsStale)
        {
            Tag(sb, ColError, "NO DATA");
            sb.Append("  ").Append(store.SecondsSinceLastSample.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" s   last from ").Append(receiver.LastSender);
            // 届いてはいるが使えていない場合は、その理由も出す
            int mark = sb.Length;
            sb.Append("   ");
            if (!AppendProblem(sb)) sb.Length = mark;
        }
        else
        {
            Tag(sb, ColLive, "LIVE");
            sb.Append("  ").Append(store.RateHz.ToString("0.0", CultureInfo.InvariantCulture)).Append(" Hz  from ")
              .Append(receiver.LastSender).Append("  [").Append(FormatName(store.LastSampleFormat)).Append(']');
        }
    }

    // 直近に届いたデータを姿勢として使えなかった理由を追記する。該当がなければ false。
    bool AppendProblem(StringBuilder sb)
    {
        double now = UdpTelemetryReceiver.Now;
        if (now - store.LastRejectedTime < ProblemWindowSeconds)
        {
            Vector3 v = store.LastRejectedRpy;
            sb.Append("values out of range (roll ").Append(v.x.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(", pitch ").Append(v.y.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(", yaw ").Append(v.z.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(") - check the sender's field order");
            return true;
        }
        string issue = receiver.Parser.LastIssue;
        if (now - receiver.LastUnparsedTime < ProblemWindowSeconds && !string.IsNullOrEmpty(issue))
        {
            // 送信側由来の文字が混ざるので、リッチテキストのタグとして解釈されないようにする
            int start = sb.Length;
            sb.Append(issue);
            sb.Replace('<', '(', start, issue.Length);
            return true;
        }
        return false;
    }

    static void Tag(StringBuilder sb, string color, string text)
    {
        sb.Append("<b><color=").Append(color).Append('>').Append(text).Append("</color></b>");
    }

    static string FormatName(TelemetryWireFormat f)
    {
        switch (f)
        {
            case TelemetryWireFormat.Dat:      return "DAT";
            case TelemetryWireFormat.Csv:      return "CSV";
            case TelemetryWireFormat.KeyValue: return "KEY:VALUE";
            case TelemetryWireFormat.Json:     return "JSON";
            case TelemetryWireFormat.Osc:      return "OSC";
            default:                           return "?";
        }
    }

    void BuildInfo(StringBuilder sb)
    {
        sb.Clear();
        if (receiver == null || store == null) return;
        sb.Append("samples ").Append(store.SampleCount)
          .Append("   lost ").Append(store.LostCount)
          .Append("   unreadable ").Append(receiver.UnparsedCount);
        if (store.RejectedCount > 0) sb.Append("   out-of-range ").Append(store.RejectedCount);
        if (store.ShadowedCount > 0) sb.Append("   osc/csv skipped ").Append(store.ShadowedCount);
        if (receiver.DroppedCount > 0) sb.Append("   dropped ").Append(receiver.DroppedCount);
    }

    void BuildDetail(StringBuilder sb)
    {
        sb.Clear();
        if (store == null) return;

        // まだ姿勢データが来ていない間は、送り方の案内を出す
        if (!store.HasPose)
        {
            if (receiver == null) return;
            if (!receiver.IsListening)
            {
                sb.Append("Not listening. Enter another UDP port (top right) and press Set,\n")
                  .Append("or close the application that is using this port.");
                return;
            }
            sb.Append("Send telemetry to this PC on UDP port ").Append(receiver.ListeningPort)
              .Append(", one message per datagram. Accepted formats:\n")
              .Append("  DAT,<seq>,<t_ms>,<values...>   (the glider's HDR / DAT / LOG lines, as-is)\n")
              .Append("  {\"roll\": 1.0, \"pitch\": 2.0, \"yaw\": 3.0}   (JSON)\n")
              .Append("  /plane/data  roll pitch yaw ...   (OSC)\n")
              .Append("Try it without hardware:  python Tools/udp_test_sender.py");
            return;
        }

        if (store.TryGetLog("mode", out string mode)) sb.Append("mode   ").Append(mode).Append('\n');

        // 直近の出来事（mode の切り替えは上の行に出ているので省く）
        if (!string.IsNullOrEmpty(store.LastEventLog) &&
            !store.LastEventLog.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
        {
            double age = UdpTelemetryReceiver.Now - store.LastEventLogTime;
            sb.Append("log    ").Append(store.LastEventLog)
              .Append("  (").Append(age.ToString("0", CultureInfo.InvariantCulture)).Append(" s ago)\n");
        }

        if (store.TryGetLog("Param", out string param)) sb.Append("param  ").Append(param).Replace(',', ' ', sb.Length - param.Length, param.Length).Append('\n');

        bool anyExtra = false;
        if (extraFields != null)
        {
            for (int i = 0; i < extraFields.Length; i++)
            {
                if (!store.TryGetValue(extraFields[i], out float v)) continue;
                sb.Append(anyExtra ? "  " : "servo  ");
                sb.Append(extraFields[i]).Append(' ').Append(v.ToString("0.0", CultureInfo.InvariantCulture));
                anyExtra = true;
            }
            if (anyExtra) sb.Append('\n');
        }

        if (store.HasPose)
        {
            Vector3 r = store.ReceivedRpy;
            sb.Append("recv   roll ").Append(r.x.ToString("0.0", CultureInfo.InvariantCulture))
              .Append("  pitch ").Append(r.y.ToString("0.0", CultureInfo.InvariantCulture))
              .Append("  yaw ").Append(r.z.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    // 送信側に宛先として教える、この PC の IPv4 アドレス
    static string LocalIPv4Addresses()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return "";
#else
        try
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    byte[] b = a.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;     // ループバック / リンクローカル
                    if (count == 4) { sb.Append(", ..."); return sb.ToString(); }
                    if (count > 0) sb.Append(", ");
                    sb.Append(a);
                    count++;
                }
            }
            return sb.ToString();
        }
        catch (Exception)
        {
            return "";
        }
#endif
    }
}
