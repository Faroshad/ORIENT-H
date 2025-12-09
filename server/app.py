"""
Emergency Unit Server with Online Learning
- Receives natural language patient descriptions from Unity
- Uses LLM to parse multiple patients from narrative text
- Uses Dynamic Regret Analyzer for optimal strategy
- Returns action plans for nurse and doctor
"""
from flask import Flask, request, jsonify
from flask_cors import CORS
import os
from dotenv import load_dotenv
from openai import OpenAI
import json
import re
from regret_engine import analyzer, DynamicRegretAnalyzer

load_dotenv()

app = Flask(__name__)
CORS(app)

# Optional: OpenAI client for patient parsing
try:
    client = OpenAI(api_key=os.getenv("OPENAI_API_KEY"))
    USE_LLM = True
except:
    client = None
    USE_LLM = False

# LLM prompt for parsing multiple patients from natural text
MULTI_PATIENT_PROMPT = """You are a medical triage AI. Parse the following description and extract ALL patients mentioned.

For EACH patient, classify their condition.

Respond ONLY with valid JSON array:
[
    {
        "type": "Minor" | "Moderate" | "Critical",
        "description": "brief description of this patient's condition"
    }
]

Classification rules:
- Minor: cuts, bruises, mild fever, minor pain
- Moderate: infections, fractures, needs tests, breathing difficulty
- Critical: chest pain, stroke, severe trauma, unconscious, heart attack

Examples:
"A patient with a small cut" → [{"type": "Minor", "description": "small cut"}]
"Two patients: one with chest pain, another with broken arm" → [{"type": "Critical", "description": "chest pain"}, {"type": "Moderate", "description": "broken arm"}]

If no patients described, return [].

Input text: """


@app.route('/process_scenario', methods=['POST'])
def process_scenario():
    """
    Main endpoint: Parse natural language, spawn patients, analyze, return plan
    
    Input: { "description": "There are two patients, one critical with chest pain..." }
    Output: { patients: [...], assignment: {...}, nurse_plan: {...}, doctor_plan: {...} }
    """
    data = request.json or {}
    description = data.get('description', '')
    
    if not description:
        return jsonify({'success': False, 'error': 'No description provided'})
    
    # Update room layout if provided
    rooms_payload = data.get('rooms') or []
    if rooms_payload:
        try:
            room_coords = {
                r['name']: (float(r.get('x', 0)), float(r.get('z', 0)))
                for r in rooms_payload if 'name' in r
            }
            if room_coords:
                analyzer.set_rooms(room_coords)
        except Exception as e:
            print(f"Room update error: {e}")
    
    # Step 1: Parse description to extract patients
    patients_info = parse_multiple_patients(description)
    
    if not patients_info:
        return jsonify({
            'success': False, 
            'error': 'No patients found in description',
            'parsed_count': 0
        })
    
    # Step 2: Spawn each patient
    spawned_patients = []
    for pinfo in patients_info:
        patient = analyzer.spawn_patient(pinfo['type'])
        spawned_patients.append({
            'id': patient.id,
            'type': patient.ptype,
            'description': pinfo.get('description', ''),
            'pathway': patient.pathway,
            'deadline': patient.deadline,
            'doctor_required': patient.doctor_required
        })
    
    # Step 3: Analyze and plan
    result = analyzer.analyze_and_plan()
    
    return jsonify({
        'success': True,
        'patients': spawned_patients,
        'patient_count': len(spawned_patients),
        'assignment': result['assignment'],
        'nurse_plan': result['nurse_plan'],
        'doctor_plan': result['doctor_plan'],
        'learning_stats': result['learning_stats'],
        'expected_reward': result['expected_reward'],
        'queue_status': analyzer.get_queue_status()
    })


def parse_multiple_patients(description: str) -> list:
    """Parse natural language to extract multiple patients"""
    
    if USE_LLM and client:
        try:
            response = client.chat.completions.create(
                model="gpt-4o-mini",
                messages=[{"role": "user", "content": MULTI_PATIENT_PROMPT + description}],
                temperature=0.1
            )
            text = response.choices[0].message.content
            
            # Find JSON array in response
            start = text.find('[')
            end = text.rfind(']') + 1
            if start >= 0 and end > start:
                patients = json.loads(text[start:end])
                # Validate and normalize
                valid_patients = []
                for p in patients:
                    ptype = p.get('type', 'Minor')
                    if ptype not in ['Minor', 'Moderate', 'Critical']:
                        ptype = 'Minor'
                    valid_patients.append({
                        'type': ptype,
                        'description': p.get('description', '')
                    })
                return valid_patients
        except Exception as e:
            print(f"LLM parsing error: {e}")
    
    # Fallback: Rule-based parsing
    return parse_patients_rules(description)


def parse_patients_rules(description: str) -> list:
    """Rule-based fallback for parsing patients"""
    desc_lower = description.lower()
    patients = []
    
    # Look for number indicators
    count = 1
    if 'two' in desc_lower or '2' in desc_lower:
        count = 2
    elif 'three' in desc_lower or '3' in desc_lower:
        count = 3
    elif 'four' in desc_lower or '4' in desc_lower:
        count = 4
    
    # Keywords for each severity
    critical_keywords = ['chest pain', 'stroke', 'unconscious', 'severe', 'critical', 'heart', 'emergency', 'dying']
    moderate_keywords = ['fracture', 'broken', 'infection', 'test', 'blood', 'xray', 'breathing', 'moderate']
    minor_keywords = ['cut', 'bruise', 'fever', 'cold', 'minor', 'small', 'mild']
    
    # Detect mentioned severities
    has_critical = any(kw in desc_lower for kw in critical_keywords)
    has_moderate = any(kw in desc_lower for kw in moderate_keywords)
    has_minor = any(kw in desc_lower for kw in minor_keywords)
    
    if count == 1:
        # Single patient
        if has_critical:
            patients.append({'type': 'Critical', 'description': 'critical condition'})
        elif has_moderate:
            patients.append({'type': 'Moderate', 'description': 'moderate condition'})
        else:
            patients.append({'type': 'Minor', 'description': 'minor condition'})
    else:
        # Multiple patients - try to distribute based on keywords
        if has_critical:
            patients.append({'type': 'Critical', 'description': 'critical condition'})
            count -= 1
        if has_moderate and count > 0:
            patients.append({'type': 'Moderate', 'description': 'moderate condition'})
            count -= 1
        if has_minor and count > 0:
            patients.append({'type': 'Minor', 'description': 'minor condition'})
            count -= 1
        # Fill remaining with minor
        while count > 0:
            patients.append({'type': 'Minor', 'description': 'unspecified condition'})
            count -= 1
    
    return patients


@app.route('/get_plan', methods=['POST'])
def get_plan():
    """Get optimal action plan for current queue"""
    data = request.json or {}
    
    # Update room layout if provided
    rooms_payload = data.get('rooms') or []
    if rooms_payload:
        try:
            room_coords = {
                r['name']: (float(r.get('x', 0)), float(r.get('z', 0)))
                for r in rooms_payload if 'name' in r
            }
            if room_coords:
                analyzer.set_rooms(room_coords)
        except Exception as e:
            print(f"Room update error: {e}")
    
    # Update agent positions if provided
    if 'nurse_position' in data:
        analyzer.update_agent_position('nurse', data['nurse_position'])
    if 'doctor_position' in data:
        analyzer.update_agent_position('doctor', data['doctor_position'])
    
    # Get optimal plan
    result = analyzer.analyze_and_plan()
    
    return jsonify(result)


@app.route('/complete_step', methods=['POST'])
def complete_step():
    """Mark a patient step as complete"""
    data = request.json or {}
    patient_id = data.get('patient_id')
    
    if patient_id is None:
        return jsonify({'success': False, 'error': 'No patient_id provided'})
    
    is_complete = analyzer.step_patient(patient_id)
    
    if is_complete:
        analyzer.remove_patient(patient_id)
    
    return jsonify({
        'success': True,
        'patient_complete': is_complete,
        'queue_status': analyzer.get_queue_status()
    })


@app.route('/patient_exit', methods=['POST'])
def patient_exit():
    """Remove patient who has exited the scene"""
    data = request.json or {}
    patient_id = data.get('patient_id')
    
    if patient_id is None:
        return jsonify({'success': False, 'error': 'No patient_id provided'})
    
    analyzer.remove_patient(patient_id)
    
    return jsonify({
        'success': True,
        'queue_status': analyzer.get_queue_status()
    })


@app.route('/queue_status', methods=['GET'])
def queue_status():
    """Get current queue status"""
    return jsonify({
        'success': True,
        'queue_status': analyzer.get_queue_status(),
        'learning_stats': analyzer.cfr.get_statistics()
    })


@app.route('/reset', methods=['POST'])
def reset():
    """Reset the analyzer state"""
    global analyzer
    analyzer = DynamicRegretAnalyzer()
    return jsonify({'success': True, 'message': 'Analyzer reset'})


@app.route('/save_analysis', methods=['POST'])
def save_analysis():
    """
    Save regret analysis charts and data to output folder
    Call this at the end of a session to generate reports
    """
    data = request.json or {}
    output_dir = data.get('output_dir', 'output')
    
    try:
        result = analyzer.save_analysis(output_dir)
        
        # Convert absolute paths to relative paths for clarity
        if result.get('saved', False):
            import os
            server_dir = os.path.dirname(os.path.abspath(__file__))
            
            chart_path = result.get('chart_path', '')
            data_path = result.get('data_path', '')
            
            # Make paths relative to server directory if they're absolute
            if os.path.isabs(chart_path):
                chart_path = os.path.relpath(chart_path, server_dir)
            if os.path.isabs(data_path):
                data_path = os.path.relpath(data_path, server_dir)
            
            result['chart_path'] = chart_path.replace('\\', '/')  # Use forward slashes
            result['data_path'] = data_path.replace('\\', '/')
        
        return jsonify({
            'success': result.get('saved', False),
            **result
        })
    except Exception as e:
        print(f"Error saving analysis: {e}")
        import traceback
        traceback.print_exc()
        return jsonify({
            'success': False,
            'saved': False,
            'reason': f'Error: {str(e)}'
        })


@app.route('/health', methods=['GET'])
def health():
    return jsonify({'status': 'ok', 'queue_size': len(analyzer.state.patient_queue)})


if __name__ == '__main__':
    print("=" * 60)
    print("EMERGENCY UNIT SERVER - Online Regret Learning")
    print("=" * 60)
    print("\nEndpoints:")
    print("  POST /process_scenario - Parse text, spawn patients, get full plan")
    print("  POST /get_plan         - Get next action plan")
    print("  POST /complete_step    - Mark patient step complete")
    print("  POST /patient_exit     - Patient exits scene")
    print("  GET  /queue_status     - Current queue and learning stats")
    print("  POST /reset            - Reset analyzer")
    print("  POST /save_analysis    - Save regret charts to output/")
    print("\nExample input for /process_scenario:")
    print('  {"description": "Two patients: one with chest pain (critical), one broken arm (moderate)"}')
    print("\nServer running on http://localhost:5000")
    print("=" * 60)
    app.run(host='0.0.0.0', port=5000, debug=True)
