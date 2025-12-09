using UnityEngine;
using UnityEngine.AI;
using System.Collections;
using System.Collections.Generic;

public enum AgentRole { Nurse, Doctor }
public enum AgentState { Idle, Moving, Escorting, Treating, Waiting }

/// <summary>
/// Controls agent movement and patient escort
/// Supports new queue-based system with accompany logic
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AgentController : MonoBehaviour {
    // Room-specific treatment times in GAME SECONDS
    // These values represent hospital minutes: 1 game second = 1 hospital minute
    // Server regret engine uses REAL hospital times (5, 15, 20, 45 MINUTES) for calculations
    // Unity displays these as minutes but executes them as game seconds
    // At 10x Time.timeScale, 5 game seconds = 0.5 real seconds (but displays as "5 min hospital time")
    public static readonly Dictionary<string, float> RoomTreatmentSeconds = new Dictionary<string, float> {
        { "TRIAGE", 5.0f },    // 5 game seconds = 5 hospital minutes (server uses 5 min for regret calc)
        { "TB", 15.0f },       // 15 game seconds = 15 hospital minutes (server uses 15 min)
        { "LAB", 20.0f },      // 20 game seconds = 20 hospital minutes (server uses 20 min)
        { "ICU", 45.0f }       // 45 game seconds = 45 hospital minutes (server uses 45 min)
    };
    
    // Conversion: 1 game second = 1 hospital minute (for display purposes)
    public static readonly float GAME_SECONDS_TO_HOSPITAL_MINUTES = 1.0f;
    
    // ICU setup time in GAME SECONDS (agent stays this long, then can leave)
    public static readonly float ICU_SETUP_SECONDS = 5.0f;  // 5 game seconds (represents 5 hospital minutes)
    
    // Rooms where agent MUST stay during entire treatment (cannot leave)
    public static readonly HashSet<string> AgentRequiredRooms = new HashSet<string> {
        "TRIAGE",  // Nurse must assess
        "TB",      // Active treatment required
        "LAB"      // Sample collection requires presence
    };
    // Note: ICU allows agent to leave after initial setup
    
    [Header("Agent Configuration")]
    public AgentRole role = AgentRole.Nurse;
    public float nurseSpeed = 4f;
    public float doctorSpeed = 2.5f;
    
    [Header("Animation")]
    public Animator animator;
    public string walkParam = "IsWalking";
    public string treatParam = "IsTreating";
    
    [Header("Current State")]
    public AgentState state = AgentState.Idle;
    public PatientController currentPatient;
    public string currentRoom = "ENT";
    
    NavMeshAgent nav;
    Queue<AgentCommand> commandQueue = new Queue<AgentCommand>();
    bool isExecuting = false;
    
    public System.Action<string, string, int> OnActionComplete; // (agentRole, action, patientId)
    
    void Start() {
        nav = GetComponent<NavMeshAgent>();
        nav.speed = role == AgentRole.Nurse ? nurseSpeed : doctorSpeed;
        if (animator == null) animator = GetComponent<Animator>();
        
        StartCoroutine(EnsureOnNavMesh());
    }
    
    IEnumerator EnsureOnNavMesh() {
        yield return null;
        
        NavMeshHit hit;
        if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas)) {
            if (!nav.isOnNavMesh) {
                nav.Warp(hit.position);
            }
        }
    }
    
    void Update() {
        UpdateAnimation();
        
        if (!isExecuting && commandQueue.Count > 0) {
            StartCoroutine(ExecuteCommand(commandQueue.Dequeue()));
        }
    }
    
    void UpdateAnimation() {
        if (animator == null) return;
        
        float speed = nav.isOnNavMesh ? nav.velocity.magnitude : 0f;
        bool isMoving = speed > 0.1f;
        
        if (HasParameter("Speed", AnimatorControllerParameterType.Float) && nav.speed > 0f) {
            animator.SetFloat("Speed", Mathf.Clamp01(speed / nav.speed));
        }
        
        if (HasParameter(walkParam, AnimatorControllerParameterType.Bool)) {
            animator.SetBool(walkParam, isMoving);
        }
    }
    
    bool HasParameter(string paramName, AnimatorControllerParameterType paramType) {
        if (animator == null) return false;
        foreach (AnimatorControllerParameter param in animator.parameters) {
            if (param.name == paramName && param.type == paramType) return true;
        }
        return false;
    }
    
    public void EnqueueCommand(AgentCommand cmd) {
        commandQueue.Enqueue(cmd);
    }
    
    public void ClearCommands() {
        commandQueue.Clear();
        StopAllCoroutines();
        isExecuting = false;
        state = AgentState.Idle;
        
        if (currentPatient != null) {
            currentPatient.StopFollowing();
            currentPatient = null;
        }
        
        if (nav != null && nav.isOnNavMesh) {
            nav.ResetPath();
        }
    }
    
    IEnumerator ExecuteCommand(AgentCommand cmd) {
        isExecuting = true;
        int patientId = cmd.patient_id;
        
        // Validate patient exists for patient-related commands BEFORE executing
        if (cmd.action == "ESCORT" || cmd.action == "TREAT" || cmd.action == "LEAVE_PATIENT") {
            PatientController patient = GameManager.Instance?.GetPatient(patientId);
            if (patient == null) {
                // Patient doesn't exist - skip this command silently
                Debug.Log($"[{role}] Skipping {cmd.action} for Patient #{patientId} (patient no longer exists)");
                isExecuting = false;
                yield break;
            }
        }
        
        switch (cmd.action) {
            case "MOVE":
                yield return StartCoroutine(MoveTo(cmd.target));
                break;
                
            case "ESCORT":
                yield return StartCoroutine(EscortPatient(patientId, cmd.target));
                break;
                
            case "TREAT":
                yield return StartCoroutine(TreatPatient(patientId, cmd.target, cmd.duration));
                break;
                
            case "LEAVE_PATIENT":
                LeavePatientAlone(patientId);
                break;
                
            case "WAIT":
                yield return StartCoroutine(Wait(cmd.duration));
                break;
        }
        
        OnActionComplete?.Invoke(role.ToString(), cmd.action, patientId);
        isExecuting = false;
    }
    
    IEnumerator MoveTo(string roomName) {
        state = AgentState.Moving;
        OOI target = OOIManager.Instance?.GetOOI(roomName);
        
        if (target == null) {
            Debug.LogError($"Room {roomName} not found!");
            state = AgentState.Idle;
            yield break;
        }
        
        if (!nav.isOnNavMesh) {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas)) {
                nav.Warp(hit.position);
                yield return null;
            } else {
                state = AgentState.Idle;
                yield break;
            }
        }
        
        nav.SetDestination(target.GetArrivalPosition());
        
        while (nav.pathPending) yield return null;
        
        if (nav.pathStatus == NavMeshPathStatus.PathInvalid) {
            state = AgentState.Idle;
            yield break;
        }
        
        while (nav.isOnNavMesh && nav.remainingDistance > nav.stoppingDistance + 0.1f) {
            yield return null;
        }
        
        currentRoom = roomName;
        state = AgentState.Idle;
    }
    
    IEnumerator EscortPatient(int patientId, string roomName) {
        state = AgentState.Escorting;
        
        // Find patient (should already be validated in ExecuteCommand, but double-check)
        PatientController patient = GameManager.Instance?.GetPatient(patientId);
        if (patient == null) {
            // Patient was destroyed - skip silently
            state = AgentState.Idle;
            yield break;
        }
        
        // Helper function to check if patient is still valid
        bool IsPatientValid() {
            return patient != null && patient.gameObject != null;
        }
        
        // CRITICAL: Wait if patient is already being escorted by another agent
        int waitCount = 0;
        if (IsPatientValid() && patient.state == PatientState.Following) {
            // Check if another agent is escorting this patient
            GameManager gm = GameManager.Instance;
            if (gm != null) {
                AgentController otherNurse = gm.nurse;
                AgentController otherDoctor = gm.doctor;
                
                while (IsPatientValid() && patient.state == PatientState.Following && waitCount < 60) {
                    bool beingEscorted = (otherNurse != null && otherNurse.currentPatient == patient && otherNurse != this) ||
                                        (otherDoctor != null && otherDoctor.currentPatient == patient && otherDoctor != this);
                    
                    if (beingEscorted) {
                        yield return new WaitForSeconds(0.5f);
                        waitCount++;
                        // Re-check patient validity after wait
                        if (!IsPatientValid()) {
                            Debug.LogWarning($"Patient {patientId} was destroyed while waiting for escort");
                            state = AgentState.Idle;
                            yield break;
                        }
                    } else {
                        break; // Patient is free or already with us
                    }
                }
            }
        }
        
        // Check patient validity before continuing
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before escort could start");
            state = AgentState.Idle;
            yield break;
        }
        
        // If patient is in treatment, wait for treatment to complete
        while (IsPatientValid() && patient.state == PatientState.InTreatment && waitCount < 120) {
            yield return new WaitForSeconds(0.5f);
            waitCount++;
            // Re-check patient validity after wait
            if (!IsPatientValid()) {
                Debug.LogWarning($"Patient {patientId} was destroyed during treatment wait");
                state = AgentState.Idle;
                yield break;
            }
        }
        
        // Final check before proceeding
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before escort");
            state = AgentState.Idle;
            yield break;
        }
        
        currentPatient = patient;
        
        // Ensure agent is on NavMesh
        if (!nav.isOnNavMesh) {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas)) {
                nav.Warp(hit.position);
                yield return null;
            }
        }
        
        // STEP 1: Agent walks TO the patient's current location first
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before movement");
            state = AgentState.Idle;
            yield break;
        }
        
        nav.SetDestination(patient.transform.position);
        while (nav.pathPending) yield return null;
        
        float approachDistance = 1.0f;
        int timeout = 0;
        while (IsPatientValid() && nav.isOnNavMesh && nav.remainingDistance > approachDistance && timeout < 300) {
            // Update destination in case patient moved
            if (timeout % 10 == 0) {
                if (IsPatientValid()) {
                    nav.SetDestination(patient.transform.position);
                } else {
                    Debug.LogWarning($"Patient {patientId} was destroyed during approach");
                    state = AgentState.Idle;
                    yield break;
                }
            }
            yield return null;
            timeout++;
        }
        
        // Check patient validity before interacting
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before interaction");
            state = AgentState.Idle;
            yield break;
        }
        
        // Brief pause when reaching patient
        yield return new WaitForSeconds(0.2f);
        
        // Re-check after pause
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed during pause");
            state = AgentState.Idle;
            yield break;
        }
        
        // STEP 2: Patient starts following closely (release from any other agent first)
        patient.StopFollowing(); // Release from any previous escort
        yield return new WaitForSeconds(0.1f);
        
        // Final check before starting follow
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before follow");
            state = AgentState.Idle;
            yield break;
        }
        
        patient.StartFollowing(transform, 0.8f); // Close follow distance
        
        // STEP 3: Navigate to target room together
        OOI target = OOIManager.Instance?.GetOOI(roomName);
        if (target == null) {
            if (IsPatientValid()) {
                patient.StopFollowing();
            }
            state = AgentState.Idle;
            yield break;
        }
        
        nav.SetDestination(target.GetArrivalPosition());
        while (nav.pathPending) yield return null;
        
        // Walk together - agent moves slowly so patient can keep up
        float originalSpeed = nav.speed;
        nav.speed = Mathf.Min(nav.speed, 2.5f); // Slow down for escort
        
        while (IsPatientValid() && nav.isOnNavMesh && nav.remainingDistance > nav.stoppingDistance + 0.1f) {
            yield return null;
        }
        
        nav.speed = originalSpeed; // Restore speed
        
        // Wait for patient to arrive next to agent
        yield return new WaitForSeconds(0.3f);
        
        // Final check before stopping follow
        if (IsPatientValid()) {
            patient.StopFollowing();
        }
        currentPatient = null; // Clear reference
        currentRoom = roomName;
        state = AgentState.Idle;
    }
    
    IEnumerator TreatPatient(int patientId, string roomName, float durationFromServer) {
        state = AgentState.Treating;
        
        PatientController patient = GameManager.Instance?.GetPatient(patientId);
        
        if (patient == null) {
            // Patient was destroyed - skip silently (already validated in ExecuteCommand)
            state = AgentState.Idle;
            yield break;
        }
        
        // Helper function to check if patient is still valid
        bool IsPatientValid() {
            return patient != null && patient.gameObject != null;
        }
        
        if (animator && HasParameter(treatParam, AnimatorControllerParameterType.Bool)) {
            animator.SetBool(treatParam, true);
        }
        
        // Server sends duration in MINUTES (real hospital time for regret calculations)
        // Convert to GAME SECONDS: 1 game second = 1 hospital minute
        // Use Unity's room-specific times if available, otherwise convert server duration
        float hospitalMinutes;
        float treatmentGameSeconds;
        
        if (RoomTreatmentSeconds.ContainsKey(roomName)) {
            // Use Unity's predefined time (in game seconds, represents hospital minutes)
            treatmentGameSeconds = RoomTreatmentSeconds[roomName];
            hospitalMinutes = treatmentGameSeconds * GAME_SECONDS_TO_HOSPITAL_MINUTES;
        } else {
            // Convert server's minutes to game seconds (1 min hospital = 1 sec game)
            hospitalMinutes = durationFromServer;
            treatmentGameSeconds = hospitalMinutes;  // 1:1 mapping for display
        }
        
        bool isICU = roomName == "ICU";
        
        // For ICU: Agent only stays for setup time, then patient continues alone
        // For other rooms: Agent must stay for entire treatment
        float agentStayGameSeconds;
        float patientTreatmentGameSeconds;
        
        if (isICU) {
            // ICU: Agent sets up patient, then can leave
            agentStayGameSeconds = ICU_SETUP_SECONDS;      // 5 game seconds (5 hospital min)
            patientTreatmentGameSeconds = treatmentGameSeconds;  // 45 game seconds (45 hospital min)
            float setupHospitalMin = ICU_SETUP_SECONDS * GAME_SECONDS_TO_HOSPITAL_MINUTES;
            Debug.Log($"[{role}] ICU: Agent {setupHospitalMin:F0}min setup, Patient {hospitalMinutes:F0}min total @ {TimeManager.TimeScale}x");
        } else {
            // TB, LAB, TRIAGE: Agent must stay entire time
            agentStayGameSeconds = treatmentGameSeconds;
            patientTreatmentGameSeconds = treatmentGameSeconds;
            Debug.Log($"[{role}] {roomName}: {hospitalMinutes:F0}min @ {TimeManager.TimeScale}x speed");
        }
        
        // Start patient treatment
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed before treatment start");
            state = AgentState.Idle;
            if (animator && HasParameter(treatParam, AnimatorControllerParameterType.Bool)) {
                animator.SetBool(treatParam, false);
            }
            yield break;
        }
        
        patient.StartTreatment(patientTreatmentGameSeconds, isICU);
        
        // Agent waits - Time.timeScale speeds this up automatically
        yield return new WaitForSeconds(agentStayGameSeconds);
        
        // Check if patient still exists after wait
        if (!IsPatientValid()) {
            Debug.LogWarning($"Patient {patientId} was destroyed during treatment");
            if (animator && HasParameter(treatParam, AnimatorControllerParameterType.Bool)) {
                animator.SetBool(treatParam, false);
            }
            state = AgentState.Idle;
            yield break;
        }
        
        if (animator && HasParameter(treatParam, AnimatorControllerParameterType.Bool)) {
            animator.SetBool(treatParam, false);
        }
        
        state = AgentState.Idle;
    }
    
    void LeavePatientAlone(int patientId) {
        // Patient stays alone (at TRIAGE after nurse, or ICU after doctor), agent can leave
        PatientController patient = GameManager.Instance?.GetPatient(patientId);
        if (patient != null) {
            // Release patient completely - but don't interrupt ongoing treatment
            patient.StopFollowing();
            
            // Only change state if patient is not currently in treatment
            // If in treatment, let them finish treatment first
            if (patient.state != PatientState.InTreatment) {
                patient.state = PatientState.Waiting;
                Debug.Log($"[{role}] Released Patient #{patientId} - now waiting for next step");
            } else {
                Debug.Log($"[{role}] Released Patient #{patientId} - continuing treatment alone");
            }
        }
        currentPatient = null;
    }
    
    IEnumerator Wait(float duration) {
        state = AgentState.Waiting;
        yield return new WaitForSeconds(duration);
        state = AgentState.Idle;
    }
    
    public AgentState GetState() => state;
    public bool IsBusy() => isExecuting || commandQueue.Count > 0;
    public string GetCurrentRoom() => currentRoom;
}

[System.Serializable]
public class AgentCommand {
    public string action;  // MOVE, ESCORT, TREAT, LEAVE_PATIENT, WAIT
    public string target;  // Room name
    public int patient_id; // Patient ID for escort/treat
    public float duration; // Duration for TREAT/WAIT
}
