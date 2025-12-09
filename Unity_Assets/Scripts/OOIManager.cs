using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages all OOIs in the scene
/// Add this to a manager GameObject
/// </summary>
public class OOIManager : MonoBehaviour {
    public static OOIManager Instance;
    
    Dictionary<string, OOI> oois = new Dictionary<string, OOI>();
    
    void Awake() {
        Instance = this;
        RefreshOOIs();
    }
    
    public void RefreshOOIs() {
        oois.Clear();
        foreach (OOI ooi in FindObjectsByType<OOI>(FindObjectsSortMode.None)) {
            oois[ooi.roomName] = ooi;
        }
    }
    
    public OOI GetOOI(string name) {
        return oois.TryGetValue(name, out OOI ooi) ? ooi : null;
    }
    
    public List<string> GetAllRoomNames() {
        return new List<string>(oois.Keys);
    }
    
    public Dictionary<string, Vector3> GetRoomPositions() {
        var positions = new Dictionary<string, Vector3>();
        foreach (var kv in oois) {
            positions[kv.Key] = kv.Value.GetArrivalPosition();
        }
        return positions;
    }
}

