using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public class UIShowHide : MonoBehaviour
{
    [Header("Initial State")]
    [Tooltip("再生開始時に非表示にするか")]
    public bool startHidden = false;

    [Header("Toggle Settings")]
    [Tooltip("キーボードで表示/非表示をトグルするキー。使わない場合は None に")]
    public KeyCode toggleKey = KeyCode.F1;
    [Tooltip("このメソッドをボタン onClick に割り当てるとトグルできます")]
    public Button optionalToggleButton; // 任意

    [Header("Fade")]
    public bool useFade = true;
    [Tooltip("フェード時間(秒)")]
    [Range(0f, 2f)] public float fadeDuration = 0.2f;
    [Tooltip("フェードに Timescale の影響を受けない（ポーズ中もフェード）")]
    public bool useUnscaledTime = true;

    [Header("Raycast & Interactable")]
    [Tooltip("非表示中はボタン操作を受け付けない")]
    public bool controlInteractable = true;
    [Tooltip("非表示中はクリックをブロックしない")]
    public bool controlBlocksRaycasts = true;

    [Header("Advanced")]
    [Tooltip("完全に非表示時は GameObject を無効化する（フェード終わり/開始前で切替）")]
    public bool deactivateWhenHidden = false;

    [Header("Events")]
    public UnityEvent onShown;
    public UnityEvent onHidden;

    CanvasGroup _cg;
    Coroutine _fadeCo;
    bool _isVisible;

    // 外部から参照できる状態
    public bool IsVisible => _isVisible;

    void Awake()
    {
        _cg = GetComponent<CanvasGroup>();
        if (optionalToggleButton) optionalToggleButton.onClick.AddListener(Toggle);
    }

    void Start()
    {
        // 初期状態反映
        if (startHidden) ApplyHidden(immediate:true);
        else ApplyShown(immediate:true);
    }

    void Update()
    {
        if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
            Toggle();
    }

    // --------- Public API（UIから割り当て可） ---------
    public void Toggle()
    {
        if (_isVisible) Hide();
        else Show();
    }
    public void Show()
    {
        if (_fadeCo != null) StopCoroutine(_fadeCo);
        if (deactivateWhenHidden && !gameObject.activeSelf) gameObject.SetActive(true);
        _fadeCo = StartCoroutine(FadeTo(1f, onShown));
    }
    public void Hide()
    {
        if (_fadeCo != null) StopCoroutine(_fadeCo);
        _fadeCo = StartCoroutine(FadeTo(0f, onHidden, after: () =>
        {
            if (deactivateWhenHidden) gameObject.SetActive(false);
        }));
    }

    // --------- 内部実装 ---------
    IEnumerator FadeTo(float targetAlpha, UnityEvent evt, System.Action after = null)
    {
        _isVisible = targetAlpha > 0.5f;

        if (!useFade || Mathf.Approximately(fadeDuration, 0f))
        {
            _cg.alpha = targetAlpha;
            ApplyInteractability(_isVisible);
            evt?.Invoke();
            after?.Invoke();
            yield break;
        }

        ApplyInteractability(true); // フェード中は操作可（必要ならここを調整）

        float t = 0f;
        float start = _cg.alpha;
        while (t < fadeDuration)
        {
            t += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float u = Mathf.Clamp01(t / fadeDuration);
            _cg.alpha = Mathf.Lerp(start, targetAlpha, u);
            yield return null;
        }
        _cg.alpha = targetAlpha;

        ApplyInteractability(_isVisible);
        evt?.Invoke();
        after?.Invoke();
    }

    void ApplyShown(bool immediate)
    {
        _isVisible = true;
        if (deactivateWhenHidden && !gameObject.activeSelf) gameObject.SetActive(true);
        _cg.alpha = 1f;
        ApplyInteractability(true);
    }

    void ApplyHidden(bool immediate)
    {
        _isVisible = false;
        _cg.alpha = 0f;
        ApplyInteractability(false);
        if (deactivateWhenHidden) gameObject.SetActive(false);
    }

    void ApplyInteractability(bool enable)
    {
        if (controlInteractable)     _cg.interactable   = enable;
        if (controlBlocksRaycasts)   _cg.blocksRaycasts = enable;
    }
}
