using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_STANDALONE
using SFB; // UnityStandaloneFileBrowser
#endif

public class CsvOpenDialogButton : MonoBehaviour
{
    public enum InitialDirMode { LastDir, PersistentDataPath, MyDocuments }

    [Header("Refs")]
    public PoseCsvStore store;              // ← loadOnStart=false にして割当
    public Button openButton;               // ← これを押すとダイアログ
    public TMP_Text statusLabel;            // 読込結果やエラー表示
    public TMP_Text fileNameLabel;          // 選択したファイル名（任意）
    public TMP_InputField manualPathInput;  // 任意：手動パス入力（Standalone/Editor向け）

    [Header("Filters")]
    public bool requireCsvExtension = true;

    [Header("Initial Directory")]
    public InitialDirMode initialDirMode = InitialDirMode.LastDir;

#if UNITY_WEBGL && !UNITY_EDITOR
    // WebGL の JS プラグイン（FilePicker.jslib）と連携
    [DllImport("__Internal")] private static extern void FilePicker_OpenFileDialog(string gameObjectName, string accept);
#endif

    void Awake()
    {
        if (openButton) openButton.onClick.AddListener(OpenDialog);
        if (store != null)
        {
            store.Loaded     += OnStoreLoaded;
            store.LoadFailed += OnStoreLoadFailed;
        }

#if UNITY_EDITOR
        Debug.Log("[CsvOpenDialogButton] UNITY_EDITOR: EditorUtility.OpenFilePanel を使用");
#elif UNITY_STANDALONE
        Debug.Log("[CsvOpenDialogButton] UNITY_STANDALONE: UnityStandaloneFileBrowser(SFB) を使用");
#elif UNITY_WEBGL
        Debug.Log("[CsvOpenDialogButton] UNITY_WEBGL: WebGL FilePicker (jslib) を使用");
#else
        Debug.Log("[CsvOpenDialogButton] このプラットフォームではOSダイアログ未対応（モバイル等）");
#endif
    }

    void OnDestroy()
    {
        if (store != null)
        {
            store.Loaded     -= OnStoreLoaded;
            store.LoadFailed -= OnStoreLoadFailed;
        }
    }

    void SetStatus(string msg, bool isError = false)
    {
        if (statusLabel) statusLabel.text = isError ? $"<color=#ff6666>{msg}</color>" : msg;
        Debug.Log((isError ? "[CsvOpenDialogButton][ERROR] " : "[CsvOpenDialogButton] ") + msg);
    }

    string GetInitialDirectory()
    {
        string d = null;
        switch (initialDirMode)
        {
            case InitialDirMode.LastDir:            d = PoseCsvStore.GetLastDirOrDefault(); break;
            case InitialDirMode.PersistentDataPath: d = Application.persistentDataPath;      break;
            case InitialDirMode.MyDocuments:        d = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); break;
        }
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d))
            d = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return d;
    }

    public void OpenDialog()
    {
        if (store == null) { SetStatus("PoseCsvStore が未割り当てです。", true); return; }
        if (openButton) openButton.interactable = false;

#if UNITY_EDITOR
        string initialDir = GetInitialDirectory();
        string selectedPath = UnityEditor.EditorUtility.OpenFilePanel("CSVを選択", initialDir, "csv");
        HandleSelectedPathOrCancel(selectedPath);

#elif UNITY_STANDALONE
        string initialDir = GetInitialDirectory();
        try
        {
            var extensions = new[] {
                new ExtensionFilter("CSV", "csv"),
                new ExtensionFilter("All Files", "*")
            };
            var paths = StandaloneFileBrowser.OpenFilePanel("CSVを選択", initialDir, extensions, false);
            Debug.Log($"[CsvOpenDialogButton] SFB returned {(paths == null ? "null" : paths.Length.ToString())} results");
            string selectedPath = (paths != null && paths.Length > 0) ? paths[0] : null;
            HandleSelectedPathOrCancel(selectedPath);
        }
        catch (Exception ex)
        {
            SetStatus($"SFB呼び出し例外: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }

#elif UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            // .csv と text/csv を許可
            FilePicker_OpenFileDialog(gameObject.name, ".csv,text/csv");
            SetStatus("ブラウザのダイアログを開きました…");
        }
        catch (Exception ex)
        {
            SetStatus($"WebGLダイアログ呼び出し例外: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }
#else
        SetStatus("このプラットフォームではOSダイアログ未対応です。", true);
        if (openButton) openButton.interactable = true;
#endif
    }

    void HandleSelectedPathOrCancel(string selectedPath)
    {
        if (string.IsNullOrEmpty(selectedPath))
        {
            SetStatus("ダイアログが閉じられました（キャンセル/失敗）。");
            TryLoadFromManualInputFallback();
            if (openButton) openButton.interactable = true;
            return;
        }

        if (!File.Exists(selectedPath))
        {
            SetStatus("ファイルが存在しません。", true);
            if (openButton) openButton.interactable = true;
            return;
        }
        if (requireCsvExtension && Path.GetExtension(selectedPath).ToLowerInvariant() != ".csv")
        {
            SetStatus("CSV(.csv)ファイルを選択してください。", true);
            if (openButton) openButton.interactable = true;
            return;
        }

        try
        {
            store.LoadFromAbsolutePath(selectedPath); // 成否はイベントで受け取る
            if (fileNameLabel) fileNameLabel.text = Path.GetFileName(selectedPath);
            SetStatus("読み込み中…");
        }
        catch (Exception ex)
        {
            SetStatus($"例外: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }
    }

    void TryLoadFromManualInputFallback()
    {
        if (manualPathInput == null) return;
        var p = manualPathInput.text?.Trim();
        if (string.IsNullOrEmpty(p)) return;

        if (!File.Exists(p))
        {
            SetStatus("手動入力のパスが存在しません。", true);
            return;
        }
        if (requireCsvExtension && Path.GetExtension(p).ToLowerInvariant() != ".csv")
        {
            SetStatus("手動入力はCSV(.csv)のみ対応です。", true);
            return;
        }
        try
        {
            store.LoadFromAbsolutePath(p);
            if (fileNameLabel) fileNameLabel.text = Path.GetFileName(p);
            SetStatus("手動パスから読み込み中…");
        }
        catch (Exception ex)
        {
            SetStatus($"手動パス読み込み例外: {ex.Message}", true);
        }
    }

    // ===== WebGL コールバック =====
    [Serializable]
    private class WebGLPickPayload { public string name; public string data; } // data = Base64

    // JS から呼ばれる（成功）
    public void OnWebGLFilePicked(string payloadJson)
    {
        try
        {
            var payload = JsonUtility.FromJson<WebGLPickPayload>(payloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.data))
                throw new Exception("payloadが空です");

            byte[] bytes = Convert.FromBase64String(payload.data);
            string text = Encoding.UTF8.GetString(bytes);
            string virtName = string.IsNullOrEmpty(payload.name) ? "(webgl)" : payload.name;

            if (fileNameLabel) fileNameLabel.text = virtName;
            store.LoadFromText(text, virtName);
            SetStatus("読み込み中…");
        }
        catch (Exception ex)
        {
            SetStatus($"WebGLデコード失敗: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }
        finally
        {
            if (openButton) openButton.interactable = true;
        }
    }

    // JS から呼ばれる（失敗/キャンセル）
    public void OnWebGLFilePickError(string message)
    {
        SetStatus($"WebGLダイアログ失敗/キャンセル: {message}");
        if (openButton) openButton.interactable = true;
    }

    void OnStoreLoaded()
    {
        if (openButton) openButton.interactable = true;
        SetStatus($"読み込み完了：{(string.IsNullOrEmpty(store.CurrentPath) ? "(不明)" : Path.GetFileName(store.CurrentPath))}  /  Duration={store.Duration:0.000}s");
    }
    void OnStoreLoadFailed(string reason)
    {
        if (openButton) openButton.interactable = true;
        SetStatus($"読み込み失敗：{reason}", true);
    }
}
