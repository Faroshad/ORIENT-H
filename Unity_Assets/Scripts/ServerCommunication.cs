using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Handles communication with Python server
/// Main endpoint: /process_scenario for natural language input
/// </summary>
public class ServerCommunication : MonoBehaviour {
    public static ServerCommunication Instance;
    
    [Header("Server Configuration")]
    public string serverUrl = "http://localhost:5000";
    
    void Awake() => Instance = this;
    
    /// <summary>
    /// Main endpoint: Send natural language description, get patients + plan
    /// Example: "Two patients: one with chest pain, another with broken arm"
    /// </summary>
    public void ProcessScenario(string description, System.Action<ScenarioResponse> callback) {
        var request = new ScenarioRequest {
            description = description,
            rooms = GetRoomPositions()
        };
        StartCoroutine(PostRequest("/process_scenario", request, callback));
    }
    
    /// <summary>
    /// Get next plan for current queue
    /// </summary>
    public void GetPlan(string nursePosition, string doctorPosition, System.Action<PlanResponse> callback) {
        var request = new PlanRequest {
            nurse_position = nursePosition,
            doctor_position = doctorPosition,
            rooms = GetRoomPositions()
        };
        StartCoroutine(PostRequest("/get_plan", request, callback));
    }
    
    /// <summary>
    /// Mark patient step as complete
    /// </summary>
    public void CompletePatientStep(int patientId, System.Action<StepResponse> callback) {
        var request = new StepRequest { patient_id = patientId };
        StartCoroutine(PostRequest("/complete_step", request, callback));
    }
    
    /// <summary>
    /// Notify patient exit
    /// </summary>
    public void PatientExit(int patientId, System.Action<StepResponse> callback) {
        var request = new StepRequest { patient_id = patientId };
        StartCoroutine(PostRequest("/patient_exit", request, callback));
    }
    
    /// <summary>
    /// Get current queue status
    /// </summary>
    public void GetQueueStatus(System.Action<QueueStatusResponse> callback) {
        StartCoroutine(GetRequest("/queue_status", callback));
    }
    
    /// <summary>
    /// Reset server state
    /// </summary>
    public void ResetServer(System.Action<SimpleResponse> callback) {
        StartCoroutine(PostRequest("/reset", new EmptyRequest(), callback));
    }
    
    /// <summary>
    /// Save regret analysis charts and data
    /// </summary>
    public void SaveAnalysis(System.Action<SaveAnalysisResponse> callback) {
        var request = new SaveAnalysisRequest { output_dir = "output" };
        StartCoroutine(PostRequest("/save_analysis", request, callback));
    }
    
    RoomPosition[] GetRoomPositions() {
        if (OOIManager.Instance == null) return null;
        
        Dictionary<string, Vector3> positions = OOIManager.Instance.GetRoomPositions();
        if (positions == null || positions.Count == 0) return null;
        
        var list = new List<RoomPosition>();
        foreach (var kv in positions) {
            list.Add(new RoomPosition {
                name = kv.Key,
                x = kv.Value.x,
                y = kv.Value.y,
                z = kv.Value.z
            });
        }
        return list.ToArray();
    }
    
    IEnumerator PostRequest<T>(string endpoint, object data, System.Action<T> callback) where T : class {
        string json = JsonUtility.ToJson(data);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        
        using (UnityWebRequest request = new UnityWebRequest(serverUrl + endpoint, "POST")) {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success) {
                T response = JsonUtility.FromJson<T>(request.downloadHandler.text);
                callback?.Invoke(response);
            } else {
                Debug.LogError($"Server error ({endpoint}): {request.error}");
                callback?.Invoke(null);
            }
        }
    }
    
    IEnumerator GetRequest<T>(string endpoint, System.Action<T> callback) where T : class {
        using (UnityWebRequest request = UnityWebRequest.Get(serverUrl + endpoint)) {
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success) {
                T response = JsonUtility.FromJson<T>(request.downloadHandler.text);
                callback?.Invoke(response);
            } else {
                Debug.LogError($"Server error ({endpoint}): {request.error}");
                callback?.Invoke(null);
            }
        }
    }
}

// ==================== REQUEST TYPES ====================

[System.Serializable]
public class ScenarioRequest {
    public string description;
    public RoomPosition[] rooms;
}

[System.Serializable]
public class PlanRequest {
    public string nurse_position;
    public string doctor_position;
    public RoomPosition[] rooms;
}

[System.Serializable]
public class StepRequest {
    public int patient_id;
}

[System.Serializable]
public class EmptyRequest { }

[System.Serializable]
public class SaveAnalysisRequest {
    public string output_dir;
}

[System.Serializable]
public class RoomPosition {
    public string name;
    public float x;
    public float y;
    public float z;
}

// ==================== RESPONSE TYPES ====================

[System.Serializable]
public class ScenarioResponse {
    public bool success;
    public string error;
    public PatientInfo[] patients;
    public int patient_count;
    public AssignmentInfo assignment;
    public AgentPlan nurse_plan;
    public AgentPlan doctor_plan;
    public LearningStats learning_stats;
    public float expected_reward;
    public QueueStatus queue_status;
}

[System.Serializable]
public class PatientInfo {
    public int id;
    public string type;
    public string description;
    public string[] pathway;
    public float deadline;
    public bool doctor_required;
}

[System.Serializable]
public class PlanResponse {
    public bool success;
    public AssignmentInfo assignment;
    public AgentPlan nurse_plan;
    public AgentPlan doctor_plan;
    public LearningStats learning_stats;
    public float expected_reward;
}

[System.Serializable]
public class AssignmentInfo {
    public string strategy;
    public string description;
    public int queue_size;
}

[System.Serializable]
public class StepResponse {
    public bool success;
    public bool patient_complete;
    public QueueStatus queue_status;
}

[System.Serializable]
public class QueueStatusResponse {
    public bool success;
    public QueueStatus queue_status;
    public LearningStats learning_stats;
}

[System.Serializable]
public class QueueStatus {
    public int queue_size;
    public QueuePatient[] patients;
    public string nurse_position;
    public string doctor_position;
}

[System.Serializable]
public class QueuePatient {
    public int id;
    public string type;
    public string next_room;
    public float urgency;
    public int steps_remaining;
}

[System.Serializable]
public class LearningStats {
    public int total_rounds;
    public float cumulative_regret;
    public float avg_regret_per_round;
}

[System.Serializable]
public class SimpleResponse {
    public bool success;
    public string message;
}

[System.Serializable]
public class SaveAnalysisResponse {
    public bool saved;
    public string chart_path;
    public string data_path;
    public int total_rounds;
    public float final_cumulative_regret;
    public string reason;
}

[System.Serializable]
public class AgentPlan {
    public AgentCommand[] commands;
}
