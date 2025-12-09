using UnityEngine;

/// <summary>
/// Object of Interest (Room/Place) - Attach to room GameObjects
/// Set the roomName in Inspector to match server expectations
/// </summary>
public class OOI : MonoBehaviour {
    [Header("Room Configuration")]
    public string roomName = "ENT"; // ENT, TRIAGE, TB, LAB, ICU
    public Transform arrivalPoint; // Where agents stand when visiting
    
    void Awake() {
        if (arrivalPoint == null) arrivalPoint = transform;
    }
    
    public Vector3 GetArrivalPosition() => arrivalPoint.position;
    
    void OnDrawGizmos() {
        Gizmos.color = roomName switch {
            "ENT" => Color.green,
            "TRIAGE" => Color.yellow,
            "TB" => Color.blue,
            "LAB" => Color.magenta,
            "ICU" => Color.red,
            _ => Color.white
        };
        Gizmos.DrawWireCube(transform.position, new Vector3(2, 0.5f, 2));
        UnityEditor.Handles.Label(transform.position + Vector3.up, roomName);
    }
}

