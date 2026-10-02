// Assets/Scripts/RealtimePreview/UdpTelemetryReceiver.cs
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// UDP でテレメトリを受信し、メインスレッド（Update）で解析して MessageReceived を発火する。
/// 送信側（Python など）は、このポートへ 1メッセージ=1データグラム で送るだけでよい。
/// 対応形式は TelemetryParser を参照（DAT/HDR/LOG 行・数値CSV・key:value・JSON・OSC を自動判別）。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]   // 同じフレーム内で、表示系より先に受信データを反映する
public class UdpTelemetryReceiver : MonoBehaviour
{
    [Header("UDP")]
    [Tooltip("待ち受ける UDP ポート番号（送信側の宛先ポートと合わせる）")]
    public int port = 9000;
    public bool listenOnEnable = true;
    [Tooltip("同じポートの IPv6 でも待ち受ける（送信側が localhost を ::1 に解決する場合への備え。使えない環境では IPv4 のみ）")]
    public bool alsoListenIPv6 = true;
    [Tooltip("ビルド版では、実行中に変更したポート番号を保存して次回起動時も使う（Editor では Inspector の値を使う）")]
    public bool rememberPortInBuild = true;

    [Header("Parsing")]
    [Tooltip("HDR を受信する前の DAT 行に使うフィールド名")]
    public string defaultDatFields = TelemetryParser.DefaultDatFieldsCsv;
    [Tooltip("数値だけのCSV / JSON配列 / OSC引数に、位置で対応付けるフィールド名")]
    public string positionalFields = TelemetryParser.DefaultPositionalFieldsCsv;
    [Tooltip("このアドレスの OSC メッセージだけを受け付ける（空にすると全アドレスを受け付ける）")]
    public string oscAddressFilter = "/plane/data";

    [Header("Debug")]
    [Tooltip("解釈できなかったデータグラムを Console に出す（毎秒1件まで）")]
    public bool logUnparsed = false;

    // 公開状態（メインスレッドから参照）
    public bool IsListening { get; private set; }
    public int ListeningPort { get; private set; }
    public bool IsIPv6Enabled { get; private set; }
    public string LastError { get; private set; }
    public long DatagramCount { get; private set; }
    public long MessageCount { get; private set; }
    public long UnparsedCount { get; private set; }
    /// <summary>最後に「読めないデータ」が届いた時刻（理由は Parser.LastIssue）</summary>
    public double LastUnparsedTime { get; private set; } = double.NegativeInfinity;
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public string LastSender { get; private set; } = "";
    public TelemetryWireFormat LastFormat { get; private set; } = TelemetryWireFormat.Unknown;
    public double LastDatagramTime { get; private set; } = double.NegativeInfinity;
    public TelemetryParser Parser => _parser;

    /// <summary>受信時刻と同じ時計（秒）。フレーム時間に依存しない。</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>解析できたメッセージごとにメインスレッドで呼ばれる。第2引数は受信時刻（Now と同じ時計）。
    /// TelemetryMessage は使い回されるので、ハンドラの外へ参照を持ち出さないこと。</summary>
    public event Action<TelemetryMessage, double> MessageReceived;

    /// <summary>待ち受け開始/停止/エラーなど、状態が変わったとき。</summary>
    public event Action StateChanged;

    const string PortPrefKey = "RealtimePreview.udpPort";
    const int MaxQueued = 2048;          // メインスレッドが止まっている間に溜め込みすぎない
    const int MaxPerFrame = 512;

    struct Datagram
    {
        public byte[] data;
        public double time;
        public EndPoint remote;
    }

    readonly ConcurrentQueue<Datagram> _queue = new ConcurrentQueue<Datagram>();
    readonly TelemetryParser _parser = new TelemetryParser();
    Action<TelemetryMessage> _onParsed;
    int _queued;
    long _dropped;
    double _currentArrival;
    EndPoint _lastRemote;
    double _lastUnparsedLog = double.NegativeInfinity;

#if !(UNITY_WEBGL && !UNITY_EDITOR)
    Socket _socket4, _socket6;
    Thread _thread4, _thread6;
    volatile bool _running;
    volatile string _threadError;
#endif

    void Awake()
    {
        _onParsed = OnParsed;
        ApplyParserSettings();
    }

    void OnEnable()
    {
        if (_onParsed == null) _onParsed = OnParsed;
#if !UNITY_EDITOR
        if (rememberPortInBuild) port = PlayerPrefs.GetInt(PortPrefKey, port);
#endif
        if (listenOnEnable) StartListening(port);
    }

    void OnDisable() { StopListening(); }
    void OnDestroy() { StopListening(); }
    void OnApplicationQuit() { StopListening(); }

    void OnValidate()
    {
        port = Mathf.Clamp(port, 0, 65535);
        ApplyParserSettings();
    }

    void ApplyParserSettings()
    {
        _parser.SetDefaultDatFields(defaultDatFields);
        _parser.SetPositionalFields(positionalFields);
        _parser.OscAddressFilter = oscAddressFilter != null ? oscAddressFilter.Trim() : "";
    }

    //=== 待ち受け制御 ===

    public bool StartListening() { return StartListening(port); }

    /// <summary>指定ポートで待ち受けを（やり直して）開始する。失敗時は false で、理由は LastError。</summary>
    public bool StartListening(int newPort)
    {
        StopListening();
        port = newPort;
        LastError = null;

#if UNITY_WEBGL && !UNITY_EDITOR
        LastError = "UDP is not available in WebGL builds";
        Debug.LogWarning($"[UdpTelemetryReceiver] {LastError}");
        StateChanged?.Invoke();
        return false;
#else
        if (newPort < 0 || newPort > 65535)
        {
            LastError = $"invalid port {newPort}";
            StateChanged?.Invoke();
            return false;
        }

        // IPv4 は必須。他のアプリが同じポートを使っていたら、ここで失敗して理由を表示する。
        try
        {
            _socket4 = Bind(AddressFamily.InterNetwork, IPAddress.Any, newPort);
            ListeningPort = ((IPEndPoint)_socket4.LocalEndPoint).Port;
        }
        catch (Exception ex)
        {
            // UI に出す文言は英数字だけにする（OS のエラーメッセージは日本語のことがあり、UI のフォントで表示できない）
            var se = ex as SocketException;
            if (se != null && (se.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                               se.SocketErrorCode == SocketError.AccessDenied))
                LastError = $"port {newPort} is already in use by another application";
            else
                LastError = $"cannot open port {newPort} ({(se != null ? se.SocketErrorCode.ToString() : ex.GetType().Name)})";
            Debug.LogError($"[UdpTelemetryReceiver] {LastError}: {ex.Message}");
            CloseSockets();
            StateChanged?.Invoke();
            return false;
        }

        // IPv6 は任意（無効な環境や使用中なら IPv4 のみで続行）
        IsIPv6Enabled = false;
        if (alsoListenIPv6)
        {
            try
            {
                _socket6 = Bind(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, ListeningPort);
                IsIPv6Enabled = true;
            }
            catch (Exception) { _socket6 = null; }
        }

        _threadError = null;
        _running = true;
        _thread4 = StartThread(_socket4, "UdpTelemetryReceiver(IPv4)");
        if (_socket6 != null) _thread6 = StartThread(_socket6, "UdpTelemetryReceiver(IPv6)");

        IsListening = true;
        Debug.Log($"[UdpTelemetryReceiver] listening on UDP port {ListeningPort} ({(IsIPv6Enabled ? "IPv4+IPv6" : "IPv4")})");
        StateChanged?.Invoke();
        return true;
#endif
    }

    /// <summary>UI からのポート変更用。成功したらビルド版では保存する。</summary>
    public bool ChangePort(int newPort)
    {
        if (newPort < 1 || newPort > 65535)
        {
            LastError = $"invalid port {newPort} (1-65535)";
            StateChanged?.Invoke();
            return false;
        }
        bool ok = StartListening(newPort);
#if !UNITY_EDITOR
        if (ok && rememberPortInBuild)
        {
            PlayerPrefs.SetInt(PortPrefKey, newPort);
            PlayerPrefs.Save();
        }
#endif
        return ok;
    }

    public void StopListening()
    {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        _running = false;
        // 受信スレッドは Poll のタイムアウトごとに _running を見て自分で抜ける。抜けるのを待ってから閉じる。
        JoinThread(ref _thread4);
        JoinThread(ref _thread6);
        CloseSockets();
#endif
        while (_queue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _queued, 0);

        if (IsListening)
        {
            IsListening = false;
            StateChanged?.Invoke();
        }
    }

#if !(UNITY_WEBGL && !UNITY_EDITOR)
    Thread StartThread(Socket socket, string name)
    {
        var t = new Thread(ReceiveLoop) { IsBackground = true, Name = name };
        t.Start(socket);
        return t;
    }

    static void JoinThread(ref Thread thread)
    {
        var t = thread;
        thread = null;
        if (t != null && t.IsAlive && !t.Join(500))
            Debug.LogWarning("[UdpTelemetryReceiver] receive thread did not stop in time");
    }

    void CloseSockets()
    {
        CloseSocket(ref _socket4);
        CloseSocket(ref _socket6);
    }

    static void CloseSocket(ref Socket socket)
    {
        var s = socket;
        socket = null;
        if (s == null) return;
        try { s.Close(); } catch (Exception) { }
    }

    static Socket Bind(AddressFamily family, IPAddress address, int bindPort)
    {
        var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // IPv4 用とは別のソケットにする（両対応ソケットだと、IPv4 側の使用中を検出できない）
            if (family == AddressFamily.InterNetworkV6)
                s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // 後から来たアプリに同じポートを横取りされないようにする
            try { s.ExclusiveAddressUse = true; } catch (Exception) { }
#endif
            // まとめて届いたとき（送信側のバースト）に OS 側で捨てられにくくする
            try { s.ReceiveBufferSize = 1 << 20; } catch (Exception) { }
            s.Bind(new IPEndPoint(address, bindPort));
            s.Blocking = false;         // 受信待ちは Poll のタイムアウトだけで行う（停止要求を必ず見られるように）
            return s;
        }
        catch (Exception)
        {
            try { s.Close(); } catch (Exception) { }
            throw;
        }
    }

    // 受信スレッド: Unity API には触れない
    void ReceiveLoop(object state)
    {
        var socket = (Socket)state;
        var buffer = new byte[65536];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                if (!socket.Poll(100000, SelectMode.SelectRead)) continue;   // 100ms ごとに停止要求を確認
                EndPoint remote = any;
                int n = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
                if (n <= 0) continue;

                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                _queue.Enqueue(new Datagram { data = copy, time = Now, remote = remote });
                if (Interlocked.Increment(ref _queued) > MaxQueued && _queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _queued);
                    Interlocked.Increment(ref _dropped);
                }
            }
            catch (SocketException ex)
            {
                if (!_running) break;
                switch (ex.SocketErrorCode)
                {
                    // 受信専用ソケットでは致命的でないもの（Windows の ICMP 到達不能通知など）
                    case SocketError.ConnectionReset:
                    case SocketError.MessageSize:
                    case SocketError.TimedOut:
                    case SocketError.WouldBlock:
                    case SocketError.Interrupted:
                        continue;
                }
                // IPv6 側は補助なので、失敗しても IPv4 の待ち受けは続ける
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                    _threadError = $"socket error ({ex.SocketErrorCode})";
                break;
            }
            catch (ObjectDisposedException) { break; }
            catch (ThreadAbortException) { break; }     // Editor のドメインリロード
            catch (Exception ex)
            {
                if (_running && socket.AddressFamily == AddressFamily.InterNetwork) _threadError = $"receive failed ({ex.GetType().Name})";
                break;
            }
        }
    }
#endif

    //=== メインスレッド ===

    void Update()
    {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        string err = _threadError;
        if (err != null)
        {
            _threadError = null;
            StopListening();
            LastError = err;
            Debug.LogError($"[UdpTelemetryReceiver] {err}");
            StateChanged?.Invoke();
        }
#endif
        int budget = MaxPerFrame;
        while (budget-- > 0 && _queue.TryDequeue(out var d))
        {
            Interlocked.Decrement(ref _queued);
            DatagramCount++;
            LastDatagramTime = d.time;
            if (!Equals(d.remote, _lastRemote))
            {
                _lastRemote = d.remote;
                LastSender = FormatEndPoint(d.remote);
            }

            _currentArrival = d.time;
            int n = 0;
            try { n = _parser.Parse(d.data, 0, d.data.Length, _onParsed); }
            catch (Exception ex) { Debug.LogException(ex, this); }

            if (n == 0)
            {
                UnparsedCount++;
                LastUnparsedTime = d.time;
                if (logUnparsed && d.time - _lastUnparsedLog >= 1.0)
                {
                    _lastUnparsedLog = d.time;
                    Debug.LogWarning($"[UdpTelemetryReceiver] unparsed datagram from {LastSender} ({d.data.Length} bytes): {Preview(d.data)}");
                }
            }
        }
    }

    void OnParsed(TelemetryMessage m)
    {
        MessageCount++;
        LastFormat = m.format;
        var handler = MessageReceived;
        if (handler == null) return;
        try { handler(m, _currentArrival); }
        catch (Exception ex) { Debug.LogException(ex, this); }
    }

    static string FormatEndPoint(EndPoint ep)
    {
        var ip = ep as IPEndPoint;
        if (ip == null) return ep != null ? ep.ToString() : "";
        var addr = ip.Address;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        return addr.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{addr}]:{ip.Port}"
            : $"{addr}:{ip.Port}";
    }

    static string Preview(byte[] data)
    {
        int n = Mathf.Min(data.Length, 80);
        var sb = new System.Text.StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            char c = (char)data[i];
            sb.Append(c >= ' ' && c < 127 ? c : '.');
        }
        return sb.ToString();
    }
}
