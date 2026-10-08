#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Train TransformerRegressor: design matrix + affect ratings -> inference_transformer.pkl

- Uses the same model classes that inference (`Inference.py`) expects
  (TabularTransformer + TransformerRegressor wrapper).
- Boring (negative 1-7 scale) is reverse-coded as `(LIKERT_MAX+1) - x`, then renamed to 'excitement'.
- Design-matrix columns (`Ceiling, WWR, CCT, Color, Floor`) are mapped to the standard training headers.
- Outputs (joblib): `inference_transformer.pkl` (TransformerRegressor) + `preprocessor.pkl`.

Usage:
  python train_transformer.py --data ratings.csv --doe design_matrix.xlsx
"""

import os
import sys
import argparse
import pickle
import numpy as np
import pandas as pd
import torch
import torch.nn as nn
import joblib
from sklearn.preprocessing import StandardScaler, OneHotEncoder
from sklearn.model_selection import KFold
from sklearn.metrics import mean_absolute_error, mean_squared_error, r2_score


# =====================================================================
# Model classes (same signature as Inference.py)
# =====================================================================
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
        d_in = X.shape[1]
        d_out = y.shape[1] if y.ndim > 1 else 1
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


# =====================================================================
# Data settings (same as train_xgboost_baseline.py, plus reverse coding/mapping)
# Column names must match your own data files.
# =====================================================================
AFFECT_COLS_DATA = ['Pleasant', 'Boring', 'Cozy', 'Spacious', 'Attractive',
                    'Simple', 'Bright', 'Open', 'Calm', 'Comfortable']

AFFECT_LABELS = ['pleasant', 'excitement', 'cozy', 'spacious', 'attractive',
                 'simple', 'bright', 'open', 'calm', 'comfortable']

DESIGN_COLS = ['ceiling_height_m', 'wwr_ratio', 'cct_k', 'room_color', 'floor_material']
CONTINUOUS_VARS = ['ceiling_height_m', 'wwr_ratio', 'cct_k']
CATEGORICAL_VARS = ['floor_material', 'room_color']

FLOOR_MAP = {'wood': 'wooden', 'tile': 'tile', 'marble': 'marble'}
COLOR_MAP = {'beige': 'beige', 'white': 'white', 'gray': 'gray', 'grey': 'gray'}

DOE_HEADER_MAP = {
    'Ceiling': 'ceiling_height_m', 'WWR': 'wwr_ratio', 'CCT': 'cct_k',
    'Color': 'room_color', 'Floor': 'floor_material',
}

REVERSE_CODED_COLS = ['Boring']
LIKERT_MAX = 7


def load_and_prepare(data_path, doe_path):
    df_data = pd.read_csv(data_path)

    # Reverse-code Boring (before training and before renaming columns)
    for col in REVERSE_CODED_COLS:
        if col in df_data.columns:
            df_data[col] = (LIKERT_MAX + 1) - df_data[col]
            print(f"[FIX] '{col}' reverse-coded: 1~{LIKERT_MAX} -> {LIKERT_MAX}~1")

    affect_means = df_data.groupby('Target')[AFFECT_COLS_DATA].mean()
    affect_means.columns = AFFECT_LABELS
    affect_means = affect_means.reset_index()

    # Load design matrix (xlsx or csv)
    ext = os.path.splitext(doe_path)[1].lower()
    df_doe = pd.read_excel(doe_path) if ext in ('.xlsx', '.xls') else pd.read_csv(doe_path)
    rename_dict = {k: v for k, v in DOE_HEADER_MAP.items() if k in df_doe.columns}
    if rename_dict:
        df_doe = df_doe.rename(columns=rename_dict)
        print(f"[FIX] DoE column mapping: {rename_dict}")

    missing = [c for c in DESIGN_COLS if c not in df_doe.columns]
    if missing:
        raise KeyError(f"DoE missing columns {missing}. Found: {df_doe.columns.tolist()}")

    design_map = df_doe.groupby('Run')[DESIGN_COLS].first().reset_index()
    design_map = design_map.rename(columns={'Run': 'Target'})

    # Clean units
    design_map['ceiling_height_m'] = design_map['ceiling_height_m'].astype(str) \
        .str.replace('m', '', regex=False).str.strip().astype(float)
    design_map['cct_k'] = design_map['cct_k'].astype(str) \
        .str.replace('K', '', regex=False).str.strip().astype(float)
    design_map['wwr_ratio'] = design_map['wwr_ratio'].astype(float)
    design_map['floor_material'] = design_map['floor_material'].str.lower().str.strip() \
        .map(FLOOR_MAP).fillna(design_map['floor_material'].str.lower().str.strip())
    design_map['room_color'] = design_map['room_color'].str.lower().str.strip() \
        .map(COLOR_MAP).fillna(design_map['room_color'].str.lower().str.strip())

    df = pd.merge(design_map, affect_means, on='Target', how='inner')
    print(f"[OK] Joined: {len(df)} rows x {len(df.columns)} cols")

    # Use OneHotEncoder (matches the OHE branch in inference)
    scaler = StandardScaler().fit(df[CONTINUOUS_VARS].values)
    try:
        ohe = OneHotEncoder(sparse_output=False, handle_unknown='ignore')
    except TypeError:
        ohe = OneHotEncoder(sparse=False, handle_unknown='ignore')
    ohe.fit(df[CATEGORICAL_VARS].values)

    preprocessor = {
        'feature_names': DESIGN_COLS,
        'continuous_vars': CONTINUOUS_VARS,
        'categorical_vars': CATEGORICAL_VARS,
        'affect_labels': AFFECT_LABELS,
        'scaler': scaler,
        'ohe': ohe,
        'opt_ranges': {
            'ceiling_height_m': sorted(df['ceiling_height_m'].unique().tolist()),
            'wwr_ratio': sorted(df['wwr_ratio'].unique().tolist()),
            'cct_k': sorted(df['cct_k'].unique().tolist()),
            'floor_material': sorted(df['floor_material'].unique().tolist()),
            'room_color': sorted(df['room_color'].unique().tolist()),
        }
    }

    X_cont = scaler.transform(df[CONTINUOUS_VARS].values).astype(np.float32)
    X_cat = ohe.transform(df[CATEGORICAL_VARS].values).astype(np.float32)
    X = np.hstack([X_cont, X_cat]).astype(np.float32)
    Y = df[AFFECT_LABELS].values.astype(np.float32)

    print(f"[OK] X={X.shape}, Y={Y.shape}")
    return X, Y, preprocessor


def cross_validate(X, Y, n_splits=5, **mkwargs):
    """5-fold CV: train a fresh model per fold and report held-out performance."""
    kf = KFold(n_splits=n_splits, shuffle=True, random_state=42)
    Y_pred = np.zeros_like(Y)
    for i, (tr, va) in enumerate(kf.split(X)):
        m = TransformerRegressor(**mkwargs).fit(X[tr], Y[tr])
        Y_pred[va] = m.predict(X[va])
        print(f"  [Fold {i+1}/{n_splits}] tr={len(tr)}, va={len(va)}")

    print(f"\n{'Affect':<14s}  {'MAE':>7s}  {'RMSE':>7s}  {'R²':>7s}")
    print('-' * 42)
    for i, lab in enumerate(AFFECT_LABELS):
        mae = mean_absolute_error(Y[:, i], Y_pred[:, i])
        rmse = np.sqrt(mean_squared_error(Y[:, i], Y_pred[:, i]))
        r2 = r2_score(Y[:, i], Y_pred[:, i])
        print(f"{lab:<14s}  {mae:7.3f}  {rmse:7.3f}  {r2:7.3f}")
    print('-' * 42)
    print(f"{'Mean':<14s}  {mean_absolute_error(Y, Y_pred):7.3f}  "
          f"{np.sqrt(mean_squared_error(Y, Y_pred)):7.3f}  "
          f"{r2_score(Y, Y_pred):7.3f}\n")


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    p = argparse.ArgumentParser()
    p.add_argument("--data", default=os.path.join(here, "ratings.csv"))
    p.add_argument("--doe", default=os.path.join(here, "design_matrix.xlsx"))
    p.add_argument("--output", default=os.path.join(here, "inference_transformer.pkl"))
    p.add_argument("--preprocessor-out", default=os.path.join(here, "preprocessor.pkl"))
    p.add_argument("--epochs", type=int, default=500)
    p.add_argument("--no-cv", action="store_true")
    args = p.parse_args()

    print("=" * 70)
    print("TransformerRegressor training")
    print(f"  data: {args.data}")
    print(f"  doe : {args.doe}")
    print("=" * 70)

    X, Y, preprocessor = load_and_prepare(args.data, args.doe)

    if not args.no_cv:
        print("\n[Step 1] 5-fold CV")
        cross_validate(X, Y, epochs=args.epochs)

    print(f"\n[Step 2] Final training on all data ({X.shape[0]} rows, epochs={args.epochs})")
    model = TransformerRegressor(epochs=args.epochs).fit(X, Y)

    # Check train fit
    Yp = model.predict(X)
    print(f"[OK] Train MAE={mean_absolute_error(Y, Yp):.4f}, R²={r2_score(Y, Yp):.4f}")

    # Save (inference reads it with joblib.load)
    joblib.dump(model, args.output)
    with open(args.preprocessor_out, 'wb') as f:
        pickle.dump(preprocessor, f)

    print(f"\n[OK] Saved")
    print(f"  - {args.output} ({os.path.getsize(args.output)/1024:.1f} KB)")
    print(f"  - {args.preprocessor_out} ({os.path.getsize(args.preprocessor_out)/1024:.1f} KB)")


if __name__ == "__main__":
    main()
