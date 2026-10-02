# リアルタイムUDP表示（RealtimePreview）送信側の実装ガイド

`Assets/Scenes/RealtimePreview.unity` は、UDP で届いた姿勢データをその場で機体モデルに反映するシーンです。
このドキュメントは、機体からデータを取得する **Python 側（例: [Glider-Control_Cluster-Comunication](https://github.com/IPTeCA/Glider-Control_Cluster-Comunication)）に UDP 送信を実装する人** 向けに、Unity 側が何を受け取れるかをまとめたものです。

```
機体 (XIAO + 親機ESP32) ──ESP-NOW──> 子機ESP32 ──USBシリアル──> PC の Python ──UDP──> Unity (RealtimePreview)
```

## 1. Unity 側の待ち受け

| 項目 | 内容 |
|---|---|
| ポート | UDP **9000**（既定）。再生中に画面右上の `UDP port` 欄で変更できます（Inspector では `UdpTelemetryReceiver` の `Port`） |
| アドレス | その PC の全インターフェース（IPv4 と IPv6 の両方）。同じ PC からなら宛先は `127.0.0.1` |
| 単位 | **1メッセージ = 1データグラム**（1データグラムに複数行を入れても構いません） |
| 形式 | データグラムごとに自動判別（下記のどれでも可） |
| 文字コード | UTF-8（末尾の改行はあってもなくても可） |

データが届く前の画面には `WAITING  UDP port 9000  this PC: <このPCのIPアドレス>` と表示されます。別の PC から送る場合は、そこに出ている IP アドレスを宛先にしてください。

## 2. 送信形式

### 2.1 シリアルの行をそのまま転送する（おすすめ）

子機 ESP32 がシリアルに出す行を、**加工せずに 1行ずつ** 送ります。

```
HDR,1,GLDR,fields=dt_ms,ax,ay,az,gx,gy,gz,roll,pitch,yaw,s0,s1,s2
DAT,1234,56789,33.000,0.010,-0.020,1.000,0.100,-0.200,0.300,1.500,-2.300,180.000,85.000,105.000,70.000
LOG,mode:Manual
```

- `DAT,<seq>,<t_ms>,<値...>` の値は、直近の `HDR` の `fields=` の順に名前が付きます。姿勢には `roll` `pitch` `yaw`（度）を使います。
- `HDR` をまだ受け取っていない間は、既定の並び `dt_ms,ax,ay,az,gx,gy,gz,roll,pitch,yaw,s0,s1,s2` とみなします。
  ファームウェアのフィールド構成を変えた場合に備えて、**`HDR` は数秒ごとに送り直してください**（UDP は届かないことがあり、Unity を後から起動することもあるため）。
- `seq` は欠落数（画面の `lost`）の計算に、`t_ms` はグラフの時間軸に使います。`t_ms` が大きく戻ったら機体の再起動とみなし、方位のゼロ点を取り直します。
- `LOG,<文字列>` は画面左上のパネルに表示されます（`mode:...` と `Param:...` は専用の行に出ます）。
- `nan` / `inf` / `ovf` のような値は、その値だけ「なし」として扱います。

```python
import socket

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
UNITY = ("127.0.0.1", 9000)

# シリアルから1行読むたびに（GUI のタイマーではなく、シリアル受信スレッドの中で送るのが理想）:
line = ser.readline().decode("utf-8", errors="ignore").strip()
if line.startswith(("HDR,", "DAT,", "LOG,")):
    sock.sendto(line.encode("utf-8"), UNITY)
```

`viewer_serialsend.py` に `--udp-ip` / `--udp-port` を追加する参考パッチを [viewer_serialsend_udp.patch](viewer_serialsend_udp.patch) に置いてあります（上流の `3a70e97` に対してそのまま当たります）。

```bash
cd Glider-Control_Cluster-Comunication
git apply /path/to/GliderIMUViewer/Docs/viewer_serialsend_udp.patch
python host/python/viewer_serialsend.py --port COM3 --udp-ip 127.0.0.1 --udp-port 9000
```

### 2.2 JSON

名前付きの数値を 1 オブジェクトで送ります。

```python
import json
payload = {"seq": dp.src_seq, "t_ms": dp.t_ms, **dp.fields}   # roll, pitch, yaw, s0, ...
sock.sendto(json.dumps(payload).encode("utf-8"), UNITY)
```

```json
{"seq": 1234, "t_ms": 56789, "roll": 1.5, "pitch": -2.3, "yaw": 180.0, "s0": 85.0}
```

- 必須なのは `roll` `pitch` `yaw` のいずれか（来なかった成分は前回値のまま）。`seq` `t_ms` は任意です。
- 入れ子も可（`{"fields": {"roll": 1.5}}` は `roll` としても `fields.roll` としても参照できます）。
- LOG は `{"log": "mode:Auto"}`。
- `NaN` / `Infinity`（`json.dumps` が出力することがあります）は、その値だけ読み飛ばします。

### 2.3 OSC

`/plane/data` に float で `[roll, pitch, yaw, sv1, sv3]` を送る OSC メッセージを受け取れます。

**`viewer_serialsend.py` の既存の OSC 出力は、そのまま使えます**（Python 側の変更は不要です）。

```bash
python host/python/viewer_serialsend.py --port COM3 --osc-ip 127.0.0.1 --osc-port 9000
```

- 引数は位置で `roll, pitch, yaw, sv1, sv3` に対応付けます（Inspector の `Positional Fields` で変更可）。数値が 3 つ未満のメッセージは無視します。
- 受け付けるアドレスは `/plane/data` だけです（`UdpTelemetryReceiver` の `Osc Address Filter`。空にすると全アドレスを受け付けます）。
- OSC には `seq` と `t_ms` がないので、欠落数は数えられず、グラフの時間軸は受信時刻になります。`viewer_serialsend.py` は 50ms 周期の描画タイマーからまとめて送るので、動きが少しカクつきます。滑らかにしたい場合は 2.1 の方法を使ってください。
- `viewer_serialsend.py` は最初の `HDR` を受け取るまで `[0, 0, 0]` を送ります（子機経由なら最長 5 秒ほど）。

> **`logger_gui.py` の「Cluster OSC」出力は、現行ファームウェア（`DAT,...` 行）では姿勢になりません。**
> 行の先頭から数値を 5 つ拾って送る実装のため、`/plane/data` の中身が `[seq, t_ms, dt_ms, ax, ay]` になります。
> Unity 側はこれを角度の範囲外として無視し、画面に `NO ATTITUDE ... values out of range` と表示します。
> `logger_gui.py` から送りたい場合は、`SerialWorker.run` の中（1行読んだ直後）に 2.1 の数行を足してください。

### 2.4 数値だけの CSV / key:value

```
1.5,-2.3,180.0            ← roll,pitch,yaw の順（3 つ以上の数値）
roll:1.5,pitch:-2.3,yaw:180.0
```

### 2.5 同じポートに複数の形式が届いた場合

名前付きの形式（`DAT` / JSON / key:value）が届いている間は、位置で対応付ける形式（OSC / 数値CSV）を無視します。
たとえば `viewer_serialsend.py` の OSC 出力と 2.1 の転送を同じ宛先に向けても、表示がちらつくことはありません（画面下の `osc/csv skipped` が増えます）。

## 3. ハードウェアなしで試す

```bash
# 滑空機らしい動きを生成して送る（標準ライブラリのみ、Python 3.10 以降）
python Tools/udp_test_sender.py
python Tools/udp_test_sender.py --format json
python Tools/udp_test_sender.py --format osc --rate 50

# 記録済み CSV を実時間で再生して送る
python Tools/udp_test_sender.py --csv Assets/StreamingAssets/out_20250912-152517.csv --loop

# 通信の悪い状況を模擬（10% 欠落、30ms までの遅延、3 個ずつまとめて到着）
python Tools/udp_test_sender.py --loss 0.1 --jitter-ms 30 --burst 3
```

実機があるときは、Python 側に UDP 送信を実装する前でも、付属のブリッジでそのまま流せます（要 `pip install pyserial`）。

```bash
python Tools/serial_udp_bridge.py --list             # シリアルポート一覧
python Tools/serial_udp_bridge.py --serial COM3      # 子機ESP32 の行を 127.0.0.1:9000 へ転送
python Tools/serial_udp_bridge.py --serial COM3 --host 192.168.0.20 --port 9000
```

> COM ポートは同時に 1 つのプログラムしか開けません。ブリッジを使う間は `viewer_serialsend.py` / `logger_gui.py` / シリアルモニタを閉じてください（ブリッジからは機体へのコマンド送信はできません）。コマンドも送りたい場合は、2.1 のパッチをビューア側に当てるのが確実です。

## 4. 画面と操作

| 表示 | 意味 |
|---|---|
| `WAITING` | 待ち受け中（まだ何も届いていない） |
| `LIVE 30.0 Hz from <送信元> [DAT]` | 受信中。レート・送信元・採用している形式 |
| `NO DATA 2.3 s` | 1 秒以上データが途絶えている（モデルは最後の姿勢のまま） |
| `NO ATTITUDE ...` | 何か届いているが姿勢として読めない。続けて理由が出ます（値が範囲外、OSC のアドレス違い、形式が読めない など） |
| `ERROR port 9000 is already in use ...` | 他のアプリが同じポートを使用中。右上の欄で別のポートにする |

| 操作 | 内容 |
|---|---|
| `Pause` / Space | 表示の一時停止・再開（受信は続きます） |
| `Zero` / Z | 現在の機首方位を 0 に取り直す（`Zero: Level` のときは、現在の傾きも「水平」として取り直す） |
| `Zero: Yaw` ボタン | ゼロ点の取り方を切り替え：`Yaw`（機首方位だけ 0 に合わせる。roll・pitch は受信値のまま）→ `Level`（方位に加えて、`Zero` を押したときの傾きを水平とみなす）→ `Off`（受信値のまま） |
| `IMU X: tail` ボタン | IMU の取り付け向き（軸の割当）を切り替え：`IMU X: tail` → `IMU X: nose` → `Legacy map`（5 章） |
| グラフ上の `Roll` / `Pitch` / `Yaw` をクリック | その線の表示・非表示（yaw を消すと roll・pitch が大きく見えます） |
| F1 / 右上の ∨ | UI の表示・非表示 |
| ドラッグ / ホイール | 視点の回転 / ズーム |

## 5. 角度の解釈と、IMU の取り付け向き

- `roll` `pitch` `yaw` は **度**（ラジアンで送る場合は `RealtimePoseStore` の `Angles Are Radians` を有効に）。
- ファームウェアが使っている Madgwick ライブラリの定義どおり、**yaw → pitch → roll の順**に合成します（`R = Rz(yaw)·Ry(pitch)·Rx(roll)`、Z が上の右手系）。
- 機体モデルは **胴体が Z 軸（機首が -Z）、翼幅が X 軸、上が Y 軸**です。IMU の X 軸が胴体方向に付いている前提で、roll を胴体軸まわり、pitch を翼幅軸まわりの回転として表示します。

IMU の +X が機首・機尾のどちらを向いているかで、roll と pitch の符号が変わります。画面下の `IMU X: ...` ボタンで切り替えてください。

| ボタン表示 | 取り付け | 受信値と実機の動きの対応 |
|---|---|---|
| `IMU X: tail`（既定） | +X が機尾向き（現行ファーム。発射時に `ax` が負に振れる機体） | 機首を上げると pitch が増える／右翼を下げると roll が減る |
| `IMU X: nose` | +X が機首向き（`out_20250912-152517.csv` を記録した頃の機体） | 機首を上げると pitch が減る／右翼を下げると roll が増える |
| `Legacy map` | KinematicPreview シーンと同じ割当（roll→X, pitch→Z, yaw→Y）・同じ合成（`Quaternion.Euler`） | CSV 再生シーンと同じ見え方になります |

**確認のしかた（10 秒）**: 実機を手に持って機首を上げ、次に右翼を下げます。画面のモデルが同じ向きに動けば合っています。逆に動くなら `IMU X: ...` を切り替えてください。機首を左に振ると、どの取り付けでも yaw は増えます。

> KinematicPreview シーンの割当（roll→X, pitch→Z）は、このモデルに対しては roll を「翼幅軸まわり」、pitch を「胴体軸まわり」に回すため、IMU の X 軸が胴体方向に付いている機体では **roll と pitch が入れ替わって見えます**。`Legacy map` は、その見え方と揃えたいとき用に残してあります。

ビルド版では、ボタンで選んだ向きを次回起動時も引き継ぎます。Editor で既定を変えるには、`RealtimePoseStore` コンポーネントの右クリックメニュー（`Axis preset: ...`）か `Axis Map` を直接編集して、シーンを保存してください。

- `yaw` はジャイロ積分のためドリフトします（地磁気センサーなし）。機首の向きがずれてきたら `Zero` を押してください。電源投入直後は姿勢推定が落ち着くまで数秒かかります。
- **水平に置いても roll / pitch が 0 にならない場合**（IMU が機体に対して少し傾いて付いている）: `Zero: Yaw` ボタンを押して `Zero: Level` にし、機体を水平に置いた状態で `Zero` を押してください。以後は、その傾きを差し引いた姿勢を表示します（グラフと数値も補正後の値。受信値そのものは左上のパネルの `recv` 行に出ます）。機体が再起動しても、この傾きの補正は保たれます（方位のゼロ点だけ取り直します）。
- Inspector の `Zero Mode` には、ボタンでは選べない `Relative` もあります。ゼロ点を取ったときの姿勢からの相対回転を表示するもので、KinematicPreview の `zeroAtStart` と同じ計算です。

## 6. うまくいかないとき

- **`WAITING` のまま**: 宛先 IP とポートを確認。`python Tools/udp_test_sender.py` で同じ PC から届くかを先に確かめると切り分けできます（同じ PC 内の `127.0.0.1` 宛てなら、ファイアウォールの設定は不要です）。
  別 PC から送る場合は、Unity 側 PC のファイアウォールで受信が許可されている必要があります。Unity Editor には、ネットワークの種類が「パブリック」のときに受信をブロックする規則（「受信の規則」の `Unity <バージョン> Editor`）が入っていることがあります。その場合は、接続中のネットワークを「プライベート」に変えるか、その規則を許可に変更してください（管理者権限が必要）。ビルドしたアプリは、初回起動時に出る確認ダイアログで許可します。
- **`NO ATTITUDE`**: 表示されている理由を確認してください。`DAT` 行の並びが既定と違う場合は `HDR` も送る。JSON はキー名が `roll` `pitch` `yaw` か確認（別名なら `RealtimePoseStore` の `Roll/Pitch/Yaw Field`）。`UdpTelemetryReceiver` の `Log Unparsed` を有効にすると、読めなかったデータが Console に出ます。
- **動きが実機と逆／roll と pitch が入れ替わる**: 5 章のボタンで取り付け向きを切り替えてください。
- **動きがカクカクする**: 送信がまとまって届いている可能性があります（例: 50ms 周期の GUI タイマーから送っている）。シリアル受信スレッドから 1 行ごとに送るのが理想です。`RealtimePoseApplier` の `Smoothing Time` を増やすと滑らかになります。
- **WebGL ビルド**: ブラウザでは UDP を受信できないため、このシーンは Editor / スタンドアロン専用です。
