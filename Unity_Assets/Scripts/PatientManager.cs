using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Tracks active patients and their status (optional visual tracking)
/// </summary>
public class PatientManager : MonoBehaviour {
    public static PatientManager Instance;
    
    [Header("Patient Tracking")]
    public List<PatientData> activePatients = new List<PatientData>();
    
    void Awake() => Instance = this;
    
    public void AddPatient(PatientData patient) {
        patient.arrivalTime = Time.time;
        activePatients.Add(patient);
    }
    
    public void RemovePatient(PatientData patient) {
        activePatients.Remove(patient);
    }
    
    public void ClearPatients() {
        activePatients.Clear();
    }
}

[System.Serializable]
public class PatientData {
    public string type;
    public string severity;
    public string[] pathway;
    public int currentStep;
    public float arrivalTime;
    public bool doctorRequired;
    public string originalDescription;
}
