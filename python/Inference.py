#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
ALIS affect inference script (Transformer-based).
Reads BIM space data, computes affect scores and saves them as JSON.

Usage:
  python Inference.py              # before mode (default)
  python Inference.py --mode after  # after mode
"""

import sys
import os
import pickle
import json
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
# Transformer class definitions (needed when loading with joblib)
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
    print("[WARN] PyTorch not installed. Transformer model will fail to load.")


# ======================================================================
# Settings
# ======================================================================
class Config:
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    MODEL_PATH = os.path.join(SCRIPT_DIR, 'inference_transformer.pkl')
    PREPROCESSOR_PATH = os.path.join(SCRIPT_DIR, 'preprocessor.pkl')
    INPUT_FILE = r'C:\Temp\current_analysis.txt'
    OUTPUT_JSON = r'C:\Temp\emotion_scores.json'
    OUTPUT_JSON_AFTER = r'C:\Temp\emotion_scores_after.json'


# ======================================================================
# Value mapping
# ======================================================================
FLOOR_MAP = {'wood': 'wooden', 'wooden': 'wooden', 'tile': 'tile', 'marble': 'marble'}
COLOR_MAP = {'beige': 'beige', 'white': 'white', 'gray': 'gray', 'grey': 'gray'}


# ======================================================================
# Preprocessing
# ======================================================================
def parse_current_analysis(file_path):
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
        data = {}
        for line in content.strip().split('\n'):
            if ':' in line:
                key, value = line.split(':', 1)
                data[key.strip()] = value.strip()
        return {
            'ceiling_height_m': float(data.get('Ceiling Height', '2.7')),
            'wwr_ratio': (lambda v: v / 100.0 if v >= 1.0 else v)(float(data.get('WWR', '0.8'))),
            'cct_k': float(data.get('CCT', '4000')),
            'floor_material': data.get('Floor Material', 'Wood'),
            'room_color': data.get('Room Color', 'Beige')
        }
    except Exception as e:
        print(f"[!] File read error: {e}")
        return {'ceiling_height_m': 2.7, 'wwr_ratio': 0.8, 'cct_k': 4000,
                'floor_material': 'Wood', 'room_color': 'Beige'}


def preprocess_input(spatial_data, preprocessor):
    """Preprocess input; detects OHE or LabelEncoder automatically."""
    continuous_vars = preprocessor['continuous_vars']
    categorical_vars = preprocessor['categorical_vars']
    scaler = preprocessor['scaler']

    # Continuous variables
    cont = np.array([[float(spatial_data[v]) for v in continuous_vars]], dtype=np.float32)
    cont_scaled = scaler.transform(cont)[0]

    # Categorical mapping
    mapped = {}
    for c in categorical_vars:
        val = str(spatial_data[c]).lower().strip()
        if c == 'floor_material': val = FLOOR_MAP.get(val, val)
        elif c == 'room_color': val = COLOR_MAP.get(val, val)
        mapped[c] = val

    # One-hot encoder if the preprocessor has one, otherwise label encoders
    ohe = preprocessor.get('ohe', None)
    if ohe is not None:
        cat_arr = np.array([[mapped[c] for c in categorical_vars]])
        cat_enc = ohe.transform(cat_arr)[0].astype(np.float32)
    else:
        le_dict = preprocessor['label_encoders']
        cat_enc = []
        for c in categorical_vars:
            le = le_dict[c]
            val = mapped[c]
            cat_enc.append(float(le.transform([val])[0]) if val in le.classes_
                           else float(le.transform([le.classes_[0]])[0]))
        cat_enc = np.array(cat_enc, dtype=np.float32)

    return np.concatenate([cont_scaled, cat_enc])


def save_emotion_scores_json(scores, labels, path):
    raw = [round(float(x), 4) for x in scores]
    data = {"labels": list(labels), "scores": raw}
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"[OK] JSON saved: {path} ({min(raw):.4f}~{max(raw):.4f})")


# ======================================================================
# Main
# ======================================================================
def run_inference():
    try:
        print("=" * 70)
        print("ALIS Affective Analysis")
        print("=" * 70)

        missing = [p for p in [Config.MODEL_PATH, Config.PREPROCESSOR_PATH, Config.INPUT_FILE]
                   if not os.path.exists(p)]
        if missing:
            for m in missing: print(f"  [X] {m}")
            sys.exit(1)

        with open(Config.PREPROCESSOR_PATH, 'rb') as f:
            preprocessor = pickle.load(f)
        labels = preprocessor['affect_labels']
        model = joblib.load(Config.MODEL_PATH)
        print(f"[OK] Model: {type(model).__name__}, Features: {len(preprocessor['feature_names'])}")

        spatial_data = parse_current_analysis(Config.INPUT_FILE)
        for k, v in spatial_data.items(): print(f"  {k}: {v}")

        vec = preprocess_input(spatial_data, preprocessor).reshape(1, -1)
        scores = model.predict(vec).flatten()

        print("\n[Chart] Affect analysis results:")
        for l, s in zip(labels, scores): print(f"  {l:15s}: {s:.4f}")

        save_emotion_scores_json(scores, labels, Config.OUTPUT_JSON)
        print(f"\n[OK] Done! -> {Config.OUTPUT_JSON}")
        return scores, labels

    except Exception as e:
        print(f"\n[X] {e}")
        import traceback; traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["before", "after"], default="before")
    args = parser.parse_args()

    if args.mode == "after":
        af_path = r"C:\Temp\after_variables.json"
        if os.path.exists(af_path):
            with open(af_path, 'r', encoding='utf-8') as _f:
                _av = json.load(_f)
            _tmp = r"C:\Temp\_after_analysis_tmp.txt"
            with open(_tmp, 'w', encoding='utf-8') as _f:
                _f.write(f"Ceiling Height: {_av.get('ceiling_height_mm', 2700) / 1000:.2f}\n")
                _f.write(f"WWR: {_av.get('wwr_ratio', 0.4) * 100:.1f}\n")
                _f.write(f"CCT: {_av.get('cct_k', 4000)}\n")
                _f.write(f"Room Color: {_av.get('room_color', 'White')}\n")
                _f.write(f"Floor Material: {_av.get('floor_material', 'Wood')}\n")
            Config.INPUT_FILE = _tmp
            Config.OUTPUT_JSON = Config.OUTPUT_JSON_AFTER

    run_inference()
