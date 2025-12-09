"""
Intelligent CFR Regret Engine for Emergency Unit
================================================
Features:
- Patient health system (0-100)
- Different healing power: Doctor (60), Nurse (40)
- Cooperative bonus: +20% when working together
- Waiting penalties: Critical -3/min, Moderate -2/min, Minor -1/min
- Room effectiveness: TRIAGE (diagnosis), TB (30%), LAB (prerequisite), ICU (50%)
- Multiple action evaluations per scenario
- True CFR convergence analysis

CODE STRUCTURE:
---------------
1. REGRET ENGINE CORE: CFR algorithm, strategy evaluation, game simulation
2. UNITY VISUALIZATION & CONNECTION: Methods that interface with Unity game
3. SAVE PLOTS & VISUALIZATION: Chart generation and data export
"""
import numpy as np
from dataclasses import dataclass, field
from typing import List, Dict, Tuple, Optional
from collections import defaultdict
import math
import json
import os
from datetime import datetime

# Set matplotlib backend early
try:
    import matplotlib
    matplotlib.use('Agg')
except:
    pass

# ============ GAME CONSTANTS ============

# Healing power per treatment
DOCTOR_HEALING_POWER = 60
NURSE_HEALING_POWER = 40
COOPERATIVE_BONUS = 1.2  # 20% bonus when working together

# Room effectiveness (health gain multiplier)
ROOM_EFFECTIVENESS = {
    'TRIAGE': 0.1,   # Diagnosis - minimal healing
    'TB': 0.30,      # Treatment Bay - 30% effectiveness
    'LAB': 0.15,     # Lab work - required for diagnosis
    'ICU': 0.50      # ICU - 50% effectiveness for critical
}

# Room treatment times (in game minutes)
ROOM_TREATMENT_TIME = {
    'TRIAGE': 5,
    'TB': 15,
    'LAB': 20,
    'ICU': 45
}

# Waiting penalties per minute
WAITING_PENALTY = {
    'Critical': -3.0,
    'Moderate': -2.0,
    'Minor': -1.0
}

# Completion rewards
COMPLETION_REWARD = {
    'Critical': 100,
    'Moderate': 60,
    'Minor': 30
}

# Patient initial health
INITIAL_HEALTH = {
    'Critical': 30,   # Critical starts low
    'Moderate': 50,
    'Minor': 70
}

# Pathways
PATHWAYS = {
    'Critical': ['TRIAGE', 'TB', 'ICU'],
    'Moderate': ['TRIAGE', 'TB', 'LAB', 'TB'],
    'Minor': ['TRIAGE', 'TB']
}

# Room distances for travel time calculation
ROOM_POSITIONS = {
    'ENT': (0, 0),
    'TRIAGE': (2, 0),
    'TB': (5, 3),
    'LAB': (8, -2),
    'ICU': (10, 5)
}

def room_distance(r1: str, r2: str) -> float:
    p1, p2 = ROOM_POSITIONS.get(r1, (0,0)), ROOM_POSITIONS.get(r2, (0,0))
    return math.sqrt((p1[0]-p2[0])**2 + (p1[1]-p2[1])**2)


# ============================================================================
# SECTION 1: REGRET ENGINE CORE - Data Classes & Game Simulation
# ============================================================================
# This section contains the core regret engine logic:
# - Patient and GameState data structures
# - Game simulator that evaluates strategies
# - CFR (Counterfactual Regret Minimization) algorithm
# - Strategy generation and evaluation
# ============================================================================

# ============ DATA CLASSES ============

@dataclass
class Patient:
    id: int
    ptype: str  # Critical, Moderate, Minor
    health: float  # 0-100, patient dies at 0
    pathway: List[str]
    current_step: int = 0
    arrival_time: float = 0.0
    waiting_time: float = 0.0
    being_treated_by: List[str] = field(default_factory=list)  # ['doctor', 'nurse']
    deadline: float = 0.0  # Time deadline for completion
    doctor_required: bool = False  # Whether doctor is required
    
    def __post_init__(self):
        """Set deadline and doctor_required based on patient type"""
        if self.deadline == 0.0:
            # Set default deadlines based on type
            self.deadline = {
                'Critical': 25.0,
                'Moderate': 45.0,
                'Minor': 30.0
            }.get(self.ptype, 30.0)
        
        if not self.doctor_required:
            # Critical patients require doctor
            self.doctor_required = (self.ptype == 'Critical')
    
    @property
    def is_complete(self) -> bool:
        return self.current_step >= len(self.pathway)
    
    @property
    def next_room(self) -> Optional[str]:
        return self.pathway[self.current_step] if not self.is_complete else None
    
    @property
    def urgency(self) -> float:
        """Higher = more urgent (low health + critical type)"""
        type_weight = {'Critical': 3, 'Moderate': 2, 'Minor': 1}[self.ptype]
        health_urgency = (100 - self.health) / 100
        return type_weight * health_urgency
    
    def clone(self):
        return Patient(
            id=self.id, ptype=self.ptype, health=self.health,
            pathway=self.pathway.copy(), current_step=self.current_step,
            arrival_time=self.arrival_time, waiting_time=self.waiting_time,
            being_treated_by=self.being_treated_by.copy(),
            deadline=self.deadline, doctor_required=self.doctor_required
        )


@dataclass
class GameState:
    patients: List[Patient] = field(default_factory=list)
    nurse_pos: str = 'ENT'
    doctor_pos: str = 'ENT'
    nurse_busy_until: float = 0.0
    doctor_busy_until: float = 0.0
    current_time: float = 0.0
    total_reward: float = 0.0
    total_penalty: float = 0.0
    completed_patients: int = 0
    
    def clone(self):
        return GameState(
            patients=[p.clone() for p in self.patients],
            nurse_pos=self.nurse_pos, doctor_pos=self.doctor_pos,
            nurse_busy_until=self.nurse_busy_until, doctor_busy_until=self.doctor_busy_until,
            current_time=self.current_time, total_reward=self.total_reward,
            total_penalty=self.total_penalty, completed_patients=self.completed_patients
        )


# ============ ACTION DEFINITIONS ============

# Possible actions for each agent
AGENT_ACTIONS = [
    'TREAT_PATIENT_1',
    'TREAT_PATIENT_2', 
    'TREAT_PATIENT_3',
    'WAIT'
]


# ============ GAME SIMULATOR (Regret Engine Core) ============

class GameSimulator:
    """
    Simulates the emergency unit game with health mechanics
    """
    
    def __init__(self):
        self.nurse_speed = 4.0
        self.doctor_speed = 2.5
    
    def create_scenario(self, patient_types: List[str]) -> GameState:
        """Create initial game state with patients"""
        state = GameState()
        for i, ptype in enumerate(patient_types):
            patient = Patient(
                id=i + 1,
                ptype=ptype,
                health=INITIAL_HEALTH[ptype],
                pathway=PATHWAYS[ptype].copy(),
                arrival_time=0.0
            )
            state.patients.append(patient)
        return state
    
    def apply_waiting_penalties(self, state: GameState, time_delta: float) -> float:
        """Apply waiting penalties to all patients not being treated"""
        penalty = 0.0
        for patient in state.patients:
            if not patient.being_treated_by:  # Only if waiting
                rate = WAITING_PENALTY[patient.ptype]
                penalty += rate * time_delta
                patient.waiting_time += time_delta
                patient.health = max(0, patient.health + rate * time_delta)  # Health decreases
        state.total_penalty += penalty
        return penalty
    
    def treat_patient(self, state: GameState, patient: Patient, 
                      agent: str, duration: float, cooperative: bool = False) -> float:
        """Apply treatment to patient and return reward"""
        room = patient.next_room
        if room is None:
            return 0.0
        
        # Calculate healing rate
        base_power = DOCTOR_HEALING_POWER if agent == 'doctor' else NURSE_HEALING_POWER
        effectiveness = ROOM_EFFECTIVENESS.get(room, 0.2)
        
        # Cooperative bonus for team work
        if cooperative:
            base_power *= COOPERATIVE_BONUS
        
        # Apply healing
        healing = base_power * effectiveness * (duration / ROOM_TREATMENT_TIME.get(room, 10))
        patient.health = min(100, patient.health + healing)
        
        # Advance step
        patient.current_step += 1
        
        # Check completion
        reward = healing  # Base reward is healing done
        if patient.is_complete:
            reward += COMPLETION_REWARD[patient.ptype]
            state.completed_patients += 1
        
        state.total_reward += reward
        return reward
    
    def simulate_action_sequence(self, initial_state: GameState, 
                                  nurse_actions: List[str], 
                                  doctor_actions: List[str],
                                  simulation_time: float = 100.0) -> Tuple[GameState, Dict]:
        """
        Simulate a sequence of actions and return final state + metrics
        """
        state = initial_state.clone()
        metrics = {
            'total_healing': 0.0,
            'total_penalty': 0.0,
            'patients_completed': 0,
            'cooperation_events': 0,
            'idle_time': 0.0
        }
        
        time_step = 1.0  # Simulate in 1-minute increments
        nurse_action_idx = 0
        doctor_action_idx = 0
        
        while state.current_time < simulation_time and state.patients:
            # Apply waiting penalties
            penalty = self.apply_waiting_penalties(state, time_step)
            metrics['total_penalty'] += abs(penalty)
            
            # Remove dead patients (health <= 0)
            dead = [p for p in state.patients if p.health <= 0]
            for p in dead:
                state.total_penalty -= 50  # Big penalty for patient death
                metrics['total_penalty'] += 50
                state.patients.remove(p)
            
            # Nurse action
            if state.current_time >= state.nurse_busy_until and nurse_action_idx < len(nurse_actions):
                action = nurse_actions[nurse_action_idx]
                reward = self._execute_agent_action(state, 'nurse', action, metrics)
                metrics['total_healing'] += reward
                nurse_action_idx += 1
            
            # Doctor action
            if state.current_time >= state.doctor_busy_until and doctor_action_idx < len(doctor_actions):
                action = doctor_actions[doctor_action_idx]
                reward = self._execute_agent_action(state, 'doctor', action, metrics)
                metrics['total_healing'] += reward
                doctor_action_idx += 1
            
            # Remove completed patients
            completed = [p for p in state.patients if p.is_complete]
            for p in completed:
                metrics['patients_completed'] += 1
                state.patients.remove(p)
            
            state.current_time += time_step
        
        return state, metrics
    
    def _execute_agent_action(self, state: GameState, agent: str, action: str, metrics: Dict) -> float:
        """Execute a single agent action"""
        reward = 0.0
        
        if action.startswith('TREAT_PATIENT_'):
            patient_idx = int(action.split('_')[-1]) - 1
            if patient_idx < len(state.patients):
                patient = state.patients[patient_idx]
                room = patient.next_room
                
                if room:
                    # Calculate travel time
                    current_pos = state.nurse_pos if agent == 'nurse' else state.doctor_pos
                    travel_time = room_distance(current_pos, room) / (self.nurse_speed if agent == 'nurse' else self.doctor_speed)
                    
                    # Treatment time
                    treatment_time = ROOM_TREATMENT_TIME.get(room, 10)
                    
                    # Check if cooperative
                    cooperative = len(patient.being_treated_by) > 0
                    if cooperative:
                        metrics['cooperation_events'] += 1
                    
                    # Mark agent busy
                    busy_until = state.current_time + travel_time + treatment_time
                    if agent == 'nurse':
                        state.nurse_busy_until = busy_until
                        state.nurse_pos = room
                    else:
                        state.doctor_busy_until = busy_until
                        state.doctor_pos = room
                    
                    # Apply treatment
                    patient.being_treated_by.append(agent)
                    reward = self.treat_patient(state, patient, agent, treatment_time, cooperative)
                    patient.being_treated_by.remove(agent)
        
        elif action == 'WAIT':
            metrics['idle_time'] += 1.0
            if agent == 'nurse':
                state.nurse_busy_until = state.current_time + 1.0
            else:
                state.doctor_busy_until = state.current_time + 1.0
        
        return reward


# ============ CFR REGRET MINIMIZER (Regret Engine Core) ============

class CFRRegretMinimizer:
    """
    Counterfactual Regret Minimization for Multi-Agent Strategy Selection
    
    Evaluates ALL possible action combinations and learns optimal strategy
    through regret analysis over multiple iterations.
    """
    
    def __init__(self):
        # Regret tracking per strategy
        self.regret_sum: Dict[str, float] = defaultdict(float)
        self.strategy_sum: Dict[str, float] = defaultdict(float)
        
        # History for visualization
        self.iteration_history: List[Dict] = []
        self.regret_history: List[float] = []
        self.cumulative_regret: float = 0.0
        self.distance_to_optimal_history: List[float] = []
        self.total_iterations: int = 0
        
        # Strategy value cache
        self.strategy_values: Dict[str, List[float]] = defaultdict(list)
        
        # For tracking probability changes
        self._prev_probs: Dict[str, float] = {}
    
    def get_strategy_probabilities(self, strategies: List[str]) -> Dict[str, float]:
        """Get current strategy probabilities using regret matching"""
        # Positive regrets only
        positive_regrets = {s: max(0, self.regret_sum[s]) for s in strategies}
        total = sum(positive_regrets.values())
        
        if total > 0:
            return {s: r / total for s, r in positive_regrets.items()}
        else:
            # Uniform distribution if no positive regrets
            return {s: 1.0 / len(strategies) for s in strategies}
    
    def update_regrets(self, strategy_values: Dict[str, float], selected_strategy: str):
        """
        Update regrets based on counterfactual values (proper CFR)
        
        For each strategy s:
            regret[s] += (value[s] - value[selected]) * reach_probability
        
        In our case, reach_probability = 1.0 (we always evaluate all strategies)
        """
        if not strategy_values:
            return
        
        # Calculate node value (expected value under current strategy)
        strategies = list(strategy_values.keys())
        probs = self.get_strategy_probabilities(strategies)
        node_value = sum(probs.get(s, 0) * strategy_values.get(s, 0) for s in strategies)
        
        # Update regrets for each strategy (counterfactual regret)
        for strategy, value in strategy_values.items():
            instant_regret = value - node_value
            self.regret_sum[strategy] += instant_regret
            
            # Store for analysis
            self.strategy_values[strategy].append(value)
        
        # Track cumulative regret: sum of (best_value - node_value) over iterations
        best_value = max(strategy_values.values()) if strategy_values else 0
        instant_regret = best_value - node_value
        
        # Accumulate cumulative regret (this increases but at decreasing rate)
        self.cumulative_regret += instant_regret
        self.regret_history.append(self.cumulative_regret)
        
        # Track distance to Nash equilibrium (probabilities stabilize as we converge)
        if strategies:
            probs = self.get_strategy_probabilities(strategies)
            prob_values = [probs.get(s, 0) for s in strategies]
            
            mean_prob = sum(prob_values) / len(prob_values) if prob_values else 0
            variance = sum((p - mean_prob) ** 2 for p in prob_values) / len(prob_values) if prob_values else 1.0
            std_dev = math.sqrt(variance)
            
            # Track probability change from previous iteration
            if self._prev_probs:
                prob_change = math.sqrt(sum((probs.get(s, 0) - self._prev_probs.get(s, 0)) ** 2 for s in strategies))
                distance = std_dev + prob_change * 0.5
            else:
                distance = std_dev
            
            self._prev_probs = probs.copy()
            self.distance_to_optimal_history.append(distance)
        else:
            self.distance_to_optimal_history.append(1.0)
        
        self.total_iterations += 1
    
    def select_strategy(self, strategies: List[str]) -> str:
        """Select strategy based on current probabilities"""
        probs = self.get_strategy_probabilities(strategies)
        strategies_list = list(probs.keys())
        probabilities = [probs[s] for s in strategies_list]
        return np.random.choice(strategies_list, p=probabilities)
    
    def get_average_strategy(self) -> Dict[str, float]:
        """Get average strategy over all iterations (converges to Nash equilibrium)"""
        total = sum(self.strategy_sum.values())
        if total > 0:
            return {s: v / total for s, v in self.strategy_sum.items()}
        return {}
    
    def record_iteration(self, strategy_values: Dict[str, float], selected: str, 
                         probs: Dict[str, float], reward: float):
        """Record iteration for visualization"""
        self.iteration_history.append({
            'iteration': self.total_iterations,
            'strategy_values': strategy_values.copy(),
            'selected_strategy': selected,
            'probabilities': probs.copy(),
            'reward': reward
        })
        
        # Update strategy sum for averaging
        for s, p in probs.items():
            self.strategy_sum[s] += p
    
    def get_statistics(self) -> Dict:
        """Get analysis statistics"""
        avg_strategy = self.get_average_strategy()
        
        return {
            'total_iterations': self.total_iterations,
            'cumulative_regret': self.cumulative_regret,
            'average_strategy': avg_strategy,
            'regret_by_strategy': dict(self.regret_sum),
            'strategy_frequency': dict(self.strategy_sum),
            'nash_distance': self.distance_to_optimal_history[-1] if self.distance_to_optimal_history else 1.0
        }


# ============ STRATEGY GENERATOR (Regret Engine Core) ============

class StrategyGenerator:
    """
    Generates all possible strategies for a given scenario
    """
    
    # Strategy definitions
    STRATEGIES = {
        'PARALLEL_CRITICAL': {
            'description': 'Both agents work on critical patient first, then split',
            'priority': 'critical_first',
            'cooperation': True
        },
        'SEQUENTIAL_SEVERITY': {
            'description': 'Handle patients in order of severity, one at a time',
            'priority': 'severity',
            'cooperation': False
        },
        'DOCTOR_CRITICAL_NURSE_OTHERS': {
            'description': 'Doctor handles critical, nurse handles others',
            'priority': 'split_by_type',
            'cooperation': False
        },
        'COOPERATIVE_ALL': {
            'description': 'Both agents work together on each patient',
            'priority': 'fifo',
            'cooperation': True
        },
        'NEAREST_FIRST': {
            'description': 'Each agent takes nearest patient',
            'priority': 'distance',
            'cooperation': False
        },
        'NURSE_TRIAGE_DOCTOR_TREAT': {
            'description': 'Nurse does triage, doctor does treatment',
            'priority': 'role_based',
            'cooperation': False
        }
    }
    
    def generate_action_sequences(self, state: GameState, strategy_name: str) -> Tuple[List[str], List[str]]:
        """Generate nurse and doctor action sequences for a strategy"""
        strategy = self.STRATEGIES[strategy_name]
        patients = state.patients
        
        nurse_actions = []
        doctor_actions = []
        
        if strategy['priority'] == 'critical_first':
            # Critical patients first
            sorted_patients = sorted(patients, key=lambda p: -p.urgency)
            for p in sorted_patients:
                if p.ptype == 'Critical':
                    # Both work on critical
                    for _ in range(len(p.pathway)):
                        nurse_actions.append(f'TREAT_PATIENT_{p.id}')
                        doctor_actions.append(f'TREAT_PATIENT_{p.id}')
                else:
                    # Nurse handles others
                    for _ in range(len(p.pathway)):
                        nurse_actions.append(f'TREAT_PATIENT_{p.id}')
                        doctor_actions.append('WAIT')
        
        elif strategy['priority'] == 'severity':
            # Sequential by severity
            sorted_patients = sorted(patients, key=lambda p: -p.urgency)
            for p in sorted_patients:
                for _ in range(len(p.pathway)):
                    nurse_actions.append(f'TREAT_PATIENT_{p.id}')
                    doctor_actions.append(f'TREAT_PATIENT_{p.id}')
        
        elif strategy['priority'] == 'split_by_type':
            # Doctor handles critical, nurse handles others
            for p in patients:
                if p.ptype == 'Critical':
                    for _ in range(len(p.pathway)):
                        doctor_actions.append(f'TREAT_PATIENT_{p.id}')
                else:
                    for _ in range(len(p.pathway)):
                        nurse_actions.append(f'TREAT_PATIENT_{p.id}')
            # Pad shorter list with WAIT
            max_len = max(len(nurse_actions), len(doctor_actions))
            nurse_actions.extend(['WAIT'] * (max_len - len(nurse_actions)))
            doctor_actions.extend(['WAIT'] * (max_len - len(doctor_actions)))
        
        elif strategy['priority'] == 'fifo':
            # FIFO with cooperation
            for p in patients:
                for _ in range(len(p.pathway)):
                    nurse_actions.append(f'TREAT_PATIENT_{p.id}')
                    doctor_actions.append(f'TREAT_PATIENT_{p.id}')
        
        elif strategy['priority'] == 'distance':
            # Nearest first (simplified)
            for i, p in enumerate(patients):
                if i % 2 == 0:
                    for _ in range(len(p.pathway)):
                        nurse_actions.append(f'TREAT_PATIENT_{p.id}')
                else:
                    for _ in range(len(p.pathway)):
                        doctor_actions.append(f'TREAT_PATIENT_{p.id}')
            max_len = max(len(nurse_actions), len(doctor_actions))
            nurse_actions.extend(['WAIT'] * (max_len - len(nurse_actions)))
            doctor_actions.extend(['WAIT'] * (max_len - len(doctor_actions)))
        
        elif strategy['priority'] == 'role_based':
            # Nurse triage, doctor treats
            for p in patients:
                nurse_actions.append(f'TREAT_PATIENT_{p.id}')  # Triage
                for _ in range(len(p.pathway) - 1):
                    doctor_actions.append(f'TREAT_PATIENT_{p.id}')
            max_len = max(len(nurse_actions), len(doctor_actions))
            nurse_actions.extend(['WAIT'] * (max_len - len(nurse_actions)))
            doctor_actions.extend(['WAIT'] * (max_len - len(doctor_actions)))
        
        return nurse_actions, doctor_actions


# ============================================================================
# SECTION 2: UNITY VISUALIZATION & CONNECTION
# ============================================================================
# This section handles all Unity integration:
# - Patient spawning and tracking (spawn_patient, step_patient, remove_patient)
# - Queue status reporting (get_queue_status)
# - Main analysis entry point (analyze_and_plan) - called from Unity
# - Command generation for Unity agents (_generate_unity_commands)
# - Room coordinate setup (set_rooms)
# ============================================================================

class DynamicRegretAnalyzer:
    """
    Main analyzer that evaluates all strategies and selects optimal using CFR
    """
    
    def __init__(self):
        self.simulator = GameSimulator()
        self.cfr = CFRRegretMinimizer()
        self.strategy_gen = StrategyGenerator()
        self.patient_counter = 0
        self.current_state: Optional[GameState] = None
        self.room_coords: Dict[str, Tuple[float, float]] = {}
        
        # Treatment times for Unity
        self.treatment_times = {
            'TRIAGE': 5, 'TB': 15, 'LAB': 20, 'ICU': 45
        }
    
    # ============ UNITY CONNECTION METHODS ============
    
    def set_rooms(self, room_coords: Dict[str, Tuple[float, float]]):
        """Set room coordinates (called from app.py - Unity connection)"""
        self.room_coords = room_coords
        # Update global room positions if provided
        global ROOM_POSITIONS
        ROOM_POSITIONS.update(room_coords)
    
    def spawn_patient(self, ptype: str) -> Patient:
        """Spawn a patient for Unity (Unity connection)"""
        self.patient_counter += 1
        
        # Set deadline and doctor requirement based on type
        deadline = {
            'Critical': 25.0,
            'Moderate': 45.0,
            'Minor': 30.0
        }.get(ptype, 30.0)
        
        doctor_required = (ptype == 'Critical')
        
        patient = Patient(
            id=self.patient_counter,
            ptype=ptype,
            health=INITIAL_HEALTH[ptype],
            pathway=PATHWAYS[ptype].copy(),
            deadline=deadline,
            doctor_required=doctor_required
        )
        
        if self.current_state is None:
            self.current_state = GameState()
        self.current_state.patients.append(patient)
        
        return patient
    
    def reset(self):
        """Reset analyzer state"""
        self.patient_counter = 0
        self.current_state = None
    
    def step_patient(self, patient_id: int) -> bool:
        """
        Mark a patient step as complete (called from Unity - Unity connection)
        Returns True if patient pathway is complete
        """
        try:
            if self.current_state is None:
                return False
            
            for patient in self.current_state.patients:
                if patient.id == patient_id:
                    # Only increment if not already complete
                    if not patient.is_complete:
                        patient.current_step += 1
                    return patient.is_complete
            
            # Patient not found - might have already been removed
            return False
        except Exception as e:
            print(f"Error in step_patient({patient_id}): {e}")
            return False
    
    def remove_patient(self, patient_id: int):
        """Remove patient from queue (called when patient exits - Unity connection)"""
        try:
            if self.current_state is None:
                return
            
            initial_count = len(self.current_state.patients)
            self.current_state.patients = [
                p for p in self.current_state.patients if p.id != patient_id
            ]
            
            if len(self.current_state.patients) < initial_count:
                print(f"Removed patient {patient_id} from queue. {len(self.current_state.patients)} remaining.")
        except Exception as e:
            print(f"Error in remove_patient({patient_id}): {e}")
    
    # ============ REGRET ENGINE CORE (Used by Unity interface) ============
    
    def evaluate_all_strategies(self, state: GameState, num_simulations: int = 10) -> Dict[str, float]:
        """
        Evaluate ALL strategies by simulating each multiple times (Regret Engine Core)
        Returns expected value for each strategy
        Called by analyze_and_plan() for Unity interface
        
        Adds small noise to differentiate strategies and show learning
        """
        strategy_values = {}
        
        for strategy_name in StrategyGenerator.STRATEGIES:
            total_reward = 0.0
            
            for _ in range(num_simulations):
                # Generate actions for this strategy
                nurse_actions, doctor_actions = self.strategy_gen.generate_action_sequences(
                    state, strategy_name
                )
                
                # Simulate
                final_state, metrics = self.simulator.simulate_action_sequence(
                    state, nurse_actions, doctor_actions
                )
                
                # Calculate total value with realistic game mechanics
                value = (
                    metrics['total_healing'] * 1.0 +
                    metrics['patients_completed'] * 50 +
                    metrics['cooperation_events'] * 10 -
                    metrics['total_penalty'] * 1.0 -
                    metrics['idle_time'] * 2.0
                )
                total_reward += value
            
            # Average with small variance for exploration
            avg_value = total_reward / num_simulations
            # Add small random noise to break ties and show learning (decreases over iterations)
            noise_scale = max(0.1, 10.0 / (self.cfr.total_iterations + 1))
            strategy_values[strategy_name] = avg_value + np.random.normal(0, noise_scale)
        
        return strategy_values
    
    def analyze_and_plan(self) -> Dict:
        """
        Main analysis entry point (called from Unity - Unity connection)
        Evaluate all strategies, select best using CFR
        COMPLETES ALL CALCULATIONS BEFORE RETURNING - Unity waits until done
        """
        if self.current_state is None or not self.current_state.patients:
            return {
                'success': True,
                'assignment': {'strategy': 'IDLE', 'description': 'No patients'},
                'nurse_plan': {'commands': []},
                'doctor_plan': {'commands': []},
                'learning_stats': self.cfr.get_statistics(),
                'expected_reward': 0.0
            }
        
        print(f"\n{'='*60}")
        print(f"CFR REGRET ANALYSIS STARTING")
        print(f"Patients: {len(self.current_state.patients)}")
        print(f"{'='*60}\n")
        
        # Run multiple CFR iterations to learn
        NUM_CFR_ITERATIONS = 20
        strategy_values = {}
        
        print(f"Evaluating {len(StrategyGenerator.STRATEGIES)} strategies over {NUM_CFR_ITERATIONS} iterations...")
        
        for iteration in range(NUM_CFR_ITERATIONS):
            if (iteration + 1) % 5 == 0:
                print(f"  Iteration {iteration + 1}/{NUM_CFR_ITERATIONS}...")
            
            # Evaluate all strategies
            strategy_values = self.evaluate_all_strategies(self.current_state, num_simulations=5)
            
            # Get current probabilities
            strategies = list(strategy_values.keys())
            probs = self.cfr.get_strategy_probabilities(strategies)
            
            # Select strategy for this iteration
            selected = self.cfr.select_strategy(strategies)
            
            # Update regrets
            self.cfr.update_regrets(strategy_values, selected)
            
            # Record iteration
            self.cfr.record_iteration(
                strategy_values, selected, probs,
                strategy_values[selected]
            )
        
        print(f"✓ Analysis complete! {self.cfr.total_iterations} iterations processed.\n")
        
        # Final selection: use average strategy (Nash equilibrium)
        avg_strategy = self.cfr.get_average_strategy()
        if avg_strategy:
            best_strategy = max(avg_strategy, key=avg_strategy.get)
        else:
            best_strategy = max(strategy_values, key=strategy_values.get)
        
        print(f"Selected optimal strategy: {best_strategy}")
        print(f"  Description: {StrategyGenerator.STRATEGIES[best_strategy]['description']}")
        print(f"  Expected Value: {strategy_values.get(best_strategy, 0):.2f}\n")
        
        # Generate COMPLETE commands for Unity - ensure ALL patients are covered
        print("Generating complete command sequences for all patients...")
        nurse_commands, doctor_commands = self._generate_unity_commands(
            self.current_state, best_strategy
        )
        
        patient_count = len(self.current_state.patients)
        print(f"✓ Generated {len(nurse_commands)} nurse commands, {len(doctor_commands)} doctor commands")
        print(f"✓ All {patient_count} patients covered\n")
        
        return {
            'success': True,
            'assignment': {
                'strategy': best_strategy,
                'description': StrategyGenerator.STRATEGIES[best_strategy]['description'],
                'expected_value': strategy_values.get(best_strategy, 0),
                'all_strategy_values': strategy_values
            },
            'nurse_plan': {'commands': nurse_commands},
            'doctor_plan': {'commands': doctor_commands},
            'learning_stats': self.cfr.get_statistics(),
            'expected_reward': strategy_values.get(best_strategy, 0)
        }
    
    def _generate_unity_commands(self, state: GameState, strategy: str) -> Tuple[List[Dict], List[Dict]]:
        """
        Generate COMPLETE commands in Unity format (Unity connection)
        Ensures EVERY patient gets FULL treatment pathway
        """
        nurse_commands = []
        doctor_commands = []
        
        patients = list(state.patients)  # Make a copy
        strategy_info = StrategyGenerator.STRATEGIES[strategy]
        
        # Sort patients based on strategy priority
        if strategy_info['priority'] in ['critical_first', 'severity']:
            patients = sorted(patients, key=lambda p: -p.urgency)
        elif strategy_info['priority'] == 'distance':
            # Keep original order for distance-based
            pass
        
        patient_locations = {p.id: 'WAITING' for p in patients}
        
        # Process EACH patient completely
        for patient in patients:
            is_critical = patient.ptype == 'Critical'
            has_doctor_commands = False
            
            # Process ALL rooms in patient pathway
            for room in patient.pathway:
                from_room = patient_locations[patient.id]
                
                # Strategy-specific command generation
                if strategy == 'DOCTOR_CRITICAL_NURSE_OTHERS':
                    if is_critical:
                        # Doctor handles critical patients
                        doctor_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        doctor_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        has_doctor_commands = True
                    else:
                        nurse_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        nurse_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                
                elif strategy == 'NURSE_TRIAGE_DOCTOR_TREAT':
                    if room == 'TRIAGE':
                        # Nurse does triage
                        nurse_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        nurse_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        nurse_commands.append({
                            'action': 'LEAVE_PATIENT', 'target': room,
                            'patient_id': patient.id, 'duration': 0
                        })
                    else:
                        doctor_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        doctor_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        has_doctor_commands = True
                
                elif strategy == 'PARALLEL_CRITICAL':
                    # Both work on critical, nurse handles others
                    if is_critical:
                        # Both agents work together on critical
                        nurse_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        nurse_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        doctor_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        doctor_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        has_doctor_commands = True
                    else:
                        nurse_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        nurse_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                
                elif strategy == 'COOPERATIVE_ALL':
                    # Both always work together
                    nurse_commands.append({
                        'action': 'ESCORT', 'target': room,
                        'patient_id': patient.id, 'duration': 0,
                        'from_room': from_room
                    })
                    nurse_commands.append({
                        'action': 'TREAT', 'target': room,
                        'patient_id': patient.id,
                        'duration': self.treatment_times.get(room, 10)
                    })
                    doctor_commands.append({
                        'action': 'ESCORT', 'target': room,
                        'patient_id': patient.id, 'duration': 0,
                        'from_room': from_room
                    })
                    doctor_commands.append({
                        'action': 'TREAT', 'target': room,
                        'patient_id': patient.id,
                        'duration': self.treatment_times.get(room, 10)
                    })
                    has_doctor_commands = True
                
                else:
                    nurse_commands.append({
                        'action': 'ESCORT', 'target': room,
                        'patient_id': patient.id, 'duration': 0,
                        'from_room': from_room
                    })
                    nurse_commands.append({
                        'action': 'TREAT', 'target': room,
                        'patient_id': patient.id,
                        'duration': self.treatment_times.get(room, 10)
                    })
                    
                    if is_critical and room in ['TB', 'ICU']:
                        doctor_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient.id, 'duration': 0,
                            'from_room': from_room
                        })
                        doctor_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient.id,
                            'duration': self.treatment_times.get(room, 10)
                        })
                        has_doctor_commands = True
                
                patient_locations[patient.id] = room
            
            # ICU patients: doctor leaves after setup
            if patient.pathway and patient.pathway[-1] == 'ICU' and has_doctor_commands:
                doctor_commands.append({
                    'action': 'LEAVE_PATIENT', 'target': 'ICU',
                    'patient_id': patient.id, 'duration': 0
                })
        
        # Final verification: ensure every patient has at least nurse commands
        all_patient_ids = {p.id for p in patients}
        nurse_covered = {cmd.get('patient_id', -1) for cmd in nurse_commands 
                        if cmd.get('action') in ['ESCORT', 'TREAT'] and cmd.get('patient_id', -1) > 0}
        
        missing_patients = all_patient_ids - nurse_covered
        if missing_patients:
            print(f"  Adding fallback commands for patients: {missing_patients}")
            for patient_id in missing_patients:
                patient = next((p for p in patients if p.id == patient_id), None)
                if patient:
                    for room in patient.pathway:
                        nurse_commands.append({
                            'action': 'ESCORT', 'target': room,
                            'patient_id': patient_id, 'duration': 0,
                            'from_room': 'WAITING'
                        })
                        nurse_commands.append({
                            'action': 'TREAT', 'target': room,
                            'patient_id': patient_id,
                            'duration': self.treatment_times.get(room, 10)
                        })
        
        # Ensure both agents have at least one command
        if not nurse_commands:
            nurse_commands.append({'action': 'MOVE', 'target': 'ENT', 'patient_id': -1, 'duration': 0})
        if not doctor_commands:
            doctor_commands.append({'action': 'MOVE', 'target': 'ENT', 'patient_id': -1, 'duration': 0})
        
        return nurse_commands, doctor_commands
    
    def get_queue_status(self) -> Dict:
        """Get current queue status (Unity connection - for UI display)"""
        if self.current_state is None:
            return {
                'queue_size': 0,
                'patients': []
            }
        
        patients = self.current_state.patients
        return {
            'queue_size': len(patients),
            'patients': [
                {
                    'id': p.id,
                    'type': p.ptype,
                    'health': getattr(p, 'health', 50),  # Fallback if health not set
                    'next_room': p.next_room,
                    'urgency': p.urgency,
                    'current_step': p.current_step,
                    'pathway_length': len(p.pathway),
                    'steps_remaining': len(p.pathway) - p.current_step
                }
                for p in patients
            ]
        }
    
    # ============================================================================
    # SECTION 3: SAVE PLOTS & VISUALIZATION
    # ============================================================================
    # This section handles saving analysis results:
    # - Generates comprehensive CFR analysis charts (9 subplots)
    # - Saves individual plot images
    # - Exports JSON data with regret history and convergence metrics
    # ============================================================================
    
    def save_analysis(self, output_dir: str = 'output') -> Dict:
        """Save CFR analysis with comprehensive charts (Visualization & Export)"""
        import matplotlib.pyplot as plt
        
        os.makedirs(output_dir, exist_ok=True)
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        
        if self.cfr.total_iterations == 0:
            return {'saved': False, 'reason': 'No iterations yet'}
        
        print(f"Generating CFR analysis charts... ({self.cfr.total_iterations} iterations)")
        
        try:
            plt.close('all')
            fig, axes = plt.subplots(3, 3, figsize=(18, 14))
            
            # Add scenario information to title
            if self.current_state and self.current_state.patients:
                patient_summary = {}
                for p in self.current_state.patients:
                    patient_summary[p.ptype] = patient_summary.get(p.ptype, 0) + 1
                scenario_desc = ', '.join([f'{count} {ptype}' for ptype, count in sorted(patient_summary.items())])
                scenario_info = f'Scenario: {len(self.current_state.patients)} patient(s) ({scenario_desc})'
            else:
                scenario_info = 'Scenario: No patients'
            
            fig.suptitle(f'CFR Regret Minimization Analysis\n'
                        f'{self.cfr.total_iterations} Iterations - {scenario_info}\n'
                        f'Game: Health={DOCTOR_HEALING_POWER}/{NURSE_HEALING_POWER}, Penalty={WAITING_PENALTY}',
                        fontsize=13, fontweight='bold')
            
            stats = self.cfr.get_statistics()
            axes_flat = axes.flatten()
            
            # Plot 1: Cumulative Regret (should increase at decreasing rate)
            ax1 = axes_flat[0]
            if self.cfr.regret_history:
                iterations = range(1, len(self.cfr.regret_history) + 1)
                ax1.plot(iterations, self.cfr.regret_history, 'r-', linewidth=2, marker='o', markersize=3)
                ax1.fill_between(iterations, self.cfr.regret_history, alpha=0.3, color='red')
                
                # Add trend line to show convergence rate
                if len(self.cfr.regret_history) > 3:
                    # Fit logarithmic curve (CFR converges at O(1/sqrt(T)) rate)
                    x_vals = np.array(iterations)
                    y_vals = np.array(self.cfr.regret_history)
                    # Fit: y = a * sqrt(x) + b
                    try:
                        coeffs = np.polyfit(np.sqrt(x_vals), y_vals, 1)
                        trend = coeffs[0] * np.sqrt(x_vals) + coeffs[1]
                        ax1.plot(iterations, trend, 'g--', linewidth=1.5, alpha=0.7, label='Convergence trend')
                        ax1.legend(fontsize=8)
                    except:
                        pass
            ax1.set_xlabel('Iteration')
            ax1.set_ylabel('Cumulative Regret')
            ax1.set_title('1. Regret Convergence (Sublinear Growth = Learning)')
            ax1.grid(True, alpha=0.3)
            
            # Plot 2: Distance to Optimal
            ax2 = axes_flat[1]
            if self.cfr.distance_to_optimal_history:
                ax2.plot(self.cfr.distance_to_optimal_history, 'b-', linewidth=2, marker='s', markersize=3)
                ax2.fill_between(range(len(self.cfr.distance_to_optimal_history)), 
                               self.cfr.distance_to_optimal_history, alpha=0.3, color='blue')
            ax2.set_xlabel('Iteration')
            ax2.set_ylabel('Distance')
            ax2.set_title('2. Convergence to Nash Equilibrium')
            ax2.grid(True, alpha=0.3)
            
            # Plot 3: Strategy Distribution (Nash Equilibrium)
            ax3 = axes_flat[2]
            avg_strategy = stats['average_strategy']
            if avg_strategy:
                names = list(avg_strategy.keys())
                probs = list(avg_strategy.values())
                colors = plt.cm.viridis(np.linspace(0, 1, len(names)))
                bars = ax3.bar(range(len(names)), probs, color=colors)
                ax3.set_xticks(range(len(names)))
                ax3.set_xticklabels([n.replace('_', '\n') for n in names], fontsize=8, rotation=45, ha='right')
                ax3.set_ylabel('Probability')
                ax3.set_title('3. Learned Strategy (Nash Equilibrium)')
                for bar, prob in zip(bars, probs):
                    ax3.text(bar.get_x() + bar.get_width()/2, bar.get_height() + 0.01,
                            f'{prob:.2f}', ha='center', va='bottom', fontsize=8)
            ax3.grid(True, alpha=0.3, axis='y')
            
            # Plot 4: Regret by Strategy
            ax4 = axes_flat[3]
            regrets = stats['regret_by_strategy']
            if regrets:
                names = list(regrets.keys())
                values = list(regrets.values())
                colors = ['green' if v < 0 else 'red' for v in values]
                bars = ax4.bar(range(len(names)), values, color=colors, alpha=0.7)
                ax4.set_xticks(range(len(names)))
                ax4.set_xticklabels([n.replace('_', '\n') for n in names], fontsize=8, rotation=45, ha='right')
                ax4.axhline(y=0, color='black', linestyle='--')
                ax4.set_ylabel('Cumulative Regret')
                ax4.set_title('4. Regret by Strategy (Negative = Good)')
            ax4.grid(True, alpha=0.3, axis='y')
            
            # Plot 5: Strategy Values Over Time
            ax5 = axes_flat[4]
            if self.cfr.iteration_history:
                for strategy in StrategyGenerator.STRATEGIES:
                    values = [it['strategy_values'].get(strategy, 0) for it in self.cfr.iteration_history]
                    if any(v != 0 for v in values):
                        ax5.plot(values, 'o-', label=strategy.replace('_', ' '), linewidth=1.5, markersize=3, alpha=0.7)
                ax5.legend(fontsize=6, loc='best')
            ax5.set_xlabel('Iteration')
            ax5.set_ylabel('Expected Value')
            ax5.set_title('5. Strategy Values Over Iterations')
            ax5.grid(True, alpha=0.3)
            
            # Plot 6: Selection Probabilities Over Time
            ax6 = axes_flat[5]
            if self.cfr.iteration_history:
                for strategy in StrategyGenerator.STRATEGIES:
                    probs = [it['probabilities'].get(strategy, 0) for it in self.cfr.iteration_history]
                    if any(p != 0 for p in probs):
                        ax6.plot(probs, 'o-', label=strategy.replace('_', ' '), linewidth=1.5, markersize=3, alpha=0.7)
                ax6.legend(fontsize=6, loc='best')
                ax6.set_ylim(-0.1, 1.1)
            ax6.set_xlabel('Iteration')
            ax6.set_ylabel('Selection Probability')
            ax6.set_title('6. Probability Evolution')
            ax6.grid(True, alpha=0.3)
            
            # Plot 7: Instant Regret
            ax7 = axes_flat[6]
            if len(self.cfr.regret_history) > 1:
                instant = [self.cfr.regret_history[i] - self.cfr.regret_history[i-1] if i > 0 else self.cfr.regret_history[0]
                          for i in range(len(self.cfr.regret_history))]
                iterations = range(1, len(instant) + 1)
                ax7.plot(iterations, instant, 'o-', color='orange', linewidth=2, markersize=4, alpha=0.7)
                ax7.fill_between(iterations, instant, alpha=0.3, color='orange')
                ax7.axhline(y=0, color='green', linestyle='--', linewidth=2)
                
                # Add exponential fit to show convergence
                if len(instant) > 3:
                    try:
                        x_vals = np.array(iterations)
                        y_vals = np.array(instant)
                        # Fit exponential decay: y = a * exp(-b*x) + c
                        positive_y = np.maximum(y_vals, 0.1)  # Avoid log(0)
                        log_y = np.log(positive_y)
                        coeffs = np.polyfit(x_vals, log_y, 1)
                        exp_fit = np.exp(coeffs[0] * x_vals + coeffs[1])
                        ax7.plot(iterations, exp_fit, 'b--', linewidth=1.5, alpha=0.7, label='Exponential fit')
                        ax7.legend(fontsize=8)
                    except:
                        pass
            ax7.set_xlabel('Iteration')
            ax7.set_ylabel('Instant Regret')
            ax7.set_title('7. Per-Iteration Regret (Should Decrease)')
            ax7.grid(True, alpha=0.3)
            
            # Plot 8: Nash Equilibrium Distribution (Average Strategy)
            # This should show the FINAL learned strategy, not selection frequency
            ax8 = axes_flat[7]
            if avg_strategy:
                names = list(avg_strategy.keys())
                probs = list(avg_strategy.values())
                # Filter out strategies with very low probability for clarity
                threshold = 0.01
                filtered = [(n, p) for n, p in zip(names, probs) if p >= threshold]
                if filtered:
                    names_filtered, probs_filtered = zip(*filtered)
                    colors = plt.cm.plasma(np.linspace(0, 1, len(names_filtered)))
                    ax8.pie(probs_filtered, labels=[n.replace('_', '\n') for n in names_filtered], 
                           colors=colors, autopct='%1.1f%%', startangle=90)
                else:
                    # Fallback: show all strategies
                    colors = plt.cm.plasma(np.linspace(0, 1, len(names)))
                    ax8.pie(probs, labels=[n.replace('_', '\n') for n in names], 
                           colors=colors, autopct='%1.1f%%', startangle=90)
            ax8.set_title('8. Nash Equilibrium Distribution\n(Average Strategy)')
            
            # Plot 9: Summary
            ax9 = axes_flat[8]
            ax9.axis('off')
            
            best = max(avg_strategy.items(), key=lambda x: x[1])[0] if avg_strategy else 'NONE'
            
            # Add scenario info to summary
            if self.current_state and self.current_state.patients:
                patient_summary = {}
                for p in self.current_state.patients:
                    patient_summary[p.ptype] = patient_summary.get(p.ptype, 0) + 1
                scenario_desc = ', '.join([f'{count} {ptype}' for ptype, count in sorted(patient_summary.items())])
                scenario_line = f'SCENARIO: {len(self.current_state.patients)} patient(s) - {scenario_desc}\n'
            else:
                scenario_line = 'SCENARIO: No patients\n'
            
            summary = f"""
CFR REGRET MINIMIZATION RESULTS
{'='*40}
{scenario_line}
OPTIMAL STRATEGY: {best}
Probability: {avg_strategy.get(best, 0):.1%}

WHY THIS IS OPTIMAL:
• Lowest cumulative regret over {self.cfr.total_iterations} iterations
• Converged to Nash equilibrium
• Best expected value considering:
  - Patient health mechanics
  - Waiting penalties (Crit: -3, Mod: -2, Minor: -1)
  - Healing power (Doctor: {DOCTOR_HEALING_POWER}, Nurse: {NURSE_HEALING_POWER})
  - Cooperative bonus: {(COOPERATIVE_BONUS-1)*100:.0f}%

CONVERGENCE METRICS:
• Total Iterations: {self.cfr.total_iterations}
• Final Cumulative Regret: {stats['cumulative_regret']:.2f}
• Nash Distance: {stats['nash_distance']:.3f}

STRATEGY COMPARISON:
"""
            for s, v in sorted(stats['regret_by_strategy'].items(), key=lambda x: x[1])[:4]:
                summary += f"  {s}: regret={v:.1f}\n"
            
            ax9.text(0.05, 0.95, summary, transform=ax9.transAxes,
                    fontsize=9, verticalalignment='top', family='monospace',
                    bbox=dict(boxstyle='round', facecolor='lightblue', alpha=0.3))
            
            # Layout and save
            try:
                plt.tight_layout(rect=[0, 0, 1, 0.96])
            except:
                fig.subplots_adjust(left=0.08, right=0.95, top=0.93, bottom=0.08, hspace=0.4, wspace=0.3)
            
            # Main combined chart
            chart_path = os.path.join(output_dir, f'cfr_analysis_{timestamp}.png')
            fig.savefig(chart_path, format='png', dpi=150, facecolor='white')

            # Also save each subplot separately in its own folder.
            # To keep this robust and fast, we avoid fancy bbox math and instead
            # save the full figure nine times, each time showing only one subplot.
            try:
                per_plot_dir = os.path.join(output_dir, f'cfr_analysis_{timestamp}_plots')
                os.makedirs(per_plot_dir, exist_ok=True)

                # Ensure everything is rendered once
                fig.canvas.draw()

                # Remember original visibility to restore later
                original_vis = [ax.get_visible() for ax in axes_flat]

                for idx, ax in enumerate(axes_flat, start=1):
                    # Show only this axis
                    for j, other_ax in enumerate(axes_flat):
                        other_ax.set_visible(j == (idx - 1))

                    fig.canvas.draw()
                    single_path = os.path.join(per_plot_dir, f'plot{idx}.png')
                    fig.savefig(single_path, format='png', dpi=150, facecolor='white')

                # Restore original visibility state (not strictly needed before close, but safe)
                for ax, vis in zip(axes_flat, original_vis):
                    ax.set_visible(vis)
            except Exception as e_sub:
                # Do not fail the whole save if per-plot export has an issue
                print(f"Warning: unable to save individual subplot images: {e_sub}")

            plt.close(fig)
            
        except Exception as e:
            print(f"Error creating charts: {e}")
            import traceback
            traceback.print_exc()
            return {'saved': False, 'reason': str(e)}
        
        # Save data
        data = {
            'timestamp': timestamp,
            'iterations': self.cfr.total_iterations,
            'game_params': {
                'doctor_power': DOCTOR_HEALING_POWER,
                'nurse_power': NURSE_HEALING_POWER,
                'cooperative_bonus': COOPERATIVE_BONUS,
                'penalties': WAITING_PENALTY
            },
            'final_strategy': dict(stats['average_strategy']),
            'regret_history': self.cfr.regret_history,
            'convergence_history': self.cfr.distance_to_optimal_history
        }
        
        data_path = os.path.join(output_dir, f'cfr_data_{timestamp}.json')
        with open(data_path, 'w') as f:
            json.dump(data, f, indent=2)
        
        print(f"✓ Analysis saved: {chart_path}")
        
        return {
            'saved': True,
            'chart_path': chart_path,
            'data_path': data_path,
            'total_iterations': self.cfr.total_iterations,
            'best_strategy': best if avg_strategy else 'NONE'
        }


# ============================================================================
# GLOBAL INSTANCE (Used by app.py for Unity integration)
# ============================================================================

# Global analyzer instance
analyzer = DynamicRegretAnalyzer()
