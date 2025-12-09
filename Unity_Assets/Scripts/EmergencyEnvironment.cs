using UnityEngine;
using System.Collections.Generic;

public enum RoomType {
    ENT,
    TRIAGE,
    TB,
    LAB,
    ICU
}

public class EmergencyEnvironment : MonoBehaviour {
    public static EmergencyEnvironment Instance;
    
    public Dictionary<RoomType, Vector3> roomPositions = new Dictionary<RoomType, Vector3> {
        { RoomType.ENT, new Vector3(0, 0, 0) },
        { RoomType.TRIAGE, new Vector3(2, 0, 0) },
        { RoomType.TB, new Vector3(5, 0, 3) },
        { RoomType.LAB, new Vector3(8, 0, -2) },
        { RoomType.ICU, new Vector3(10, 0, 5) }
    };
    
    public Transform nurse, doctor;
    public Material roomMaterial;
    
    void Awake() {
        Instance = this;
        CreateRooms();
    }
    
    void CreateRooms() {
        foreach (var kv in roomPositions) {
            GameObject room = GameObject.CreatePrimitive(PrimitiveType.Cube);
            room.transform.position = kv.Value;
            room.transform.localScale = new Vector3(1.5f, 0.1f, 1.5f);
            room.name = kv.Key.ToString();
            room.GetComponent<Renderer>().material.color = GetRoomColor(kv.Key);
        }
    }
    
    Color GetRoomColor(RoomType r) {
        return r switch {
            RoomType.ENT => Color.green,
            RoomType.TRIAGE => Color.yellow,
            RoomType.TB => Color.blue,
            RoomType.LAB => Color.magenta,
            RoomType.ICU => Color.red,
            _ => Color.white
        };
    }
    
    public RoomType GetCurrentRoom(Vector3 pos) {
        float minDist = float.MaxValue;
        RoomType closest = RoomType.ENT;
        foreach (var kv in roomPositions) {
            float d = Vector3.Distance(pos, kv.Value);
            if (d < minDist) { minDist = d; closest = kv.Key; }
        }
        return minDist < 1f ? closest : RoomType.ENT;
    }
    
    public Vector3 GetRoomPosition(RoomType r) => roomPositions[r];
    
    public float GetDistance(RoomType from, RoomType to) =>
        Vector3.Distance(roomPositions[from], roomPositions[to]);
}

