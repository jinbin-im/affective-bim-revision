#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
[Baseline] XGBoost training script
ratings.csv + design_matrix.xlsx -> xgb_baseline.pkl  (separate from the main inference)

* The main inference (`Inference.py`, `Optimizer.py`) uses
  `inference_transformer.pkl` (PyTorch TransformerRegressor), trained with
  `train_transformer.py`. This script trains an XGBoost baseline for comparison.

Notes:
  - Output file is `xgb_baseline.pkl` (kept apart from the main model)
  - Boring is reverse-coded as 8 - x before training, so it aligns with 'excitement'
  - Design-matrix input can be .xlsx (Run/Ceiling/WWR/CCT/Color/Floor) or
    a preprocessed .csv (Run/ceiling_height_m/...); both are detected

Usage:
  python train_xgboost_baseline.py --data ratings.csv --doe design_matrix.xlsx
"""

import os
import sys
import argparse
import pickle
import numpy as np
import pandas as pd
from sklearn.preprocessing import StandardScaler, LabelEncoder
from sklearn.multioutput import MultiOutputRegressor
from sklearn.model_selection import cross_val_predict, KFold
from sklearn.metrics import mean_absolute_error, mean_squared_error, r2_score
import joblib

try:
    from xgboost import XGBRegressor
except ImportError:
    print("[ERROR] xgboost not installed. Run: pip install xgboost")
    sys.exit(1)


# ======================================================================
# Settings
# Column names must match your own data files.
# ======================================================================
AFFECT_COLS_DATA = ['Pleasant', 'Boring', 'Cozy', 'Spacious', 'Attractive',
                    'Simple', 'Bright', 'Open', 'Calm', 'Comfortable']

AFFECT_LABELS = ['pleasant', 'excitement', 'cozy', 'spacious', 'attractive',
                 'simple', 'bright', 'open', 'calm', 'comfortable']

DESIGN_COLS = ['ceiling_height_m', 'wwr_ratio', 'cct_k', 'room_color', 'floor_material']

CONTINUOUS_VARS = ['ceiling_height_m', 'wwr_ratio', 'cct_k']
CATEGORICAL_VARS = ['floor_material', 'room_color']

FLOOR_MAP = {'wood': 'wooden', 'tile': 'tile', 'marble': 'marble'}
COLOR_MAP = {'beige': 'beige', 'white': 'white', 'gray': 'gray', 'grey': 'gray'}

# Map raw design-matrix headers (.xlsx) to the standard training headers.
# The design matrix has 'Run, Ceiling, WWR, CCT, Color, Floor',
# so rename them to match the training variable names.
DOE_HEADER_MAP = {
    'Ceiling': 'ceiling_height_m',
    'WWR': 'wwr_ratio',
    'CCT': 'cct_k',
    'Color': 'room_color',
    'Floor': 'floor_material',
}

# Boring is a negative affect (higher = more boring), so reverse-code it
# as 8 - x on the 1-7 Likert scale to align with 'excitement' (positive direction).
REVERSE_CODED_COLS = ['Boring']
LIKERT_MAX = 7  # Assumes 1..7 Likert. Change only here if the scale changes.


# ======================================================================
# Data loading and preprocessing
# ======================================================================
def load_and_prepare(data_path, doe_path, preprocessor_path=None):
    """
    1. Extract the Target -> design variable mapping from the design matrix
    2. Compute mean affect ratings per Target from the ratings file
    3. Join into a dataset of (5 design + 10 affect) columns, one row per design
    """
    # --- Affect ratings (averaged per design) ---
    df_data = pd.read_csv(data_path)

    # [FIX] Reverse-code Boring: flip as 8 - x on the 1-7 Likert scale
    # so it matches the positive direction of 'excitement'.
    # Renaming the column alone would make the model learn 'Boring' scores as 'excitement'.
    for col in REVERSE_CODED_COLS:
        if col in df_data.columns:
            df_data[col] = (LIKERT_MAX + 1) - df_data[col]
            print(f"[FIX] '{col}' reverse-coded: 1~{LIKERT_MAX} -> {LIKERT_MAX}~1 "
                  f"(now positive direction)")

    affect_means = df_data.groupby('Target')[AFFECT_COLS_DATA].mean()
    # Rename columns (reverse coding is done, so only rename 'Boring' -> 'excitement')
    affect_means.columns = AFFECT_LABELS
    affect_means = affect_means.reset_index()

    print(f"[OK] Ratings loaded: {len(df_data)} rows -> {len(affect_means)} design means")

    # --- Design variables (from the design matrix) ---
    # [FIX] Handle .xlsx or .csv.
    ext = os.path.splitext(doe_path)[1].lower()
    if ext in ('.xlsx', '.xls'):
        df_doe = pd.read_excel(doe_path)
    else:
        df_doe = pd.read_csv(doe_path)

    # [FIX] Map raw design-matrix headers ('Run, Ceiling, WWR, CCT, Color, Floor')
    # to the standard training headers. No-op if headers are already standard.
    rename_dict = {k: v for k, v in DOE_HEADER_MAP.items() if k in df_doe.columns}
    if rename_dict:
        df_doe = df_doe.rename(columns=rename_dict)
        print(f"[FIX] DoE column mapping: {rename_dict}")

    missing = [c for c in DESIGN_COLS if c not in df_doe.columns]
    if missing:
        raise KeyError(
            f"DoE file is missing these columns: {missing}\n"
            f"Found columns: {df_doe.columns.tolist()}\n"
            f"Add a mapping to DOE_HEADER_MAP."
        )

    design_map = df_doe.groupby('Run')[DESIGN_COLS].first().reset_index()
    design_map = design_map.rename(columns={'Run': 'Target'})

    # Clean design variables
    design_map['ceiling_height_m'] = design_map['ceiling_height_m'].astype(str) \
        .str.replace('m', '', regex=False).str.strip().astype(float)
    design_map['cct_k'] = design_map['cct_k'].astype(str) \
        .str.replace('K', '', regex=False).str.strip().astype(float)
    design_map['wwr_ratio'] = design_map['wwr_ratio'].astype(float)
    design_map['floor_material'] = design_map['floor_material'].str.lower().str.strip() \
        .map(FLOOR_MAP).fillna(design_map['floor_material'].str.lower().str.strip())
    design_map['room_color'] = design_map['room_color'].str.lower().str.strip() \
        .map(COLOR_MAP).fillna(design_map['room_color'].str.lower().str.strip())

    print(f"[OK] Design variables loaded: {len(design_map)} designs")

    # --- Join ---
    df = pd.merge(design_map, affect_means, on='Target', how='inner')
    print(f"[OK] Joined: {len(df)} rows x {len(df.columns)} cols")

    # --- Load or create preprocessor ---
    if preprocessor_path and os.path.exists(preprocessor_path):
        print(f"[OK] Loaded existing preprocessor.pkl: {preprocessor_path}")
        with open(preprocessor_path, 'rb') as f:
            preprocessor = pickle.load(f)
        scaler = preprocessor['scaler']
        label_encoders = preprocessor['label_encoders']
    else:
        print("[INFO] preprocessor.pkl not found; creating a new one")
        scaler = StandardScaler()
        scaler.fit(df[CONTINUOUS_VARS].values)
        label_encoders = {}
        for c in CATEGORICAL_VARS:
            le = LabelEncoder()
            le.fit(df[c].values)
            label_encoders[c] = le
        # Save
        preprocessor = {
            'feature_names': DESIGN_COLS,
            'continuous_vars': CONTINUOUS_VARS,
            'categorical_vars': CATEGORICAL_VARS,
            'affect_labels': AFFECT_LABELS,
            'scaler': scaler,
            'label_encoders': label_encoders,
            'opt_ranges': {
                'ceiling_height_m': sorted(df['ceiling_height_m'].unique().tolist()),
                'wwr_ratio': sorted(df['wwr_ratio'].unique().tolist()),
                'cct_k': sorted(df['cct_k'].unique().tolist()),
                'floor_material': sorted(df['floor_material'].unique().tolist()),
                'room_color': sorted(df['room_color'].unique().tolist()),
            }
        }
        save_path = preprocessor_path or 'preprocessor.pkl'
        with open(save_path, 'wb') as f:
            pickle.dump(preprocessor, f)
        print(f"[OK] preprocessor.pkl saved: {save_path}")

    # --- Feature encoding ---
    X_cont = scaler.transform(df[CONTINUOUS_VARS].values)
    X_cat = np.column_stack([
        label_encoders[c].transform(df[c].values).astype(float)
        for c in CATEGORICAL_VARS
    ])
    X = np.hstack([X_cont, X_cat])
    Y = df[AFFECT_LABELS].values

    print(f"[OK] Feature matrix: X={X.shape}, Y={Y.shape}")
    print(f"  Continuous (scaled): {CONTINUOUS_VARS}")
    print(f"  Categorical (encoded): {CATEGORICAL_VARS}")
    print(f"  Scaler mean: {scaler.mean_.tolist()}")
    print(f"  Scaler scale: {scaler.scale_.tolist()}")
    for c in CATEGORICAL_VARS:
        print(f"  {c} classes: {label_encoders[c].classes_.tolist()}")

    return X, Y, df, preprocessor


# ======================================================================
# Training and validation
# ======================================================================
def train_and_score(X, Y):
    """
    Train an XGBoost MultiOutputRegressor.
    Report cross-validated performance, then fit the final model on all data.
    """
    print("\n" + "=" * 70)
    print("XGBoost training started")
    print("=" * 70)

    base_xgb = XGBRegressor(
        n_estimators=300,
        learning_rate=0.05,
        max_depth=4,
        subsample=0.8,
        colsample_bytree=0.8,
        random_state=42,
        verbosity=0
    )
    model = MultiOutputRegressor(base_xgb)

    # --- 5-fold CV (for reporting performance) ---
    print("\n[Step 1] 5-fold cross-validation...")
    kf = KFold(n_splits=5, shuffle=True, random_state=42)
    Y_pred_cv = cross_val_predict(model, X, Y, cv=kf)

    print(f"\n{'=' * 60}")
    print(f"  Per-affect CV performance (5-fold)")
    print(f"{'=' * 60}")
    print(f"  {'Affect':<14s}  {'MAE':>8s}  {'RMSE':>8s}  {'MAPE%':>8s}  {'R²':>8s}")
    print(f"  {'-'*14}  {'-'*8}  {'-'*8}  {'-'*8}  {'-'*8}")

    maes, r2s, mapes = [], [], []
    for i, label in enumerate(AFFECT_LABELS):
        y_true = Y[:, i]
        y_pred = Y_pred_cv[:, i]
        mae = mean_absolute_error(y_true, y_pred)
        rmse = np.sqrt(mean_squared_error(y_true, y_pred))
        r2 = r2_score(y_true, y_pred)
        mape = np.mean(np.abs((y_true - y_pred) / np.clip(y_true, 1e-8, None))) * 100
        maes.append(mae)
        r2s.append(r2)
        mapes.append(mape)
        print(f"  {label:<14s}  {mae:8.3f}  {rmse:8.3f}  {mape:8.2f}  {r2:8.3f}")

    print(f"  {'-'*14}  {'-'*8}  {'-'*8}  {'-'*8}  {'-'*8}")
    print(f"  {'Mean':<14s}  {np.mean(maes):8.3f}  {'':>8s}  {np.mean(mapes):8.2f}  {np.mean(r2s):8.3f}")

    # --- Final model on all data ---
    print(f"\n[Step 2] Training final model on all data ({X.shape[0]} rows)...")
    model.fit(X, Y)
    print("[OK] Training done")

    # --- Check fit on training data ---
    Y_train_pred = model.predict(X)
    train_mae = mean_absolute_error(Y, Y_train_pred)
    train_r2 = r2_score(Y, Y_train_pred)
    print(f"[OK] Training fit: MAE={train_mae:.4f}, R²={train_r2:.4f}")

    return model


# ======================================================================
# Main
# ======================================================================
def main():
    parser = argparse.ArgumentParser(description="Train XGBoost affect prediction model")
    parser.add_argument("--data", default="ratings.csv",
                        help="Affect ratings file (Target + 10 affects, including 'Boring')")
    parser.add_argument("--doe", default="design_matrix.xlsx",
                        help="Design matrix with design variables (.xlsx or .csv)")
    parser.add_argument("--preprocessor", default="preprocessor.pkl",
                        help="Preprocessor path (created if missing)")
    parser.add_argument("--output", default="xgb_baseline.pkl",
                        help="Output model path (baseline, separate from main inference)")
    args = parser.parse_args()

    # Load data
    X, Y, df, preprocessor = load_and_prepare(args.data, args.doe, args.preprocessor)

    # Train
    model = train_and_score(X, Y)

    # Save
    joblib.dump(model, args.output)
    print(f"\n[OK] Model saved: {args.output}")
    print(f"     File size: {os.path.getsize(args.output) / 1024:.1f} KB")

    print("\n" + "=" * 70)
    print("XGBoost training done!")
    print(f"  Model: {args.output}")
    print(f"  Preprocessor: {args.preprocessor}")
    print(f"  Data: {X.shape[0]} designs x {X.shape[1]} features -> {Y.shape[1]} affects")
    print("=" * 70)


if __name__ == "__main__":
    main()
