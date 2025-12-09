using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages simulation time scale for realistic hospital scenarios
/// Uses Unity's Time.timeScale to actually speed up the simulation
/// Agents walk faster, animations play faster, everything speeds up naturally
/// </summary>
public class TimeManager : MonoBehaviour {
    public static TimeManager Instance;
    
    [Header("Time Scale Settings")]
    [Tooltip("1x = real time, 10x = 10 minutes pass in 1 minute, etc.")]
    [Range(1f, 60f)]
    public float timeScale = 10f;  // Default: 10x speed (reasonable for visual clarity)
    
    [Header("Preset Time Scales")]
    public float realTimeScale = 1f;        // 1x - Real time
    public float fastScale = 5f;            // 5x - Fast but smooth
    public float veryFastScale = 10f;       // 10x - Very fast
    public float ultraFastScale = 20f;      // 20x - Maximum recommended
    
    [Header("UI References")]
    public Slider timeScaleSlider;
    public TMP_Text timeScaleText;
    public TMP_Text elapsedTimeText;
    public TMP_Text realTimeText;
    public Button realTimeButton;
    public Button fastButton;
    public Button veryFastButton;
    public Button ultraFastButton;
    
    [Header("Simulation State")]
    public bool isSimulationRunning = false;
    float simulationStartTime = 0f;
    float totalRealSecondsElapsed = 0f;
    float lastUnscaledTime = 0f;
    
    // Static access to time scale
    public static float TimeScale => Instance != null ? Instance.timeScale : 10f;
    
    void Awake() {
        Instance = this;
    }
    
    void Start() {
        // Setup slider (optional - can be null)
        if (timeScaleSlider != null) {
            timeScaleSlider.minValue = 1f;
            timeScaleSlider.maxValue = 60f;
            timeScaleSlider.value = timeScale;
            timeScaleSlider.onValueChanged.AddListener(OnTimeScaleChanged);
        }
        
        // Setup buttons (optional - can be null)
        if (realTimeButton != null) realTimeButton.onClick.AddListener(() => SetTimeScale(realTimeScale));
        if (fastButton != null) fastButton.onClick.AddListener(() => SetTimeScale(fastScale));
        if (veryFastButton != null) veryFastButton.onClick.AddListener(() => SetTimeScale(veryFastScale));
        if (ultraFastButton != null) ultraFastButton.onClick.AddListener(() => SetTimeScale(ultraFastScale));
        
        // Apply initial time scale
        ApplyTimeScale();
        UpdateTimeScaleDisplay();
    }
    
    /// <summary>
    /// Public method to set time scale programmatically (useful if no UI slider)
    /// </summary>
    public void SetTimeScalePublic(float scale) {
        SetTimeScale(scale);
    }
    
    void Update() {
        if (isSimulationRunning) {
            // Track game time elapsed (scaled by Time.timeScale automatically via Time.deltaTime)
            // 1 game second = 1 hospital minute for display purposes
            // So game seconds elapsed = hospital minutes elapsed
            totalRealSecondsElapsed += Time.deltaTime;  // This is already scaled by Time.timeScale
            UpdateTimeDisplay();
        }
    }
    
    /// <summary>
    /// Apply time scale to Unity's Time.timeScale
    /// This makes everything actually run faster - agents walk faster, etc.
    /// </summary>
    void ApplyTimeScale() {
        Time.timeScale = timeScale;
        // Adjust fixed delta time to maintain physics stability at high speeds
        Time.fixedDeltaTime = 0.02f * timeScale;
        Debug.Log($"Time scale applied: {timeScale}x (Unity Time.timeScale = {Time.timeScale})");
    }
    
    /// <summary>
    /// Start tracking simulation time and apply time scale
    /// </summary>
    public void StartSimulation() {
        isSimulationRunning = true;
        simulationStartTime = Time.time;
        lastUnscaledTime = Time.unscaledTime;
        totalRealSecondsElapsed = 0f;
        ApplyTimeScale();  // Apply speed when simulation starts
        UpdateTimeDisplay();
    }
    
    /// <summary>
    /// Stop tracking simulation time and reset to normal speed
    /// </summary>
    public void StopSimulation() {
        isSimulationRunning = false;
        // Reset to normal speed when simulation stops
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
    }
    
    /// <summary>
    /// Reset simulation time
    /// </summary>
    public void ResetSimulation() {
        totalRealSecondsElapsed = 0f;
        lastUnscaledTime = Time.unscaledTime;
        UpdateTimeDisplay();
    }
    
    /// <summary>
    /// Convert real seconds to game seconds based on time scale
    /// Since Unity's timeScale handles this automatically, this just returns the value
    /// (Treatment times are in real seconds, Unity speeds them up automatically)
    /// </summary>
    public static float RealToGameSeconds(float realSeconds) {
        // With Unity timeScale applied, we just use real seconds directly
        // Unity will speed up the WaitForSeconds automatically
        return realSeconds;
    }
    
    /// <summary>
    /// Convert game seconds to real seconds
    /// </summary>
    public static float GameToRealSeconds(float gameSeconds) {
        return gameSeconds * TimeScale;
    }
    
    /// <summary>
    /// Get hospital time elapsed in minutes (1 game second = 1 hospital minute)
    /// </summary>
    public float GetHospitalMinutesElapsed() {
        return totalRealSecondsElapsed;  // Game seconds = hospital minutes
    }
    
    /// <summary>
    /// Get total real time elapsed in simulation (actual wall clock seconds)
    /// </summary>
    public float GetRealTimeElapsed() {
        return Time.time - simulationStartTime;
    }
    
    /// <summary>
    /// Get formatted hospital time string (HH:MM)
    /// Game seconds are displayed as hospital minutes
    /// </summary>
    public string GetRealTimeFormatted() {
        // totalRealSecondsElapsed = game seconds = hospital minutes
        float hospitalMinutes = totalRealSecondsElapsed;
        int hours = Mathf.FloorToInt(hospitalMinutes / 60f);
        int minutes = Mathf.FloorToInt(hospitalMinutes % 60f);
        
        if (hours > 0) {
            return $"{hours}h {minutes:D2}m";
        }
        return $"{minutes} min";
    }
    
    /// <summary>
    /// Get game time elapsed since simulation start (Unity scaled time)
    /// </summary>
    public float GetGameTimeElapsed() {
        return Time.time - simulationStartTime;
    }
    
    void SetTimeScale(float scale) {
        timeScale = Mathf.Clamp(scale, 1f, 60f);
        if (timeScaleSlider != null) timeScaleSlider.value = timeScale;
        if (isSimulationRunning) {
            ApplyTimeScale();  // Apply immediately if simulation is running
        }
        UpdateTimeScaleDisplay();
    }
    
    void OnTimeScaleChanged(float value) {
        timeScale = Mathf.Clamp(value, 1f, 60f);
        if (isSimulationRunning) {
            ApplyTimeScale();  // Apply immediately if simulation is running
        }
        UpdateTimeScaleDisplay();
    }
    
    void UpdateTimeScaleDisplay() {
        if (timeScaleText != null) {
            string speedLabel = GetSpeedLabel();
            timeScaleText.text = $"Speed: {timeScale:F0}x ({speedLabel})";
        }
    }
    
    string GetSpeedLabel() {
        if (timeScale <= 1.5f) return "Real Time";
        if (timeScale <= 15f) return "Fast";
        if (timeScale <= 45f) return "Very Fast";
        if (timeScale <= 75f) return "Ultra Fast";
        return "Maximum";
    }
    
    void UpdateTimeDisplay() {
        // Show simulated real time (what would be on a hospital clock)
        if (realTimeText != null) {
            realTimeText.text = $"Hospital Time: {GetRealTimeFormatted()}";
        }
        
        // Show actual elapsed game time
        if (elapsedTimeText != null) {
            float gameElapsed = Time.time - simulationStartTime;
            elapsedTimeText.text = $"Simulation: {FormatTime(gameElapsed)} (game)";
        }
    }
    
    /// <summary>
    /// Format seconds into HH:MM:SS or MM:SS string
    /// </summary>
    public static string FormatTime(float totalSeconds) {
        int hours = Mathf.FloorToInt(totalSeconds / 3600f);
        int minutes = Mathf.FloorToInt((totalSeconds % 3600f) / 60f);
        int seconds = Mathf.FloorToInt(totalSeconds % 60f);
        
        if (hours > 0) {
            return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
        }
        return $"{minutes:D2}:{seconds:D2}";
    }
    
    /// <summary>
    /// Format minutes into readable string (e.g., "15 min", "1h 30min")
    /// </summary>
    public static string FormatMinutes(float minutes) {
        if (minutes < 60) {
            return $"{minutes:F0} min";
        }
        int hours = Mathf.FloorToInt(minutes / 60f);
        int mins = Mathf.FloorToInt(minutes % 60f);
        if (mins == 0) {
            return $"{hours}h";
        }
        return $"{hours}h {mins}min";
    }
}

