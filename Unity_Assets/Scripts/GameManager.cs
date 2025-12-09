using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Main game controller
/// - Takes natural language input describing patients
/// - Sends to server for analysis
/// - Spawns patients and executes treatment plan
/// - Coordinates nurse and doctor for full pathway execution
/// </summary>
public class GameManager : MonoBehaviour {
    public static GameManager Instance;
    
    [Header("Agents")]
    public AgentController nurse;
    public AgentController doctor;
    
    [Header("UI References")]
    public TMP_InputField scenarioInputField;
    public Button sendButton;
    public Button saveAnalysisButton;
    public TMP_Text statusText;
    public TMP_Text strategyText;
    public TMP_Text queueText;
    public TMP_Text learningText;
    public TMP_Text scenarioTimeText;  // Shows elapsed time for current scenario
    
    [Header("Time Manager")]
    public TimeManager timeManager;  // Reference to TimeManager component (should be on empty GameObject)
    
    [Header("Patient Spawning")]
    public GameObject patientPrefab;
    public Transform spawnPoint;      // ENT position
    public Transform waitingArea;     // WAITING area position
    
    [Header("State")]
    public bool isProcessing = false;
    public List<PatientController> activePatients = new List<PatientController>();
    int scenarioCount = 0;
    bool chartsGeneratedForCurrentScenario = false;
    
    [Header("Visual Settings")]
    public Color minorColor = Color.green;
    public Color moderateColor = new Color(1f, 0.8f, 0f); // Orange-yellow
    public Color criticalColor = Color.red;
    
    void Awake() => Instance = this;
    
    void Start() {
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (saveAnalysisButton != null) saveAnalysisButton.onClick.AddListener(OnSaveAnalysisClicked);
        
        if (nurse != null) nurse.OnActionComplete += OnAgentActionComplete;
        if (doctor != null) doctor.OnActionComplete += OnAgentActionComplete;
        
        // Find TimeManager if not assigned (should be on empty GameObject)
        if (timeManager == null) {
            timeManager = FindFirstObjectByType<TimeManager>();
            if (timeManager == null) {
                Debug.LogWarning("TimeManager not found! Create an empty GameObject and add TimeManager component to it.");
            }
        }
        
        // Find spawn point if not assigned
        if (spawnPoint == null) {
            OOI ent = OOIManager.Instance?.GetOOI("ENT");
            if (ent != null) spawnPoint = ent.transform;
        }
        
        // Find waiting area if not assigned
        if (waitingArea == null) {
            OOI waiting = OOIManager.Instance?.GetOOI("WAITING");
            if (waiting != null) waitingArea = waiting.transform;
        }
        
        UpdateStatus("Ready. Describe patients in natural language and click Send.");
        UpdateScenarioTimeDisplay();  // Initialize time display
        
        // Initial server reset
        ServerCommunication.Instance?.ResetServer(null);
    }
    
    void Update() {
        // Update scenario time display every frame when simulation is running
        if (isProcessing || (timeManager != null && timeManager.isSimulationRunning)) {
            UpdateScenarioTimeDisplay();
        }
    }
    
    void OnSendClicked() {
        if (isProcessing) {
            UpdateStatus("Already processing. Please wait.");
            return;
        }
        
        string description = scenarioInputField?.text;
        if (string.IsNullOrEmpty(description)) {
            UpdateStatus("Please describe the patients.");
            return;
        }
        
        isProcessing = true;
        scenarioCount++;
        chartsGeneratedForCurrentScenario = false; // Reset flag for new scenario
        
        // Reset time tracking for new scenario
        TimeManager tm = timeManager != null ? timeManager : TimeManager.Instance;
        if (tm != null) {
            tm.ResetSimulation();
            tm.StopSimulation();  // Stop any previous simulation
            UpdateScenarioTimeDisplay();  // Reset display
        }
        
        UpdateStatus($"Analyzing scenario #{scenarioCount} with LLM...");
        
        // Clear previous patients
        ClearAllPatients();
        
        // Reset agents
        if (nurse != null) nurse.ClearCommands();
        if (doctor != null) doctor.ClearCommands();
        
        // Send to server
        ServerCommunication.Instance?.ProcessScenario(description, OnScenarioProcessed);
    }
    
    void ClearAllPatients() {
        foreach (var p in activePatients) {
            if (p != null) Destroy(p.gameObject);
        }
        activePatients.Clear();
    }
    
    void OnScenarioProcessed(ScenarioResponse response) {
        if (response == null) {
            UpdateStatus("Error: Server connection failed.");
            isProcessing = false;
            return;
        }
        
        if (!response.success) {
            UpdateStatus($"Error: {response.error}");
            isProcessing = false;
            return;
        }
        
        // Clear input
        if (scenarioInputField != null) scenarioInputField.text = "";
        
        // Spawn patients from response
        StartCoroutine(SpawnAndExecuteSequence(response));
    }
    
    IEnumerator SpawnAndExecuteSequence(ScenarioResponse response) {
        UpdateStatus($"Spawning {response.patient_count} patients...");
        
        // Start time tracking - use reference if available, otherwise use static instance
        TimeManager tm = timeManager != null ? timeManager : TimeManager.Instance;
        if (tm != null) {
            tm.ResetSimulation();
            tm.StartSimulation();
            UpdateScenarioTimeDisplay();  // Initialize display
        } else {
            Debug.LogWarning("TimeManager not found! Time tracking disabled.");
        }
        
        // Spawn each patient at entrance
        foreach (var patientInfo in response.patients) {
            SpawnPatientVisual(patientInfo);
            yield return new WaitForSeconds(0.3f);
        }
        
        // Wait for patients to reach waiting area
        yield return new WaitForSeconds(1.5f);
        
        // Display strategy info with time scale
        DisplayStrategy(response);
        
        // Execute the FULL pathway for all patients
        Debug.Log("=== EXECUTING TREATMENT PLAN ===");
        Debug.Log($"Strategy: {response.assignment?.strategy}");
        Debug.Log($"Nurse Commands: {response.nurse_plan?.commands?.Length ?? 0}");
        Debug.Log($"Doctor Commands: {response.doctor_plan?.commands?.Length ?? 0}");
        
        // Log commands
        if (response.nurse_plan?.commands != null) {
            foreach (var cmd in response.nurse_plan.commands) {
                Debug.Log($"  Nurse: {cmd.action} → {cmd.target} (Patient #{cmd.patient_id})");
            }
        }
        if (response.doctor_plan?.commands != null) {
            foreach (var cmd in response.doctor_plan.commands) {
                Debug.Log($"  Doctor: {cmd.action} → {cmd.target} (Patient #{cmd.patient_id})");
            }
        }
        
        // Execute plans - both agents work simultaneously
        ExecuteAgentPlan(nurse, response.nurse_plan);
        ExecuteAgentPlan(doctor, response.doctor_plan);
        
        isProcessing = false;
    }
    
    void SpawnPatientVisual(PatientInfo info) {
        if (patientPrefab == null) {
            Debug.LogError("Patient prefab not assigned!");
            return;
        }
        
        // Spawn at entrance with slight offset
        Vector3 spawnPos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        spawnPos += new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), 0, UnityEngine.Random.Range(-0.3f, 0.3f));
        
        GameObject patientObj = Instantiate(patientPrefab, spawnPos, Quaternion.identity);
        patientObj.name = $"Patient_{info.id}_{info.type}";
        
        PatientController patient = patientObj.GetComponent<PatientController>();
        if (patient == null) patient = patientObj.AddComponent<PatientController>();
        
        // Configure patient
        patient.patientId = info.id;
        patient.patientType = info.type;
        patient.pathway = info.pathway;
        patient.deadline = info.deadline;
        patient.doctorRequired = info.doctor_required;
        patient.state = PatientState.Spawning;
        
        // Apply color based on severity
        ApplyPatientColor(patientObj, info.type);
        
        // Register callbacks
        patient.OnTreatmentComplete += OnPatientTreatmentComplete;
        patient.OnExitComplete += OnPatientExit;
        
        activePatients.Add(patient);
        
        // Move to waiting area
        if (waitingArea != null) {
            patient.MoveToWaitingArea(waitingArea.position);
        }
        
        Debug.Log($"Patient #{info.id} ({info.type}) spawned: {info.description}");
        Debug.Log($"  Pathway: {string.Join(" → ", info.pathway)}");
        Debug.Log($"  Deadline: {info.deadline}s | Doctor Required: {info.doctor_required}");
    }
    
    void ApplyPatientColor(GameObject patientObj, string ptype) {
        Renderer renderer = patientObj.GetComponentInChildren<Renderer>();
        if (renderer == null) return;
        
        Color color = ptype switch {
            "Minor" => minorColor,
            "Moderate" => moderateColor,
            "Critical" => criticalColor,
            _ => Color.white
        };
        
        renderer.material.color = color;
    }
    
    void DisplayStrategy(ScenarioResponse response) {
        if (response.assignment != null) {
            float timeScale = TimeManager.TimeScale;
            UpdateStatus($"Executing: {response.assignment.strategy} @ {timeScale}x speed");
            
            if (strategyText != null) {
                // Calculate estimated real time for the scenario
                string treatmentTimes = GetTreatmentTimesInfo();
                
                strategyText.text = $"<b>Strategy:</b> {response.assignment.strategy}\n\n" +
                                   $"{response.assignment.description}\n\n" +
                                   $"<b>Time Scale:</b> {timeScale}x (1 real min = {60f/timeScale:F1}s game)\n\n" +
                                   $"<b>Treatment Durations (Real Time):</b>\n{treatmentTimes}\n\n" +
                                   $"<b>Expected Reward:</b> {response.expected_reward:F2}";
            }
        }
        
        // Update learning stats
        if (response.learning_stats != null && learningText != null) {
            var stats = response.learning_stats;
            learningText.text = $"<b>Online Learning (UCB1)</b>\n" +
                               $"Rounds: {stats.total_rounds}\n" +
                               $"Cumulative Regret: {stats.cumulative_regret:F2}\n" +
                               $"Avg Regret/Round: {stats.avg_regret_per_round:F4}";
        }
        
        UpdateQueueDisplay();
    }
    
    string GetTreatmentTimesInfo() {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        float timeScale = TimeManager.TimeScale;
        foreach (var kvp in AgentController.RoomTreatmentSeconds) {
            float gameSeconds = kvp.Value;  // Game seconds
            float hospitalMinutes = gameSeconds * AgentController.GAME_SECONDS_TO_HOSPITAL_MINUTES;
            float realSeconds = gameSeconds / timeScale;  // Actual wall-clock seconds
            bool agentRequired = AgentController.AgentRequiredRooms.Contains(kvp.Key);
            string agentNote = agentRequired ? "(agent stays)" : "(agent leaves)";
            sb.AppendLine($"  {kvp.Key}: {hospitalMinutes:F0}min = {realSeconds:F1}s real {agentNote}");
        }
        return sb.ToString().TrimEnd();
    }
    
    void ExecuteAgentPlan(AgentController agent, AgentPlan plan) {
        if (agent == null) return;
        if (plan == null || plan.commands == null || plan.commands.Length == 0) {
            Debug.Log($"{agent.role}: No commands to execute");
            return;
        }
        
        Debug.Log($"{agent.role}: Enqueuing {plan.commands.Length} commands");
        
        foreach (var cmd in plan.commands) {
            agent.EnqueueCommand(cmd);
        }
    }
    
    void OnAgentActionComplete(string agentRole, string action, int patientId) {
        Debug.Log($"[{agentRole}] Completed: {action} (Patient #{patientId})");
        UpdateQueueDisplay();
        
        // Notify server of step completion if it was a TREAT action
        if (action == "TREAT" && patientId > 0) {
            ServerCommunication.Instance?.CompletePatientStep(patientId, null);
        }
        
        // Check if all work is done
        CheckAllWorkComplete();
    }
    
    void CheckAllWorkComplete() {
        bool nurseIdle = nurse == null || !nurse.IsBusy();
        bool doctorIdle = doctor == null || !doctor.IsBusy();
        
        if (nurseIdle && doctorIdle) {
            // Remove null references (patients that have been destroyed)
            activePatients.RemoveAll(p => p == null);
            
            // Check if any patients are still active (being treated, following, or exiting)
            bool patientsActive = false;
            foreach (var p in activePatients) {
                if (p != null) {
                    if (p.state == PatientState.InTreatment || 
                        p.state == PatientState.Following || 
                        p.state == PatientState.Exiting ||
                        p.state == PatientState.Waiting) {
                        patientsActive = true;
                        break;
                    }
                }
            }
            
            // ALL patients must be gone (exited and disappeared) before generating charts
            if (!patientsActive && activePatients.Count == 0 && !chartsGeneratedForCurrentScenario) {
                chartsGeneratedForCurrentScenario = true;
                
                // Stop time tracking and log final time
                TimeManager tm = timeManager != null ? timeManager : TimeManager.Instance;
                if (tm != null) {
                    tm.StopSimulation();
                    string realTime = tm.GetRealTimeFormatted();
                    float gameTime = tm.GetGameTimeElapsed();
                    Debug.Log($"=== SIMULATION COMPLETE ===");
                    Debug.Log($"Real Time Elapsed: {realTime}");
                    Debug.Log($"Game Time Elapsed: {gameTime:F1}s");
                    UpdateStatus($"Complete! Hospital time: {realTime} (simulated in {gameTime:F1}s)");
                    UpdateScenarioTimeDisplay();  // Final update
                }
                
                Debug.Log($"=== ALL PATIENTS HAVE EXITED - GENERATING CHARTS ===");
                StartCoroutine(GenerateChartsAfterCompletion());
            }
        }
    }
    
    System.Collections.IEnumerator GenerateChartsAfterCompletion() {
        // Wait for all patients to fully exit and disappear
        UpdateStatus("All patients have exited. Finalizing scenario...");
        yield return new WaitForSeconds(1.5f);
        
        // Ensure all patients are gone
        activePatients.RemoveAll(p => p == null);
        if (activePatients.Count > 0) {
            Debug.LogWarning($"Warning: {activePatients.Count} patient(s) still in scene, waiting...");
            yield return new WaitForSeconds(1.0f);
        }
        
        UpdateStatus($"Scenario #{scenarioCount} complete! Generating regret analysis charts...");
        Debug.Log($"========================================");
        Debug.Log($"ALL PATIENTS HAVE EXITED AND DISAPPEARED");
        Debug.Log($"AUTOMATICALLY GENERATING REGRET ANALYSIS CHARTS");
        Debug.Log($"========================================");
        
        ServerCommunication.Instance?.SaveAnalysis(OnAnalysisSaved);
    }
    
    void OnPatientTreatmentComplete(PatientController patient) {
        Debug.Log($"Patient #{patient.patientId} step {patient.currentStep} treatment complete");
        UpdateQueueDisplay();
    }
    
    void OnPatientExit(PatientController patient) {
        if (patient == null) return;
        
        int patientId = patient.patientId;
        string patientType = patient.patientType;
        
        // Notify server that patient has exited
        ServerCommunication.Instance?.PatientExit(patientId, null);
        
        // Remove from active list
        activePatients.Remove(patient);
        UpdateQueueDisplay();
        
        Debug.Log($"Patient #{patientId} ({patientType}) has exited through entrance and disappeared");
        UpdateStatus($"Patient #{patientId} exited. {activePatients.Count} patient(s) remaining...");
        
        // Check if all patients are done
        StartCoroutine(DelayedCompletionCheck());
    }
    
    System.Collections.IEnumerator DelayedCompletionCheck() {
        // Wait a frame to ensure patient is destroyed
        yield return null;
        CheckAllWorkComplete();
    }
    
    void UpdateQueueDisplay() {
        if (queueText == null) return;
        
        if (activePatients.Count == 0) {
            queueText.text = "Queue: Empty";
            return;
        }
        
        string text = $"<b>Active Patients ({activePatients.Count})</b>\n";
        foreach (var p in activePatients) {
            if (p == null) continue;
            
            float timeLeft = p.GetTimeRemaining();
            string urgentIcon = timeLeft < 10 ? " [!]" : (timeLeft < 20 ? " [T]" : "");
            string stateIcon = p.state switch {
                PatientState.Waiting => "[W]",
                PatientState.Following => "[F]",
                PatientState.InTreatment => "[T]",
                PatientState.Exiting => "[E]",
                _ => "[-]"
            };
            
            string next = p.GetNextRoom() ?? "EXIT";
            text += $"{stateIcon} #{p.patientId} {p.patientType} > {next}{urgentIcon}\n";
        }
        
        // Add agent status
        text += $"\n<b>Agents</b>\n";
        if (nurse != null) text += $"Nurse: {nurse.state} @ {nurse.currentRoom}\n";
        if (doctor != null) text += $"Doctor: {doctor.state} @ {doctor.currentRoom}\n";
        
        queueText.text = text;
    }
    
    void OnSaveAnalysisClicked() {
        UpdateStatus("Saving regret analysis charts...");
        ServerCommunication.Instance?.SaveAnalysis(OnAnalysisSaved);
    }
    
    void OnAnalysisSaved(SaveAnalysisResponse response) {
        if (response == null) {
            UpdateStatus("Error: Failed to save analysis. Check server connection.");
            Debug.LogError("SaveAnalysisResponse is null - check server connection");
            return;
        }
        
        if (response.saved) {
            string chartFileName = System.IO.Path.GetFileName(response.chart_path);
            string dataFileName = System.IO.Path.GetFileName(response.data_path);
            string outputDir = System.IO.Path.GetDirectoryName(response.chart_path);
            
            UpdateStatus($"Scenario #{scenarioCount} complete! Charts saved to server/{outputDir}/");
            Debug.Log($"========================================");
            Debug.Log($"REGRET ANALYSIS CHARTS GENERATED!");
            Debug.Log($"========================================");
            Debug.Log($"Chart Image: {chartFileName}");
            Debug.Log($"Data File: {dataFileName}");
            Debug.Log($"Location: server/{outputDir}/");
            Debug.Log($"Total Learning Rounds: {response.total_rounds}");
            Debug.Log($"Final Cumulative Regret: {response.final_cumulative_regret:F4}");
            Debug.Log($"========================================");
            Debug.Log($"Charts are saved in the 'server' folder:");
            Debug.Log($"  - Open: server/{outputDir}/{chartFileName}");
            Debug.Log($"  - Data: server/{outputDir}/{dataFileName}");
            Debug.Log($"========================================");
        } else {
            UpdateStatus($"Chart generation failed: {response.reason ?? "Unknown error"}");
            Debug.LogWarning($"Chart generation failed: {response.reason}");
        }
    }
    
    void UpdateStatus(string message) {
        if (statusText != null) statusText.text = message;
        Debug.Log(message);
    }
    
    /// <summary>
    /// Updates the scenario time display showing elapsed hospital time
    /// </summary>
    void UpdateScenarioTimeDisplay() {
        if (scenarioTimeText == null) return;
        
        TimeManager tm = timeManager != null ? timeManager : TimeManager.Instance;
        if (tm == null) {
            scenarioTimeText.text = "Time: --:--";
            return;
        }
        
        if (tm.isSimulationRunning) {
            string realTime = tm.GetRealTimeFormatted();
            float timeScale = tm.timeScale;
            scenarioTimeText.text = $"Hospital Time: <b>{realTime}</b>\n" +
                                   $"Speed: {timeScale:F0}x";
        } else {
            // Show last recorded time or default
            if (tm.GetRealTimeElapsed() > 0) {
                string lastTime = tm.GetRealTimeFormatted();
                scenarioTimeText.text = $"Hospital Time: <b>{lastTime}</b> (Complete)";
            } else {
                scenarioTimeText.text = "Hospital Time: 00:00";
            }
        }
    }
    
    /// <summary>
    /// Get patient by ID for agent escort
    /// </summary>
    public PatientController GetPatient(int id) {
        return activePatients.Find(p => p != null && p.patientId == id);
    }
}

