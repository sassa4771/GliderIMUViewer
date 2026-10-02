// Assets/Scripts/RealtimePreview/TelemetryParser.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public enum TelemetryMessageKind { Sample, Header, Log }

public enum TelemetryWireFormat { Unknown, Dat, Csv, KeyValue, Json, Osc }

/// <summary>
/// 解析結果1件分。TelemetryParser が使い回すので、ハンドラの外へ参照を持ち出さないこと。
/// </summary>
public sealed class TelemetryMessage
{
    public TelemetryMessageKind kind;
    public TelemetryWireFormat format;

    public bool hasSeq;
    public long seq;                 // DAT の seq / JSON 等の "seq" "src_seq"
    public bool hasDeviceTimeMs;
    public double deviceTimeMs;      // DAT の t_ms / JSON 等の "t_ms"

    /// <summary>名前付きの数値（大文字小文字は区別しない）</summary>
    public readonly Dictionary<string, float> values =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public string text;              // kind==Log のときの本文
    public string[] fields;          // kind==Header のときのフィールド名
    public string oscAddress;        // format==Osc のときのアドレス

    public bool TryGet(string name, out float v)
    {
        if (string.IsNullOrEmpty(name)) { v = 0f; return false; }
        return values.TryGetValue(name, out v);
    }

    internal void Reset(TelemetryMessageKind k, TelemetryWireFormat f)
    {
        kind = k; format = f;
        hasSeq = false; seq = 0;
        hasDeviceTimeMs = false; deviceTimeMs = 0;
        values.Clear();
        text = null; fields = null; oscAddress = null;
    }
}

/// <summary>
/// UDP 1データグラムをテレメトリとして解釈する（UnityEngine 非依存）。
/// 先頭バイトで形式を自動判別する:
///   "DAT,seq,t_ms,v0,v1,..." / "HDR,...,fields=a,b,c" / "LOG,text"  … 機体ファームのテキスト行
///   "1.0,2.0,3.0"                                                   … 数値だけのCSV（位置で対応付け）
///   "roll:1.0,pitch:2.0" / "roll=1.0 pitch=2.0"                      … key:value
///   {"roll":1.0,"pitch":2.0,"yaw":3.0}                              … JSON
///   /plane/data ,fff ...                                            … OSC メッセージ / バンドル
/// </summary>
public sealed class TelemetryParser
{
    public const string DefaultDatFieldsCsv = "dt_ms,ax,ay,az,gx,gy,gz,roll,pitch,yaw,s0,s1,s2";
    public const string DefaultPositionalFieldsCsv = "roll,pitch,yaw,sv1,sv3";

    const int MaxJsonDepth = 16;
    const int MaxOscDepth = 4;
    // 名前のない（位置で対応付ける）形式は、roll/pitch/yaw の3つ以上そろっているものだけ受け付ける。
    // 同じポートに流れてくる無関係な単発の数値（他アプリの OSC など）を姿勢と取り違えないため。
    const int MinPositionalValues = 3;

    string[] _defaultDatFields = SplitNames(DefaultDatFieldsCsv);
    string[] _datFields;                       // HDR で受け取ったもの（未受信なら null）
    string[] _positional = SplitNames(DefaultPositionalFieldsCsv);
    readonly List<string> _tmpNames = new List<string>(32);
    readonly TelemetryMessage _msg = new TelemetryMessage();

    // OSC アドレス文字列のキャッシュ（毎回 string を作らない）
    byte[] _oscAddrBytes = new byte[0];
    string _oscAddrString = "";
    string _oscIssueAddress, _oscIssueText;

    /// <summary>空でなければ、このアドレスの OSC メッセージだけを受け付ける。</summary>
    public string OscAddressFilter = "";

    /// <summary>直近に読めなかった（無視した）データの理由。送信側のデバッグ用に UI へ出す。</summary>
    public string LastIssue { get; private set; }

    /// <summary>HDR を一度でも受信したか。</summary>
    public bool HeaderSeen => _datFields != null;

    /// <summary>DAT 行の値に対応付けるフィールド名（HDR 受信前は既定値）。</summary>
    public string[] DatFields => _datFields ?? _defaultDatFields;

    /// <summary>数値CSV / JSON配列 / OSC 引数に位置で対応付けるフィールド名。</summary>
    public string[] PositionalFields => _positional;

    public void SetDefaultDatFields(string csv)
    {
        var a = SplitNames(csv);
        if (a.Length > 0) _defaultDatFields = a;
    }

    public void SetPositionalFields(string csv)
    {
        var a = SplitNames(csv);
        if (a.Length > 0) _positional = a;
    }

    /// <summary>HDR で覚えたフィールド名を忘れる（送信元が変わったとき用）。</summary>
    public void ForgetHeader() { _datFields = null; }

    public static string[] SplitNames(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return new string[0];
        var parts = csv.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        return parts;
    }

    //====================================================================
    // 入口
    //====================================================================

    /// <summary>1データグラムを解析し、得られたメッセージごとに onMessage を呼ぶ。戻り値は件数。</summary>
    public int Parse(byte[] data, int offset, int count, Action<TelemetryMessage> onMessage)
    {
        if (data == null || count <= 0 || onMessage == null) return 0;
        int start = offset, end = offset + count;

        // UTF-8 BOM と先頭の空白を飛ばす
        if (end - start >= 3 && data[start] == 0xEF && data[start + 1] == 0xBB && data[start + 2] == 0xBF) start += 3;
        while (start < end && IsWhite((char)data[start])) start++;
        // 末尾の NUL / 空白（C 文字列をそのまま送る実装への配慮）は OSC 判定の後で落とす
        if (start >= end) return 0;

        byte b0 = data[start];
        if (b0 == (byte)'/' || b0 == (byte)'#')
        {
            if (TryParseOsc(data, start, end - start, onMessage, 0, out int n)) return n;
        }

        while (end > start && (data[end - 1] == 0 || IsWhite((char)data[end - 1]))) end--;
        if (start >= end) return 0;

        string text;
        try { text = Encoding.UTF8.GetString(data, start, end - start); }
        catch (Exception) { return 0; }
        return ParseText(text, onMessage);
    }

    /// <summary>テキスト（JSON または 1行以上のテキスト行）を解析する。</summary>
    public int ParseText(string text, Action<TelemetryMessage> onMessage)
    {
        if (string.IsNullOrEmpty(text) || onMessage == null) return 0;

        int p = 0;
        while (p < text.Length && (IsWhite(text[p]) || text[p] == (char)0xFEFF)) p++;
        if (p >= text.Length) return 0;

        if (text[p] == '{' || text[p] == '[')
        {
            if (TryParseJson(text, p, onMessage, out int n)) return n;
        }

        int emitted = 0;
        int ls = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n' || text[i] == '\r')
            {
                if (i > ls) emitted += ParseLine(text, ls, i, onMessage);
                ls = i + 1;
            }
        }
        return emitted;
    }

    //====================================================================
    // テキスト行
    //====================================================================

    int ParseLine(string s, int ls, int le, Action<TelemetryMessage> on)
    {
        Trim(s, ref ls, ref le);
        if (le <= ls) return 0;

        if (StartsWith(s, ls, le, "DAT,")) return ParseDat(s, ls + 4, le, on);
        if (StartsWith(s, ls, le, "HDR,")) return ParseHdr(s, ls + 4, le, on);
        if (StartsWith(s, ls, le, "LOG,"))
        {
            int a = ls + 4, b = le;
            Trim(s, ref a, ref b);
            _msg.Reset(TelemetryMessageKind.Log, TelemetryWireFormat.Dat);
            _msg.text = s.Substring(a, b - a);
            on(_msg);
            return 1;
        }
        // 行内に埋め込まれた JSON（1データグラムに複数行の JSON など）
        if (s[ls] == '{')
        {
            if (TryParseJson(s.Substring(ls, le - ls), 0, on, out int n)) return n;
            return 0;
        }
        return ParseBare(s, ls, le, on);
    }

    // DAT,<seq>,<t_ms>,<v0>,<v1>,...
    int ParseDat(string s, int p, int le, Action<TelemetryMessage> on)
    {
        if (!NextToken(s, ref p, le, out int ts, out int te)) return 0;
        bool hasSeq = TryParseDouble(s, ts, te, out double seq);
        if (!NextToken(s, ref p, le, out ts, out te)) return 0;
        bool hasT = TryParseDouble(s, ts, te, out double tms);

        var names = DatFields;
        _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.Dat);
        int i = 0;
        while (NextToken(s, ref p, le, out ts, out te))
        {
            if (TryParseDouble(s, ts, te, out double v))
                Put(_msg, i < names.Length ? names[i] : ExtraName(i), v);
            i++;
        }
        if (_msg.values.Count == 0) { LastIssue = "DAT line without numeric values"; return 0; }

        // 行頭の seq / t_ms を優先（フィールド名に同名があっても上書きする）
        if (hasSeq && IsFinite(seq)) { _msg.hasSeq = true; _msg.seq = (long)seq; }
        if (hasT && IsFinite(tms)) { _msg.hasDeviceTimeMs = true; _msg.deviceTimeMs = tms; }
        on(_msg);
        return 1;
    }

    // HDR,1,GLDR,fields=dt_ms,ax,...,s2[,rate=30]
    int ParseHdr(string s, int p, int le, Action<TelemetryMessage> on)
    {
        int idx = s.IndexOf("fields=", p, le - p, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) { LastIssue = "HDR line without fields="; return 0; }
        p = idx + 7;

        _tmpNames.Clear();
        while (NextToken(s, ref p, le, out int ts, out int te))
        {
            if (te <= ts) continue;
            if (s.IndexOf('=', ts, te - ts) >= 0) break;   // rate=30 などのパラメータで打ち切り
            _tmpNames.Add(s.Substring(ts, te - ts));
        }
        if (_tmpNames.Count == 0) return 0;

        // 内容が変わったときだけ配列を作り直す（HDR は数秒ごとに再送される）
        bool same = _datFields != null && _datFields.Length == _tmpNames.Count;
        for (int i = 0; same && i < _tmpNames.Count; i++)
            same = string.Equals(_datFields[i], _tmpNames[i], StringComparison.Ordinal);
        if (!same) _datFields = _tmpNames.ToArray();

        _msg.Reset(TelemetryMessageKind.Header, TelemetryWireFormat.Dat);
        _msg.fields = _datFields;
        on(_msg);
        return 1;
    }

    // "1.0,2.0,3.0" または "roll:1.0,pitch:2.0" / "roll=1.0 pitch=2.0"
    int ParseBare(string s, int ls, int le, Action<TelemetryMessage> on)
    {
        // Python の str((roll, pitch, yaw)) のように括弧で囲まれていてもよい
        if (le - ls >= 2 && s[ls] == '(' && s[le - 1] == ')') { ls++; le--; }

        int p = ls;
        if (!NextTokenAny(s, ref p, le, out int ts, out int te)) return 0;

        bool firstIsNumber = TryParseDouble(s, ts, te, out double first);
        if (firstIsNumber || IsNonFiniteToken(s, ts, te))
        {
            // 数値だけの行: 位置でフィールドに対応付け。nan / inf は「その位置の値なし」、それ以外の文字が混ざる行は捨てる。
            _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.Csv);
            if (firstIsNumber) Put(_msg, PositionalName(0), first);
            int i = 1;
            while (NextTokenAny(s, ref p, le, out ts, out te))
            {
                if (TryParseDouble(s, ts, te, out double v)) Put(_msg, PositionalName(i), v);
                else if (!IsNonFiniteToken(s, ts, te))
                {
                    LastIssue = "text is not DAT / HDR / LOG, numbers, key:value or JSON";
                    return 0;
                }
                i++;
            }
            if (i < MinPositionalValues || _msg.values.Count == 0)
            {
                LastIssue = "fewer than 3 numbers in an unnamed (positional) message";
                return 0;
            }
            on(_msg);
            return 1;
        }

        // key:value / key=value（"roll: 1.0" のように値が次のトークンに分かれていてもよい）
        _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.KeyValue);
        string pendingKey = null;
        p = ls;
        while (NextTokenAny(s, ref p, le, out ts, out te))
        {
            if (pendingKey != null)
            {
                if (TryParseDouble(s, ts, te, out double pv)) Put(_msg, pendingKey, pv);
                pendingKey = null;
                continue;
            }
            int sep = -1;
            for (int i = ts; i < te; i++) { if (s[i] == ':' || s[i] == '=') { sep = i; break; } }
            if (sep <= ts) continue;                       // 区切りなし、またはキーが空
            int ks = ts, ke = sep; Trim(s, ref ks, ref ke);
            if (ke <= ks) continue;
            string key = s.Substring(ks, ke - ks);
            if (sep + 1 >= te) { pendingKey = key; continue; }
            if (TryParseDouble(s, sep + 1, te, out double v)) Put(_msg, key, v);
        }
        if (_msg.values.Count == 0)
        {
            LastIssue = "text is not DAT / HDR / LOG, numbers, key:value or JSON";
            return 0;
        }
        on(_msg);
        return 1;
    }

    //====================================================================
    // JSON（必要最小限の寛容なリーダー）
    //====================================================================

    bool TryParseJson(string s, int start, Action<TelemetryMessage> on, out int emitted)
    {
        emitted = 0;
        int p = start;
        // 連結された複数ドキュメント（改行区切り JSON）も受け付ける
        while (true)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) break;
            char c = s[p];
            if (c == '{')
            {
                if (!ParseJsonObjectMessage(s, ref p, on, ref emitted)) return emitted > 0;
            }
            else if (c == '[')
            {
                if (!ParseJsonTopArray(s, ref p, on, ref emitted)) return emitted > 0;
            }
            else return emitted > 0;
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ',') p++;          // "{...},{...}" も許容
        }
        return emitted > 0;
    }

    // トップレベル配列: 数値の配列（位置対応）またはオブジェクトの配列（バッチ）
    bool ParseJsonTopArray(string s, ref int p, Action<TelemetryMessage> on, ref int emitted)
    {
        p++; // '['
        SkipWs(s, ref p);
        if (p >= s.Length) return false;
        if (s[p] == ']') { p++; return true; }

        if (s[p] == '{')
        {
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '{') return false;
                if (!ParseJsonObjectMessage(s, ref p, on, ref emitted)) return false;
                SkipWs(s, ref p);
                if (p >= s.Length) return false;
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return true; }
                return false;
            }
        }

        _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.Json);
        int i = 0;
        while (true)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (!ParseJsonScalar(s, ref p, out bool isNum, out double v, out _)) return false;
            if (isNum) Put(_msg, PositionalName(i), v);
            i++;
            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (s[p] == ',') { p++; continue; }
            if (s[p] == ']') { p++; break; }
            return false;
        }
        if (i >= MinPositionalValues && _msg.values.Count > 0) { on(_msg); emitted++; }
        else LastIssue = "fewer than 3 numbers in an unnamed (positional) message";
        return true;
    }

    string _jsonLog, _jsonType, _jsonText;

    bool ParseJsonObjectMessage(string s, ref int p, Action<TelemetryMessage> on, ref int emitted)
    {
        _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.Json);
        _jsonLog = null; _jsonType = null; _jsonText = null;
        if (!ParseJsonObject(s, ref p, "", 0)) return false;

        string log = _jsonLog;
        if (log == null && _jsonText != null && _jsonType != null &&
            string.Equals(_jsonType, "log", StringComparison.OrdinalIgnoreCase)) log = _jsonText;

        if (_msg.values.Count > 0) { on(_msg); emitted++; }
        else if (log == null) LastIssue = "JSON object without numeric values";
        if (log != null)
        {
            _msg.Reset(TelemetryMessageKind.Log, TelemetryWireFormat.Json);
            _msg.text = log;
            on(_msg);
            emitted++;
        }
        return true;
    }

    bool ParseJsonObject(string s, ref int p, string prefix, int depth)
    {
        if (depth > MaxJsonDepth) return false;
        p++; // '{'
        while (true)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (s[p] == '}') { p++; return true; }
            if (s[p] != '"') return false;
            if (!ReadJsonString(s, ref p, out string key)) return false;
            SkipWs(s, ref p);
            if (p >= s.Length || s[p] != ':') return false;
            p++;
            SkipWs(s, ref p);
            if (p >= s.Length) return false;

            char c = s[p];
            if (c == '{')
            {
                if (!ParseJsonObject(s, ref p, prefix + key + ".", depth + 1)) return false;
            }
            else if (c == '[')
            {
                if (!ParseJsonNestedArray(s, ref p, prefix + key + ".", depth + 1)) return false;
            }
            else
            {
                if (!ParseJsonScalar(s, ref p, out bool isNum, out double v, out string str)) return false;
                if (isNum) PutJson(prefix, key, v);
                else if (str != null)
                {
                    bool special = false;
                    if (depth == 0)
                    {
                        special = true;
                        if (string.Equals(key, "log", StringComparison.OrdinalIgnoreCase)) _jsonLog = str;
                        else if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)) _jsonType = str;
                        else if (string.Equals(key, "msg", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(key, "message", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(key, "text", StringComparison.OrdinalIgnoreCase)) _jsonText = str;
                        else special = false;
                    }
                    // 数値が文字列で届くことがある（json.dumps(..., default=str) など）
                    if (!special && TryParseDouble(str, 0, str.Length, out double sv)) PutJson(prefix, key, sv);
                }
            }

            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (s[p] == ',') { p++; continue; }
            if (s[p] == '}') { p++; return true; }
            return false;
        }
    }

    // オブジェクト内の配列: 数値要素を "key.0" "key.1" ... として登録（入れ子はその先へ）
    bool ParseJsonNestedArray(string s, ref int p, string prefix, int depth)
    {
        if (depth > MaxJsonDepth) return false;
        p++; // '['
        int i = 0;
        while (true)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (s[p] == ']') { p++; return true; }
            char c = s[p];
            if (c == '{')
            {
                if (!ParseJsonObject(s, ref p, prefix + ExtraIndex(i) + ".", depth + 1)) return false;
            }
            else if (c == '[')
            {
                if (!ParseJsonNestedArray(s, ref p, prefix + ExtraIndex(i) + ".", depth + 1)) return false;
            }
            else
            {
                if (!ParseJsonScalar(s, ref p, out bool isNum, out double v, out _)) return false;
                if (isNum) Put(_msg, prefix + ExtraIndex(i), v);
            }
            i++;
            SkipWs(s, ref p);
            if (p >= s.Length) return false;
            if (s[p] == ',') { p++; continue; }
            if (s[p] == ']') { p++; return true; }
            return false;
        }
    }

    // 入れ子のキーは "a.b" と末端名 "b" の両方で引けるようにする（末端名は先勝ち、トップレベルは常に上書き）
    void PutJson(string prefix, string key, double v)
    {
        if (prefix.Length == 0) { Put(_msg, key, v); return; }
        Put(_msg, prefix + key, v);
        if (!_msg.values.ContainsKey(key)) Put(_msg, key, v);
    }

    // 文字列 / 数値 / true / false / null / NaN / Infinity
    bool ParseJsonScalar(string s, ref int p, out bool isNum, out double v, out string str)
    {
        isNum = false; v = 0; str = null;
        char c = s[p];
        if (c == '"') return ReadJsonString(s, ref p, out str);
        if (MatchWord(s, p, "true")) { p += 4; isNum = true; v = 1; return true; }
        if (MatchWord(s, p, "false")) { p += 5; isNum = true; v = 0; return true; }
        if (MatchWord(s, p, "null")) { p += 4; return true; }
        // Python の json.dumps は NaN / Infinity をそのまま出力する → 値なしとして読み飛ばす
        if (MatchWord(s, p, "NaN")) { p += 3; return true; }
        if (MatchWord(s, p, "Infinity")) { p += 8; return true; }
        if (MatchWord(s, p, "-Infinity")) { p += 9; return true; }
        if (MatchWord(s, p, "+Infinity")) { p += 9; return true; }

        int q = p;
        while (q < s.Length)
        {
            char d = s[q];
            if ((d >= '0' && d <= '9') || d == '+' || d == '-' || d == '.' || d == 'e' || d == 'E') q++;
            else break;
        }
        if (q == p) return false;
        if (!TryParseDouble(s, p, q, out v)) return false;
        p = q;
        isNum = IsFinite(v);
        return true;
    }

    static bool MatchWord(string s, int p, string w)
    {
        return p + w.Length <= s.Length && string.CompareOrdinal(s, p, w, 0, w.Length) == 0;
    }

    bool ReadJsonString(string s, ref int p, out string result)
    {
        result = null;
        int q = p + 1;
        // エスケープなしの高速パス
        while (q < s.Length && s[q] != '"' && s[q] != '\\') q++;
        if (q >= s.Length) return false;
        if (s[q] == '"') { result = s.Substring(p + 1, q - p - 1); p = q + 1; return true; }

        var sb = new StringBuilder(s.Length - p);
        sb.Append(s, p + 1, q - p - 1);
        while (q < s.Length)
        {
            char c = s[q++];
            if (c == '"') { result = sb.ToString(); p = q; return true; }
            if (c != '\\') { sb.Append(c); continue; }
            if (q >= s.Length) return false;
            char e = s[q++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (q + 4 > s.Length) return false;
                    if (!ushort.TryParse(s.Substring(q, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort cp)) return false;
                    sb.Append((char)cp);
                    q += 4;
                    break;
                default: sb.Append(e); break;                  // \" \\ \/
            }
        }
        return false;
    }

    static void SkipWs(string s, ref int p)
    {
        while (p < s.Length && (IsWhite(s[p]) || s[p] == '\0')) p++;
    }

    //====================================================================
    // OSC 1.0（メッセージ / バンドル）
    //====================================================================

    bool TryParseOsc(byte[] d, int start, int len, Action<TelemetryMessage> on, int depth, out int emitted)
    {
        emitted = 0;
        if (len < 8 || (len & 3) != 0 || depth > MaxOscDepth) return false;   // OSC は常に4バイト境界
        int end = start + len;

        if (d[start] == (byte)'#')
        {
            // "#bundle\0" + timetag(8) + { size(int32 BE) + element }...
            const string tag = "#bundle";
            if (len < 16) return false;
            for (int i = 0; i < 7; i++) if (d[start + i] != (byte)tag[i]) return false;
            if (d[start + 7] != 0) return false;
            int p = start + 16;
            while (p + 4 <= end)
            {
                int size = ReadInt32BE(d, p);
                p += 4;
                if (size < 0 || size > end - p) return false;
                TryParseOsc(d, p, size, on, depth + 1, out int n);
                emitted += n;
                p += size;
            }
            return true;
        }

        if (d[start] != (byte)'/') return false;
        int pos = start;
        if (!ReadOscString(d, ref pos, end, out int aStart, out int aLen)) return false;
        if (pos >= end || d[pos] != (byte)',') return false;                  // 型タグなしの旧形式は非対応
        if (!ReadOscString(d, ref pos, end, out int tStart, out int tLen)) return false;

        string address = OscAddress(d, aStart, aLen);
        if (!string.IsNullOrEmpty(OscAddressFilter) &&
            !string.Equals(address, OscAddressFilter, StringComparison.Ordinal))
        {
            // 対象外（OSC としては正しい）。理由の文字列はアドレスが変わったときだけ作り直す
            if (!ReferenceEquals(address, _oscIssueAddress))
            {
                _oscIssueAddress = address;
                _oscIssueText = "OSC address " + address + " ignored (filter: " + OscAddressFilter + ")";
            }
            LastIssue = _oscIssueText;
            return true;
        }

        _msg.Reset(TelemetryMessageKind.Sample, TelemetryWireFormat.Osc);
        _msg.oscAddress = address;

        int arg = 0, numbers = 0;
        for (int t = tStart + 1; t < tStart + tLen; t++)
        {
            char tag = (char)d[t];
            switch (tag)
            {
                case 'f':
                    if (pos + 4 > end) return false;
                    Put(_msg, PositionalName(arg), BitConverter.Int32BitsToSingle(ReadInt32BE(d, pos)));
                    pos += 4; arg++; numbers++; break;
                case 'i':
                    if (pos + 4 > end) return false;
                    Put(_msg, PositionalName(arg), ReadInt32BE(d, pos));
                    pos += 4; arg++; numbers++; break;
                case 'd':
                    if (pos + 8 > end) return false;
                    Put(_msg, PositionalName(arg), BitConverter.Int64BitsToDouble(ReadInt64BE(d, pos)));
                    pos += 8; arg++; numbers++; break;
                case 'h':
                    if (pos + 8 > end) return false;
                    Put(_msg, PositionalName(arg), ReadInt64BE(d, pos));
                    pos += 8; arg++; numbers++; break;
                case 'T': Put(_msg, PositionalName(arg), 1); arg++; break;
                case 'F': Put(_msg, PositionalName(arg), 0); arg++; break;
                case 'N': case 'I': arg++; break;
                case 's': case 'S':
                    if (!ReadOscString(d, ref pos, end, out _, out _)) return false;
                    arg++; break;
                case 'b':
                {
                    if (pos + 4 > end) return false;
                    int size = ReadInt32BE(d, pos);
                    pos += 4;
                    if (size < 0 || size > end - pos) return false;
                    pos += (size + 3) & ~3;
                    arg++; break;
                }
                case 't': pos += 8; arg++; break;
                case 'c': case 'r': case 'm': pos += 4; arg++; break;
                case '[': case ']': break;
                default: return false;                                      // 未知の型はサイズ不明なので打ち切り
            }
            if (pos > end) return false;
        }

        // 数値(f/i/d/h)が3つ未満のメッセージは姿勢ではない（真偽値だけのフラグなどを取り違えない）
        if (numbers < MinPositionalValues || _msg.values.Count == 0)
        {
            LastIssue = "OSC message with fewer than 3 numbers";
            return true;
        }
        on(_msg);
        emitted = 1;
        return true;
    }

    // NUL 終端・4バイト境界にパディングされた文字列。pos は次の要素へ進む。
    static bool ReadOscString(byte[] d, ref int pos, int end, out int sStart, out int sLen)
    {
        sStart = pos; sLen = 0;
        int q = pos;
        while (q < end && d[q] != 0) q++;
        if (q >= end) return false;
        sLen = q - pos;
        pos = sStart + ((sLen + 4) & ~3);
        return pos <= end;
    }

    string OscAddress(byte[] d, int start, int len)
    {
        bool same = _oscAddrBytes.Length == len;
        for (int i = 0; same && i < len; i++) same = _oscAddrBytes[i] == d[start + i];
        if (!same)
        {
            _oscAddrBytes = new byte[len];
            Buffer.BlockCopy(d, start, _oscAddrBytes, 0, len);
            _oscAddrString = Encoding.ASCII.GetString(d, start, len);
        }
        return _oscAddrString;
    }

    static int ReadInt32BE(byte[] d, int p)
    {
        return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
    }

    static long ReadInt64BE(byte[] d, int p)
    {
        return ((long)(uint)ReadInt32BE(d, p) << 32) | (uint)ReadInt32BE(d, p + 4);
    }

    //====================================================================
    // helpers
    //====================================================================

    // 値を登録。seq / t_ms は精度を保つためメッセージ側にも double / long で持つ。
    static void Put(TelemetryMessage m, string name, double v)
    {
        if (!IsFinite(v)) return;
        m.values[name] = (float)v;
        if (name.Length == 4 && string.Equals(name, "t_ms", StringComparison.OrdinalIgnoreCase))
        {
            m.hasDeviceTimeMs = true; m.deviceTimeMs = v;
        }
        else if ((name.Length == 3 && string.Equals(name, "seq", StringComparison.OrdinalIgnoreCase)) ||
                 (name.Length == 7 && string.Equals(name, "src_seq", StringComparison.OrdinalIgnoreCase)))
        {
            m.hasSeq = true; m.seq = (long)v;
        }
    }

    static bool IsFinite(double v) { return !(double.IsNaN(v) || double.IsInfinity(v)); }

    string PositionalName(int i) { return i < _positional.Length ? _positional[i] : ExtraName(i); }

    static readonly string[] s_extraNames = BuildNames("v", 64);
    static readonly string[] s_extraIndex = BuildNames("", 64);
    static string[] BuildNames(string prefix, int n)
    {
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = prefix + i.ToString(CultureInfo.InvariantCulture);
        return a;
    }
    static string ExtraName(int i) { return i < s_extraNames.Length ? s_extraNames[i] : "v" + i.ToString(CultureInfo.InvariantCulture); }
    static string ExtraIndex(int i) { return i < s_extraIndex.Length ? s_extraIndex[i] : i.ToString(CultureInfo.InvariantCulture); }

    static bool IsWhite(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

    static void Trim(string s, ref int a, ref int b)
    {
        while (a < b && (IsWhite(s[a]) || s[a] == '\0')) a++;
        while (b > a && (IsWhite(s[b - 1]) || s[b - 1] == '\0')) b--;
    }

    static bool StartsWith(string s, int ls, int le, string prefix)
    {
        return le - ls >= prefix.Length &&
               string.Compare(s, ls, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    // カンマ区切りの次のトークン（空トークンも返す。前後の空白は除く）
    static bool NextToken(string s, ref int p, int le, out int ts, out int te)
    {
        ts = te = p;
        if (p > le) return false;
        int q = p;
        while (q < le && s[q] != ',') q++;
        ts = p; te = q;
        p = q + 1;                    // 末尾まで読んだら p == le + 1 になり、次回は false
        Trim(s, ref ts, ref te);
        return true;
    }

    // カンマ / セミコロン / 空白 / タブ区切りの次の「空でない」トークン
    static bool NextTokenAny(string s, ref int p, int le, out int ts, out int te)
    {
        while (p < le && IsDelim(s[p])) p++;
        ts = p;
        while (p < le && !IsDelim(s[p])) p++;
        te = p;
        return te > ts;
    }

    static bool IsDelim(char c) { return c == ',' || c == ';' || c == ' ' || c == '\t'; }

    // "nan" "inf" "-inf" "infinity" "ovf"（Arduino の Serial.print や Python の str(float) が出す、値なしの印）
    static bool IsNonFiniteToken(string s, int a, int b)
    {
        if (a < b && (s[a] == '-' || s[a] == '+')) a++;
        int n = b - a;
        return (n == 3 && (string.Compare(s, a, "nan", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 ||
                           string.Compare(s, a, "inf", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 ||
                           string.Compare(s, a, "ovf", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)) ||
               (n == 8 && string.Compare(s, a, "infinity", 0, 8, StringComparison.OrdinalIgnoreCase) == 0);
    }

    static bool TryParseDouble(string s, int a, int b, out double v)
    {
        v = 0;
        if (b <= a) return false;
        // 数字・符号・小数点から始まるものだけを数値とみなす（"NaN" "Infinity" などの単語は数値扱いしない）
        char c = s[a];
        if (!((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.')) return false;
        return double.TryParse(s.AsSpan(a, b - a), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && IsFinite(v);
    }
}
