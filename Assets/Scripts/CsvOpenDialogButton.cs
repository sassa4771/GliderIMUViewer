using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CsvOpenDialogButton : MonoBehaviour
{
    [Header("Refs")]
    public PoseCsvStore store;          // ← loadOnStart=false にして割当
    public Button openButton;           // ← これを押すとダイアログ
    public TMP_Text statusLabel;        // 読込結果やエラー表示
    public TMP_Text fileNameLabel;      // 選択したファイル名（任意）

    [Header("Filters")]
    public bool requireCsvExtension = true;

    void Awake()
    {
        if (openButton) openButton.onClick.AddListener(OpenDialog);
        if (store != null)
        {
            store.Loaded     += OnStoreLoaded;
            store.LoadFailed += OnStoreLoadFailed;
        }
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

    void OpenDialog()
    {
        if (store == null) { SetStatus("PoseCsvStore が未割り当てです。", true); return; }
        if (openButton) openButton.interactable = false;

        string selectedPath = null;

#if SFB
        // Standalone File Browser（ビルドで使用）
        var lastDir = PoseCsvStore.GetLastDirOrDefault();
        var extensions = new[] { new SFB.ExtensionFilter("CSV", "csv"), new SFB.ExtensionFilter("All Files", "*") };
        var paths = SFB.StandaloneFileBrowser.OpenFilePanel("CSVを選択", lastDir, extensions, false);
        if (paths != null && paths.Length > 0) selectedPath = paths[0];
#elif UNITY_EDITOR
        // エディタでは標準ダイアログ
        var lastDir = PoseCsvStore.GetLastDirOrDefault();
        selectedPath = UnityEditor.EditorUtility.OpenFilePanel("CSVを選択", lastDir, "csv");
#else
        SetStatus("このプラットフォームではファイルダイアログ未対応です。SFB等のプラグインをご利用ください。", true);
#endif

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
            store.LoadFromAbsolutePath(selectedPath); // 成否はイベントで通知
            if (fileNameLabel) fileNameLabel.text = Path.GetFileName(selectedPath);
            SetStatus("読み込み中…");
        }
        catch (System.Exception ex)
        {
            SetStatus($"例外: {ex.Message}", true);
            if (openButton) openButton.interactable = true;
        }
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
