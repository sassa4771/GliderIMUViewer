using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PosePlaybackController : MonoBehaviour
{
    [Header("Refs")]
    public PoseCsvStore store;
    public Button playPauseButton;     // 1ボタン
    public Button rewindButton;        // 任意
    public Slider timeSlider;          // 自動で 0..Duration
    public Toggle loopToggle;          // ループON/OFF
    public TMP_Text timeLabel;         // "1.234 / 12.345 s"

    [Header("UI (TMP)")]
    public TMP_Text playPauseText;     // ボタン表示
    public string labelPlay = "再生";
    public string labelPause = "一時停止";
    public Image playPauseIcon;        // 任意
    public Sprite iconPlay;
    public Sprite iconPause;

    [Header("Behavior")]
    public float playbackSpeed = 1f;
    public bool autoPlayOnLoad = true;
    public bool autoPauseOnSeek = false;
    public enum StopMode { Pause, PauseAndRewind }
    public StopMode stopMode = StopMode.Pause;

    // 状態
    public bool IsPlaying { get; private set; } = false;
    public float TimeSec  { get; private set; } = 0f;
    public bool Loop      { get; private set; } = true;

    void Awake()
    {
        if (playPauseButton) playPauseButton.onClick.AddListener(OnPlayPauseClicked);
        if (rewindButton)    rewindButton.onClick.AddListener(OnRewindClicked);

        if (timeSlider)
        {
            timeSlider.wholeNumbers = false;
            timeSlider.onValueChanged.AddListener(OnSliderChanged);
        }

        if (loopToggle)
        {
            loopToggle.isOn = true;
            loopToggle.onValueChanged.AddListener(v => Loop = v);
        }
    }

    void OnEnable()
    {
        if (store) store.Loaded += OnStoreLoaded;
    }
    void OnDisable()
    {
        if (store) store.Loaded -= OnStoreLoaded;
    }

    void OnStoreLoaded()
    {
        TimeSec = 0f;
        if (timeSlider)
        {
            timeSlider.minValue = 0f;
            timeSlider.maxValue = Mathf.Max(0.0001f, store.Duration);
            timeSlider.SetValueWithoutNotify(0f);
        }
        Loop = loopToggle ? loopToggle.isOn : true;
        IsPlaying = autoPlayOnLoad;
        UpdateUI();
    }

    void Update()
    {
        if (store == null || !store.IsLoaded) return;

        if (IsPlaying)
        {
            TimeSec += Time.deltaTime * Mathf.Max(0f, playbackSpeed);
            if (TimeSec > store.Duration)
            {
                if (Loop) TimeSec -= store.Duration;
                else { TimeSec = store.Duration; IsPlaying = false; }
            }
            UpdateUI();
        }

        if (Input.GetKeyDown(KeyCode.Space)) OnPlayPauseClicked();
    }

    void OnPlayPauseClicked()
    {
        if (store == null || !store.IsLoaded) return;

        if (IsPlaying)
        {
            IsPlaying = false;
            if (stopMode == StopMode.PauseAndRewind) TimeSec = 0f;
        }
        else
        {
            if (TimeSec >= store.Duration) TimeSec = 0f;
            IsPlaying = true;
        }
        UpdateUI();
    }

    void OnRewindClicked()
    {
        TimeSec = 0f;
        UpdateUI();
    }

    void OnSliderChanged(float v)
    {
        if (store == null || !store.IsLoaded) return;
        TimeSec = Mathf.Clamp(v, 0f, store.Duration);
        if (autoPauseOnSeek) IsPlaying = false;
        UpdateUI(showToSlider:false);
    }

    void UpdateUI(bool showToSlider = true)
    {
        if (timeSlider && showToSlider)
            timeSlider.SetValueWithoutNotify(TimeSec);

        if (timeLabel)
            timeLabel.text = $"{TimeSec:0.000} / {store.Duration:0.000} s";

        if (playPauseText)
            playPauseText.text = IsPlaying ? labelPause : labelPlay;

        if (playPauseIcon)
            playPauseIcon.sprite = IsPlaying ? iconPause : iconPlay;
    }
}
