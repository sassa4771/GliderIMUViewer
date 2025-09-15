using System;
using System.IO;
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
    public TMP_Text statusLabel;            // 成功/失敗のみ表示
    public TMP_Text fileNameLabel;          // 選択したファイル名（任意）
    public TMP_InputField manualPathInput;  // 任意：手動パス入力（Standalone/Editor向け）

    [Header("Filters")]
    public bool requireCsvExtension = true;

    [Header("Initial Directory")]
    public InitialDirMode initialDirMode = InitialDirMode.LastDir;

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void FilePicker_OpenFileDialog(string gameObjectName, string accept);
#endif

    void Awake()
    {
        if (openButton) openButton.onClick.AddListener(OpenDialog);
        if (store != null)
        {
            // 成功/失敗だけ拾う（「読み込み中…」は出さない）
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
        string selectedPath = UnityEditor.EditorUtility.OpenFilePanel("CSVを選択", GetInitialDirectory(), "csv");
        HandleSelectedPathOrCancel(selectedPath);

#elif UNITY_STANDALONE
        try
        {
            var exts  = new[] {
                new ExtensionFilter("CSV", "csv"),
                new ExtensionFilter("All Files", "*")
            };
            var paths = StandaloneFileBrowser.OpenFilePanel("CSVを選択", GetInitialDirectory(), exts, false);
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
            FilePicker_OpenFileDialog(gameObject.name, ".csv,text/csv");
            // 進捗表示はしない（成功/失敗のCBだけ扱う）
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
            SetStatus("キャンセルしました。");
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
            // 同期読み込みなので即時に Loaded が発火する想定。二重表示にならないようここでは何も表示しない。
            store.LoadFromAbsolutePath(selectedPath);
            if (fileNameLabel) fileNameLabel.text = Path.GetFileName(selectedPath);
        }
        catch (Exception ex)
        {
            SetStatus($"例外: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }
    }

    // WebGL 成功: PoseCsvStore からイベント経由で来る
    void OnStoreLoaded()
    {
        if (openButton) openButton.interactable = true;
        // 完了だけ表示
        var name = string.IsNullOrEmpty(store.CurrentPath) ? "(不明)" : Path.GetFileName(store.CurrentPath);
        SetStatus($"読み込み完了：{name} / {store.Duration:0.000}s");
    }

    // WebGL 失敗/キャンセル: こちらに来る
    void OnStoreLoadFailed(string reason)
    {
        if (openButton) openButton.interactable = true;
        SetStatus($"読み込み失敗：{reason}", true);
    }
}
