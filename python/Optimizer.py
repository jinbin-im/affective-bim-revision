#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
ALIS design optimization script (Transformer-based).
Runs a grid search for the design that maximizes target_emotion.
Categorical variables are one-hot encoded; continuous variables use a fine grid.
"""

import sys
import os
import pickle
import json
from itertools import product
from typing import Dict, Optional, List

import numpy as np

if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except:
        import codecs
        sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'strict')
        sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'strict')

try:
    import joblib
except ImportError:
    print("[ERROR] joblib not installed. Run: pip install joblib")
    sys.exit(1)

# ======================================================================
# Transformer class definition (needed to load the joblib model)
# ======================================================================
try:
    import torch
    import torch.nn as nn

    class TabularTransformer(nn.Module):
        def __init__(self, d_input, d_model=64, n_heads=4, n_layers=2,
                     d_ff=128, d_output=10, dropout=0.1):
            super().__init__()
            self.input_proj = nn.Linear(d_input, d_model)
            self.pos_embed = nn.Parameter(torch.randn(1, 1, d_model) * 0.02)
            encoder_layer = nn.TransformerEncoderLayer(
                d_model=d_model, nhead=n_heads, dim_feedforward=d_ff,
                dropout=dropout, batch_first=True)
            self.transformer = nn.TransformerEncoder(encoder_layer, num_layers=n_layers)
            self.output_head = nn.Sequential(
                nn.LayerNorm(d_model), nn.Linear(d_model, d_ff), nn.GELU(),
                nn.Dropout(dropout), nn.Linear(d_ff, d_output))

        def forward(self, x):
            h = self.input_proj(x).unsqueeze(1) + self.pos_embed
            h = self.transformer(h)
            return self.output_head(h.squeeze(1))

    class TransformerRegressor:
        def __init__(self, d_model=64, n_heads=4, n_layers=2, d_ff=128,
                     lr=0.001, epochs=500, weight_decay=1e-4, patience=50,
                     dropout=0.1, seed=42):
            self.d_model = d_model; self.n_heads = n_heads
            self.n_layers = n_layers; self.d_ff = d_ff
            self.lr = lr; self.epochs = epochs
            self.weight_decay = weight_decay; self.patience = patience
            self.dropout = dropout; self.seed = seed; self.model = None

        def fit(self, X, y):
            torch.manual_seed(self.seed); np.random.seed(self.seed)
            d_in, d_out = X.shape[1], y.shape[1] if y.ndim > 1 else 1
            self.model = TabularTransformer(d_in, self.d_model, self.n_heads,
                self.n_layers, self.d_ff, d_out, self.dropout)
            X_t, y_t = torch.FloatTensor(X), torch.FloatTensor(y)
            opt = torch.optim.AdamW(self.model.parameters(), lr=self.lr,
                                     weight_decay=self.weight_decay)
            sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=self.epochs)
            crit = nn.MSELoss(); best_loss = float('inf'); pat = 0; best_st = None
            self.model.train()
            for ep in range(self.epochs):
                opt.zero_grad(); pred = self.model(X_t)
                loss = crit(pred, y_t); loss.backward()
                torch.nn.utils.clip_grad_norm_(self.model.parameters(), 1.0)
                opt.step(); sched.step()
                if loss.item() < best_loss:
                    best_loss = loss.item()
                    best_st = {k: v.clone() for k, v in self.model.state_dict().items()}
                    pat = 0
                else:
                    pat += 1
                    if pat >= self.patience: break
            if best_st: self.model.load_state_dict(best_st)
            return self

        def predict(self, X):
            self.model.eval()
            with torch.no_grad():
                return self.model(torch.FloatTensor(X)).numpy()

        def get_params(self, deep=True):
            return {'d_model': self.d_model, 'n_heads': self.n_heads,
                    'n_layers': self.n_layers, 'd_ff': self.d_ff,
                    'lr': self.lr, 'epochs': self.epochs,
                    'weight_decay': self.weight_decay, 'patience': self.patience,
                    'dropout': self.dropout, 'seed': self.seed}

        def set_params(self, **p):
            for k, v in p.items(): setattr(self, k, v)
            return self

except ImportError:
    print("[WARN] PyTorch not installed.")


# ======================================================================
# Settings
# ======================================================================
class Config:
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    MODEL_PATH = os.path.join(SCRIPT_DIR, 'inference_transformer.pkl')
    PREPROCESSOR_PATH = os.path.join(SCRIPT_DIR, 'preprocessor.pkl')
    CURRENT_ANALYSIS = r'C:\Temp\current_analysis.txt'
    TARGET_EMOTION_FILE = r'C:\Temp\target_emotion.txt'
    OUTPUT_FILE = r'C:\Temp\revised_analysis.txt'
    DELTA = 0.1


# ======================================================================
# Search grid (fine steps for continuous variables)
# ======================================================================
SEARCH_GRID = {
    'ceiling_height_m': [2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 2.9, 3.0],
    'wwr_ratio':        [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0],
    'cct_k':            [2700, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500],
    'floor_material':   ['marble', 'tile', 'wooden'],
    'room_color':       ['beige', 'gray', 'white'],
}
# Total: 8 × 10 × 9 × 3 × 3 = 6,480 combos


# ======================================================================
# Value mapping
# ======================================================================
FLOOR_MAP = {'wood': 'wooden', 'wooden': 'wooden', 'tile': 'tile', 'marble': 'marble'}
COLOR_MAP = {'beige': 'beige', 'white': 'white', 'gray': 'gray', 'grey': 'gray'}


# ======================================================================
# Data loading
# ======================================================================
def load_current_analysis(file_path):
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
        data = {}
        for line in content.strip().split('\n'):
            if ':' in line:
                key, value = line.split(':', 1)
                data[key.strip()] = value.strip()
        design = {
            'ceiling_height_m': float(data.get('Ceiling Height', '2.7')),
            'wwr_ratio': (lambda v: v / 100.0 if v >= 1.0 else v)(float(data.get('WWR', '0.8'))),
            'cct_k': float(data.get('CCT', '4000')),
            'floor_material': data.get('Floor Material', 'Wood'),
            'room_color': data.get('Room Color', 'Beige')
        }
        return design
    except Exception as e:
        print(f"[X] File read error: {e}")
        return None


def load_target_emotion(file_path):
    try:
        with open(file_path, 'r', encoding='utf-8-sig') as f:
            content = f.read().replace('\r', '').replace('\n', ' ').strip()
        if "Target_Emotion:" in content:
            emotion = content.split("Target_Emotion:")[1].strip().split()[0]
        else:
            emotion = content.split()[0]
        return emotion.lower().strip()
    except Exception as e:
        print(f"[X] Target emotion read error: {e}")
        return None


def save_revised_analysis(design, output_path):
    try:
        with open(output_path, 'w', encoding='utf-8') as f:
            f.write(f"Ceiling Height: {design['ceiling_height_m']:.2f}\n")
            f.write(f"WWR: {int(design['wwr_ratio'] * 100)}\n")
            f.write(f"CCT: {int(design['cct_k'])}\n")
            f.write(f"Floor Material: {design['floor_material']}\n")
            f.write(f"Room Color: {design['room_color']}\n")
        print(f"[OK] Saved revised_analysis.txt: {output_path}")
    except Exception as e:
        print(f"[X] Save error: {e}")


def save_after_variables(design, json_path=r'C:\Temp\after_variables.json'):
    try:
        data = {
            'ceiling_height_mm': float(design['ceiling_height_m']) * 1000,
            'wwr_ratio': float(design['wwr_ratio']),
            'cct_k': float(design['cct_k']),
            'room_color': design['room_color'],
            'floor_material': design['floor_material']
        }
        with open(json_path, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        print(f'[OK] Saved after_variables.json: {json_path}')
    except Exception as e:
        print(f'[ERROR] Failed to save after_variables.json: {e}')


def save_after_emotion_json(scores, labels, json_path=r'C:\Temp\emotion_scores_after.json'):
    try:
        data = {
            "labels": list(labels),
            "scores": [round(float(s), 4) for s in scores]
        }
        os.makedirs(os.path.dirname(json_path), exist_ok=True)
        with open(json_path, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        print(f'[OK] Saved emotion_scores_after.json: {json_path}')
    except Exception as e:
        print(f'[ERROR] Failed to save emotion_scores_after.json: {e}')


# ======================================================================
# Optimization engine
# ======================================================================
class DesignOptimizer:
    def __init__(self):
        print(f"[*] Loading preprocessor: {Config.PREPROCESSOR_PATH}")
        with open(Config.PREPROCESSOR_PATH, 'rb') as f:
            self.preprocessor = pickle.load(f)

        self.affect_labels = self.preprocessor['affect_labels']
        self.continuous_vars = self.preprocessor['continuous_vars']
        self.categorical_vars = self.preprocessor['categorical_vars']
        self.scaler = self.preprocessor['scaler']
        self.ohe = self.preprocessor.get('ohe', None)
        self.label_encoders = self.preprocessor.get('label_encoders', {})

        print(f"[*] Loading model: {Config.MODEL_PATH}")
        self.model = joblib.load(Config.MODEL_PATH)
        model_type = type(self.model).__name__
        print(f"[OK] Initialized ({model_type})")

        n_combos = 1
        for v in SEARCH_GRID.values():
            n_combos *= len(v)
        print(f"  - Emotions: {len(self.affect_labels)}")
        print(f"  - Search grid: {n_combos:,} combos")

    def encode_design(self, design: Dict) -> np.ndarray:
        """Encode a design as a model input vector."""
        # Continuous variables
        cont = np.array(
            [[float(design[v]) for v in self.continuous_vars]], dtype=np.float32)
        cont_scaled = self.scaler.transform(cont)[0]

        # Categorical mapping
        mapped = {}
        for c in self.categorical_vars:
            val = str(design[c]).lower().strip()
            if c == 'floor_material': val = FLOOR_MAP.get(val, val)
            elif c == 'room_color': val = COLOR_MAP.get(val, val)
            mapped[c] = val

        # One-hot encoder if the preprocessor has one, otherwise label encoders
        if self.ohe is not None:
            cat_arr = np.array([[mapped[c] for c in self.categorical_vars]])
            cat_enc = self.ohe.transform(cat_arr)[0].astype(np.float32)
        else:
            cat_enc = []
            for c in self.categorical_vars:
                le = self.label_encoders[c]
                val = mapped[c]
                cat_enc.append(float(le.transform([val])[0]) if val in le.classes_
                               else float(le.transform([le.classes_[0]])[0]))
            cat_enc = np.array(cat_enc, dtype=np.float32)

        return np.concatenate([cont_scaled, cat_enc])

    def predict(self, design: Dict) -> np.ndarray:
        vec = self.encode_design(design).reshape(1, -1)
        return self.model.predict(vec).flatten()

    def predict_batch(self, designs: List[Dict]) -> np.ndarray:
        X = np.array([self.encode_design(d) for d in designs], dtype=np.float32)
        return self.model.predict(X)

    def generate_all_combinations(self) -> List[Dict]:
        var_names = ['ceiling_height_m', 'cct_k', 'floor_material', 'room_color', 'wwr_ratio']
        value_lists = [SEARCH_GRID[name] for name in var_names]
        return [dict(zip(var_names, combo)) for combo in product(*value_lists)]

    def optimize(self, initial_design: Dict, target_emotion: str,
                 delta: float = 0.2) -> Optional[Dict]:
        if target_emotion not in self.affect_labels:
            raise ValueError(f"Invalid emotion: {target_emotion}")

        print(f"\n[*] '{target_emotion}' optimization started...")
        Q0 = self.predict(initial_design)
        target_idx = self.affect_labels.index(target_emotion)
        initial_score = float(Q0[target_idx])
        print(f"  Initial {target_emotion}: {initial_score:.4f}")

        # Batch prediction for all combinations
        all_designs = self.generate_all_combinations()
        print(f"  Combinations: {len(all_designs):,}")
        all_preds = self.predict_batch(all_designs)

        # Filtering
        other_indices = [i for i in range(len(self.affect_labels)) if i != target_idx]
        other_mean_initial = float(np.mean([Q0[i] for i in other_indices]))

        valid = []
        for idx, (design, Q) in enumerate(zip(all_designs, all_preds)):
            target_score = float(Q[target_idx])
            improvement = target_score - initial_score
            if improvement > 0:
                other_mean = float(np.mean([Q[i] for i in other_indices]))
                composite = target_score * 2.0 + other_mean
                valid.append({
                    'design': design, 'score': target_score,
                    'improvement': improvement, 'composite': composite,
                    'other_delta': other_mean - other_mean_initial
                })

        print(f"  Valid: {len(valid):,} / excluded: {len(all_designs) - len(valid):,}")

        if not valid:
            print(f"[X] No design improves {target_emotion}!")
            return None

        valid.sort(key=lambda x: x['composite'], reverse=True)
        best = valid[0]

        print(f"\n[OK] Optimization complete!")
        print(f"  {target_emotion}: {initial_score:.4f} → {best['score']:.4f} (+{best['improvement']:.4f})")
        print(f"  Change in other emotions: {best['other_delta']:+.4f}")

        for i, res in enumerate(valid[:5]):
            d = res['design']
            print(f"  [{i+1}] {target_emotion}={res['score']:.4f} (+{res['improvement']:.4f}) "
                  f"CH={d['ceiling_height_m']} WWR={d['wwr_ratio']} CCT={d['cct_k']} "
                  f"F={d['floor_material']} C={d['room_color']}")

        return best['design']


# ======================================================================
# Main
# ======================================================================
def main():
    try:
        print("=" * 70)
        print("ALIS Design Optimization")
        print("=" * 70)

        missing = []
        for p in [Config.MODEL_PATH, Config.PREPROCESSOR_PATH,
                  Config.CURRENT_ANALYSIS, Config.TARGET_EMOTION_FILE]:
            if not os.path.exists(p):
                missing.append(p)
        if missing:
            print("[X] Missing files:")
            for f in missing: print(f"  - {f}")
            sys.exit(1)

        initial_design = load_current_analysis(Config.CURRENT_ANALYSIS)
        if initial_design is None: sys.exit(1)
        print(f"[OK] Current design:")
        for k, v in initial_design.items(): print(f"  {k}: {v}")

        target_emotion = load_target_emotion(Config.TARGET_EMOTION_FILE)
        if target_emotion is None: sys.exit(1)
        print(f"[OK] Target emotion: {target_emotion}")

        optimizer = DesignOptimizer()
        optimal_design = optimizer.optimize(initial_design, target_emotion, Config.DELTA)
        if optimal_design is None: sys.exit(1)

        print(f"\n[*] Optimal design:")
        for k, v in optimal_design.items(): print(f"  {k}: {v}")

        print(f"\n[*] Changed variables:")
        for k in initial_design:
            if str(initial_design[k]) != str(optimal_design.get(k)):
                print(f"  {k}: {initial_design[k]} → {optimal_design.get(k)}")

        save_revised_analysis(optimal_design, Config.OUTPUT_FILE)
        save_after_variables(optimal_design)

        after_scores = optimizer.predict(optimal_design)
        save_after_emotion_json(after_scores, optimizer.affect_labels)

        print("\n" + "=" * 70)
        print("[OK] Design optimization complete!")
        print("=" * 70)

    except Exception as e:
        print(f"\n[X] Error: {e}")
        import traceback; traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
