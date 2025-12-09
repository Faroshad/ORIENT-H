using UnityEngine;
using UnityEngine.AI;

public enum PatientState { Spawning, MovingToWait, Waiting, Following, InTreatment, Exiting, Done }

/// <summary>
/// Controls patient behavior:
/// - Spawn at ENT
/// - Move to Waiting area
/// - Follow agent during escort
/// - Stay for treatment
/// - Exit through ENT
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class PatientController : MonoBehaviour {
    [Header("Patient Info")]
    public int patientId = -1;
    public string patientType = "Minor";
    public string[] pathway;
    public int currentStep = 0;
    public float deadline = 30f;
    public bool doctorRequired = false;
    
    [Header("State")]
    public PatientState state = PatientState.Spawning;
    public Transform followTarget;
    public float followDistance = 1.5f;
    
    [Header("Treatment")]
    public float treatmentTimeRemaining = 0f;
    public bool waitingAlone = false;
    float treatmentStartTime = -1f;
    float maxTreatmentDuration = 10f; // Safety timeout
    bool isExiting = false;  // Prevent multiple exit attempts
    float stepWaitingStartTime = -1f;  // Track when patient started waiting at current step
    
    NavMeshAgent nav;
    Animator animator;
    float spawnTime;
    
    public System.Action<PatientController> OnTreatmentComplete;
    public System.Action<PatientController> OnExitComplete;
    
    void Start() {
        nav = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        spawnTime = Time.time;
        
        if (nav != null) {
            nav.speed = 2.5f;
            nav.stoppingDistance = followDistance;
        }
        
        // Ensure on NavMesh
        StartCoroutine(EnsureOnNavMesh());
    }
    
    System.Collections.IEnumerator EnsureOnNavMesh() {
        yield return null;
        
        if (nav != null && !nav.isOnNavMesh) {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas)) {
                nav.Warp(hit.position);
            }
        }
    }
    
    void Update() {
        // Don't process if already done
        if (state == PatientState.Done) {
            return;  // Patient is done, don't do anything else
        }
        
        // If exiting, only process exit logic (don't do other processing)
        if (isExiting || state == PatientState.Exiting) {
            CheckExit();
            return;  // Only handle exit, nothing else
        }
        
        // TIMEOUT CHECK: If treatment started but is taking too long, force completion
        if (treatmentStartTime > 0 && treatmentTimeRemaining > 0) {
            float elapsed = Time.time - treatmentStartTime;
            if (elapsed > maxTreatmentDuration) {
                Debug.LogWarning($"Patient #{patientId}: Treatment timeout! Elapsed: {elapsed:F1}s, Expected: {maxTreatmentDuration:F1}s. Forcing completion...");
                treatmentTimeRemaining = 0;
                treatmentStartTime = -1f;
                CompleteTreatment();
            }
        }
        
        // CRITICAL: Always process treatment if timer is active, regardless of state
        // This ensures ICU patients complete treatment even if state changes
        if (treatmentTimeRemaining > 0) {
            if (state != PatientState.InTreatment && state != PatientState.Exiting && state != PatientState.Done) {
                Debug.Log($"Patient #{patientId}: Treatment active ({(treatmentTimeRemaining):F1}s remaining) but state is {state}. Restoring InTreatment state.");
                state = PatientState.InTreatment;
            }
            ProcessTreatment();
        }
        
        // Check if pathway is complete - exit immediately
        if (pathway != null && currentStep >= pathway.Length && !isExiting && state != PatientState.Exiting && state != PatientState.Done) {
            Debug.Log($"Patient #{patientId}: Pathway complete (step {currentStep}/{pathway.Length}). Exiting...");
            ExitScene();
            return;  // Exit immediately, don't process further
        }
        
        // SPECIAL CASE: ICU patients - check if treatment complete and at final step
        if (waitingAlone && treatmentTimeRemaining <= 0 && pathway != null) {
            if (currentStep >= pathway.Length - 1 && currentStep < pathway.Length) {
                // At last step, treatment complete, but pathway not marked complete yet
                // This will be handled by CompleteTreatment() which increments currentStep
                // Just ensure we don't get stuck
                if (state == PatientState.Waiting) {
                    // Treatment is done, increment step if needed
                    if (currentStep == pathway.Length - 1) {
                        currentStep = pathway.Length;  // Mark pathway complete
                        ExitScene();
                        return;
                    }
                }
            }
        }
        
        switch (state) {
            case PatientState.MovingToWait:
                CheckArrivalAtWaiting();
                break;
            case PatientState.Following:
                FollowAgent();
                break;
            case PatientState.InTreatment:
                // ProcessTreatment already called above
                break;
            case PatientState.Exiting:
                CheckExit();
                break;
            case PatientState.Waiting:
                // Treatment processing handled above if timer active
                
                // Initialize step waiting time if not set (only when actually waiting, not during treatment/following)
                if (stepWaitingStartTime < 0 && treatmentTimeRemaining <= 0) {
                    stepWaitingStartTime = Time.time;
                }
                
                // GENERAL WAITING TIMEOUT: If patient has been waiting too long at ANY step, force progression
                if (pathway != null && currentStep < pathway.Length) {
                    // Calculate how long patient has been waiting at current step
                    float waitTime = stepWaitingStartTime > 0 ? (Time.time - stepWaitingStartTime) : 0f;
                    
                    // Timeout: 60 seconds real time per step (NOT scaled by time scale - this is a safety timeout)
                    // At 30x speed, 60 seconds real time = 30 minutes game time, which is reasonable
                    float timeoutSeconds = 60f; // Real seconds, not game seconds
                    
                    if (waitTime > timeoutSeconds) {
                        Debug.LogWarning($"Patient #{patientId}: Waiting {waitTime:F1}s (real time) at step {currentStep}/{pathway.Length}. Forcing next step...");
                        
                        // If at last step, complete pathway
                        if (currentStep >= pathway.Length - 1) {
                            currentStep = pathway.Length;
                            stepWaitingStartTime = -1f; // Reset
                            ExitScene();
                            return;
                        } else {
                            // Force progression to next step
                            currentStep++;
                            stepWaitingStartTime = Time.time; // Reset for new step
                            Debug.Log($"Patient #{patientId}: Forced to step {currentStep}/{pathway.Length}");
                        }
                    }
                }
                
                // If waiting at final step after treatment, check if pathway should be complete
                if (pathway != null && currentStep >= pathway.Length - 1 && treatmentTimeRemaining <= 0) {
                    // Should have been completed by CompleteTreatment() - double check
                    if (currentStep < pathway.Length) {
                        // Still at last step but treatment done - might need to complete
                        float waitTime = stepWaitingStartTime > 0 ? (Time.time - stepWaitingStartTime) : 0f;
                        if (waitTime > 10f) {  // 10 seconds real time for final step
                            Debug.LogWarning($"Patient #{patientId}: Waiting {waitTime:F1}s at step {currentStep}. Completing pathway...");
                            currentStep = pathway.Length;
                            stepWaitingStartTime = -1f; // Reset
                            ExitScene();
                            return;
                        }
                    }
                }
                break;
        }
        
        // Only update animation if not done
        if (state != PatientState.Done) {
            UpdateAnimation();
        }
    }
    
    void CheckArrivalAtWaiting() {
        if (nav == null || !nav.isOnNavMesh) return;
        
        if (!nav.pathPending && nav.remainingDistance <= nav.stoppingDistance + 0.3f) {
            state = PatientState.Waiting;
        }
    }
    
    void FollowAgent() {
        if (followTarget == null) {
            state = PatientState.Waiting;
            return;
        }
        
        if (nav == null || !nav.isOnNavMesh) return;
        
        float distToAgent = Vector3.Distance(transform.position, followTarget.position);
        
        // Only update destination if agent has moved significantly
        if (distToAgent > followDistance * 0.5f) {
            // Calculate position slightly behind the agent
            Vector3 agentForward = followTarget.forward;
            Vector3 targetPos = followTarget.position - agentForward * followDistance;
            nav.SetDestination(targetPos);
        }
        
        // Speed up if falling behind
        if (distToAgent > followDistance * 2f) {
            nav.speed = 4f;
        } else {
            nav.speed = 2.5f;
        }
    }
    
    void ProcessTreatment() {
        if (treatmentTimeRemaining > 0) {
            // Time.deltaTime is automatically scaled by Unity's Time.timeScale
            treatmentTimeRemaining -= Time.deltaTime;
            
            // Ensure state is InTreatment while treatment is active
            if (state != PatientState.InTreatment && state != PatientState.Exiting && state != PatientState.Done) {
                Debug.LogWarning($"Patient #{patientId}: Treatment active ({treatmentTimeRemaining:F1}s) but state is {state}. Restoring InTreatment state.");
                state = PatientState.InTreatment;
            }
            
            // Log progress every 10 game seconds (= 10 hospital minutes) for ICU patients
            if (waitingAlone) {
                int currentInterval = Mathf.FloorToInt(treatmentTimeRemaining / 10f);
                int previousInterval = Mathf.FloorToInt((treatmentTimeRemaining + Time.deltaTime) / 10f);
                if (currentInterval != previousInterval && treatmentTimeRemaining > 0) {
                    float hospitalMinutesLeft = treatmentTimeRemaining;  // game seconds = hospital minutes
                    Debug.Log($"Patient #{patientId}: ICU... {hospitalMinutesLeft:F0}min remaining");
                }
            }
            
            if (treatmentTimeRemaining <= 0) {
                treatmentTimeRemaining = 0; // Ensure it's zero
                Debug.Log($"Patient #{patientId}: Treatment complete ({(waitingAlone ? "ICU" : "regular")})");
                CompleteTreatment();
            }
        } else if (treatmentTimeRemaining < 0) {
            // Timer went negative somehow - force completion
            Debug.LogWarning($"Patient #{patientId}: Treatment timer is negative ({treatmentTimeRemaining}), forcing completion!");
            treatmentTimeRemaining = 0;
            CompleteTreatment();
        }
    }
    
    void CheckExit() {
        if (nav == null || !nav.isOnNavMesh) {
            // If not on NavMesh, destroy immediately
            state = PatientState.Done;
            OnExitComplete?.Invoke(this);
            Destroy(gameObject, 0.1f);
            return;
        }
        
        // Check if patient reached entrance (exiting point)
        if (!nav.pathPending && nav.remainingDistance <= nav.stoppingDistance + 0.3f) {
            state = PatientState.Done;
            Debug.Log($"Patient #{patientId} reached entrance and is leaving the scene");
            
            // Notify completion BEFORE destroying
            OnExitComplete?.Invoke(this);
            
            // Fade out and destroy
            StartCoroutine(FadeOutAndDestroy());
        }
    }
    
    System.Collections.IEnumerator FadeOutAndDestroy() {
        // Brief pause at entrance
        yield return new WaitForSeconds(0.3f);
        
        // Fade out effect (optional visual)
        Renderer renderer = GetComponentInChildren<Renderer>();
        if (renderer != null) {
            Material mat = renderer.material;
            if (mat != null) {
                float fadeTime = 0.5f;
                float elapsed = 0f;
                Color originalColor = mat.color;
                
                while (elapsed < fadeTime) {
                    elapsed += Time.deltaTime;
                    float alpha = Mathf.Lerp(1f, 0f, elapsed / fadeTime);
                    mat.color = new Color(originalColor.r, originalColor.g, originalColor.b, alpha);
                    yield return null;
                }
            }
        }
        
        // Destroy the patient
        Destroy(gameObject);
    }
    
    void UpdateAnimation() {
        if (animator == null) return;
        
        float speed = nav != null && nav.isOnNavMesh ? nav.velocity.magnitude : 0f;
        
        if (HasParameter("Speed", AnimatorControllerParameterType.Float)) {
            animator.SetFloat("Speed", nav.speed > 0 ? speed / nav.speed : 0);
        }
        if (HasParameter("IsWalking", AnimatorControllerParameterType.Bool)) {
            animator.SetBool("IsWalking", speed > 0.1f);
        }
    }
    
    bool HasParameter(string name, AnimatorControllerParameterType type) {
        if (animator == null) return false;
        foreach (var param in animator.parameters) {
            if (param.name == name && param.type == type) return true;
        }
        return false;
    }
    
    /// <summary>
    /// Move to waiting area after spawning
    /// </summary>
    public void MoveToWaitingArea(Vector3 waitingPosition) {
        state = PatientState.MovingToWait;
        
        if (nav != null && nav.isOnNavMesh) {
            // Add some randomness to spread out patients
            Vector3 target = waitingPosition + new Vector3(
                Random.Range(-1.5f, 1.5f), 0, Random.Range(-1.5f, 1.5f)
            );
            nav.SetDestination(target);
        } else {
            state = PatientState.Waiting;
        }
    }
    
    /// <summary>
    /// Start following an agent
    /// </summary>
    public void StartFollowing(Transform agent, float distance = 1.5f) {
        followTarget = agent;
        followDistance = distance;
        state = PatientState.Following;
        // Don't reset stepWaitingStartTime here - following is part of the step, not a new step
        
        // Match agent speed initially
        if (nav != null) nav.speed = 2.5f;
    }
    
    /// <summary>
    /// Stop following and wait
    /// </summary>
    public void StopFollowing() {
        followTarget = null;
        // Only change to Waiting if not already exiting or done
        if (state != PatientState.Exiting && state != PatientState.Done) {
            state = PatientState.Waiting;
        }
        // Don't reset path if exiting (we need to walk to entrance)
        if (state != PatientState.Exiting && nav != null && nav.isOnNavMesh) {
            nav.ResetPath();
        }
    }
    
    /// <summary>
    /// Start treatment at current location
    /// Duration is in GAME SECONDS (1 game second = 1 hospital minute)
    /// Unity's Time.timeScale handles speedup automatically
    /// </summary>
    public void StartTreatment(float gameSeconds, bool alone = false) {
        // Stop following if currently following an agent
        if (state == PatientState.Following) {
            StopFollowing();
        }
        
        state = PatientState.InTreatment;
        treatmentTimeRemaining = gameSeconds;
        treatmentStartTime = Time.time;
        waitingAlone = alone;
        followTarget = null;
        maxTreatmentDuration = gameSeconds * 2f; // Allow generous buffer
        // Don't reset stepWaitingStartTime here - treatment is part of the step, not a new step
        
        if (nav != null && nav.isOnNavMesh) {
            nav.ResetPath();
        }
        
        // Game seconds = hospital minutes for display
        float hospitalMinutes = gameSeconds;
        
        // Special logging for ICU treatment (critical patients)
        if (alone) {
            Debug.Log($"Patient #{patientId} ({patientType}) ICU: {hospitalMinutes:F0}min @ {TimeManager.TimeScale}x speed");
            // Start backup coroutine to ensure ICU treatment completes
            StartCoroutine(ICUTreatmentBackup(gameSeconds));
        } else {
            Debug.Log($"Patient #{patientId} treatment: {hospitalMinutes:F0}min @ {TimeManager.TimeScale}x speed");
        }
        
        // Ensure treatment timer is properly initialized
        if (treatmentTimeRemaining <= 0) {
            Debug.LogError($"Patient #{patientId}: Treatment duration is {gameSeconds}, but timer is {treatmentTimeRemaining}!");
        }
    }
    
    /// <summary>
    /// Backup mechanism: Force ICU treatment completion after duration
    /// This ensures ICU patients always exit, even if timer fails
    /// </summary>
    System.Collections.IEnumerator ICUTreatmentBackup(float duration) {
        yield return new WaitForSeconds(duration + 1f); // Wait slightly longer than treatment
        
        // If treatment hasn't completed yet, force it
        if (treatmentTimeRemaining > 0 || (state != PatientState.Exiting && state != PatientState.Done)) {
            Debug.LogWarning($"Patient #{patientId}: ICU treatment backup triggered! Timer: {treatmentTimeRemaining}, State: {state}. Forcing completion...");
            
            // Force treatment completion
            if (treatmentTimeRemaining > 0) {
                treatmentTimeRemaining = 0;
            }
            
            // Check if pathway should be complete
            if (pathway != null && currentStep < pathway.Length) {
                CompleteTreatment();
            } else if (currentStep >= pathway.Length && state != PatientState.Exiting && state != PatientState.Done) {
                ExitScene();
            }
        }
    }
    
    /// <summary>
    /// Treatment complete, advance to next step
    /// </summary>
    void CompleteTreatment() {
        if (pathway == null || pathway.Length == 0) {
            Debug.LogError($"Patient #{patientId} has no pathway defined!");
            ExitScene(); // Exit anyway
            return;
        }
        
        // Prevent multiple calls or completion beyond pathway
        if (currentStep >= pathway.Length || isExiting || state == PatientState.Done || state == PatientState.Exiting) {
            return;  // Already complete or exiting
        }
        
        // Get the room that was just completed (before incrementing currentStep)
        string completedRoom = currentStep < pathway.Length ? pathway[currentStep] : "UNKNOWN";
        int completedStepIndex = currentStep;  // Store the step index before incrementing
        
        // Increment step AFTER getting the completed room
        currentStep++;
        state = PatientState.Waiting;
        treatmentTimeRemaining = 0; // Clear treatment timer
        treatmentStartTime = -1f; // Clear treatment start time
        stepWaitingStartTime = -1f; // Reset step waiting time for new step
        
        Debug.Log($"Patient #{patientId} completed step {completedStepIndex + 1}/{pathway.Length} ({completedRoom})");
        
        // Special check for ICU completion
        if (completedRoom == "ICU") {
            Debug.Log($"Patient #{patientId}: ICU treatment completed! This is the final step for critical patients.");
        }
        
        OnTreatmentComplete?.Invoke(this);
        
        // Check if pathway complete - patient must exit through entrance
        if (currentStep >= pathway.Length) {
            Debug.Log($"Patient #{patientId} ({patientType}) pathway complete ({pathway.Length}/{pathway.Length} steps) - exiting to entrance");
            ExitScene();
        } else if (currentStep < pathway.Length) {
            Debug.Log($"Patient #{patientId} waiting for next step: {pathway[currentStep]}");
        }
    }
    
    /// <summary>
    /// Start exiting to entrance - patient must walk to ENT and disappear
    /// </summary>
    public void ExitScene() {
        // Prevent multiple calls
        if (isExiting || state == PatientState.Done || state == PatientState.Exiting) {
            return;  // Already exiting or done
        }
        
        isExiting = true;
        state = PatientState.Exiting;
        
        // Stop any following behavior
        StopFollowing();
        
        // Mark pathway as complete if not already
        if (pathway != null && currentStep < pathway.Length) {
            currentStep = pathway.Length;
        }
        
        OOI entrance = OOIManager.Instance?.GetOOI("ENT");
        if (entrance == null) {
            Debug.LogWarning($"Patient #{patientId}: ENT not found, destroying immediately");
            state = PatientState.Done;
            OnExitComplete?.Invoke(this);
            Destroy(gameObject, 0.1f);
            return;
        }
        
        if (nav != null) {
            // Ensure on NavMesh
            if (!nav.isOnNavMesh) {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas)) {
                    nav.Warp(hit.position);
                }
            }
            
            // Set destination to entrance
            nav.SetDestination(entrance.GetArrivalPosition());
            nav.speed = 3.0f; // Slightly faster exit
            
            Debug.Log($"Patient #{patientId} ({patientType}) exiting to entrance (ENT)");
        } else {
            // No NavMesh agent - destroy immediately
            Debug.LogWarning($"Patient #{patientId}: No NavMeshAgent, destroying immediately");
            state = PatientState.Done;
            OnExitComplete?.Invoke(this);
            Destroy(gameObject, 0.1f);
        }
    }
    
    /// <summary>
    /// Get next room in pathway
    /// </summary>
    public string GetNextRoom() {
        if (currentStep < pathway.Length) {
            return pathway[currentStep];
        }
        return null;
    }
    
    /// <summary>
    /// Get time remaining before deadline
    /// </summary>
    public float GetTimeRemaining() {
        return deadline - (Time.time - spawnTime);
    }
    
    /// <summary>
    /// Check if deadline passed
    /// </summary>
    public bool IsOverdue() {
        return GetTimeRemaining() <= 0;
    }
}
