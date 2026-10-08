# -*- coding: utf-8 -*-
"""
Shared loader for the design-selection analysis.
Loads the trained affect model and preprocessor, builds the grid of 6,480 candidate designs
(ceiling height x colour temperature x floor material x wall colour x window-to-wall ratio),
predicts the 10-dimension affective profile of every design, and builds the cost matrix
C_ij = 1 - r_ij from the correlations between the predicted dimensions.
Environment: ALIS_MODEL_DIR (folder with preprocessor.pkl and the model file),
             ALIS_MODEL_FILE (model file name, default inference_transformer.pkl).
"""
import sys, io, os, pickle, json
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
from itertools import product
import numpy as np, joblib, torch, torch.nn as nn

class TabularTransformer(nn.Module):
    def __init__(self, d_input, d_model=64, n_heads=4, n_layers=2, d_ff=128, d_output=10, dropout=0.1):
        super().__init__()
        self.input_proj = nn.Linear(d_input, d_model)
        self.pos_embed = nn.Parameter(torch.randn(1, 1, d_model) * 0.02)
        enc = nn.TransformerEncoderLayer(d_model=d_model, nhead=n_heads, dim_feedforward=d_ff, dropout=dropout, batch_first=True)
        self.transformer = nn.TransformerEncoder(enc, num_layers=n_layers)
        self.output_head = nn.Sequential(nn.LayerNorm(d_model), nn.Linear(d_model, d_ff), nn.GELU(), nn.Dropout(dropout), nn.Linear(d_ff, d_output))
    def forward(self, x):
        h = self.input_proj(x).unsqueeze(1) + self.pos_embed
        return self.output_head(self.transformer(h).squeeze(1))

class TransformerRegressor:
    def __init__(self, **kw):
        self.model = None
    def predict(self, X):
        self.model.eval()
        with torch.no_grad(): return self.model(torch.FloatTensor(np.asarray(X))).numpy()
    def get_params(self, deep=True): return {}
    def set_params(self, **p): return self

PY = os.environ.get("ALIS_MODEL_DIR", os.path.join(os.path.dirname(os.path.abspath(__file__)), "models"))
with open(os.path.join(PY, "preprocessor.pkl"), 'rb') as f: prep = pickle.load(f)
labels = prep['affect_labels']; cont_vars = prep['continuous_vars']; cat_vars = prep['categorical_vars']
scaler = prep['scaler']; ohe = prep.get('ohe'); le = prep.get('label_encoders', {})
model = joblib.load(os.path.join(PY, os.environ.get("ALIS_MODEL_FILE", "inference_transformer.pkl")))

SEARCH_GRID = {
    'ceiling_height_m': [2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 2.9, 3.0],
    'wwr_ratio': [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0],
    'cct_k': [2700, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500],
    'floor_material': ['marble', 'tile', 'wooden'],
    'room_color': ['beige', 'gray', 'white'],
}
CONFIGS = {
    'A': {'ceiling_height_m':2.3,'cct_k':2700,'floor_material':'tile','room_color':'gray','wwr_ratio':0.3},
    'B': {'ceiling_height_m':2.7,'cct_k':6500,'floor_material':'wooden','room_color':'beige','wwr_ratio':0.4},
    'C': {'ceiling_height_m':3.0,'cct_k':3500,'floor_material':'marble','room_color':'white','wwr_ratio':0.8},
}
DELTA_RAW, ALPHA, EPS, ITERS = 1.0, 2.0, 0.05, 50

def encode(d):
    cont = scaler.transform(np.array([[float(d[v]) for v in cont_vars]], dtype=np.float32))[0]
    if ohe is not None:
        cat = ohe.transform(np.array([[str(d[c]).lower() for c in cat_vars]]))[0].astype(np.float32)
    else:
        cat = np.array([float(le[c].transform([str(d[c]).lower()])[0]) for c in cat_vars], dtype=np.float32)
    return np.concatenate([cont, cat])

def norm_dist(s):
    s = np.clip(np.asarray(s, dtype=np.float64), 0.01, None)
    return s / s.sum()

def sinkhorn_cost(P_star_n, Q_n_batch, M, epsilon=EPS, n_iters=ITERS):
    N, n = Q_n_batch.shape
    P = P_star_n + 1e-10; P = P / P.sum()
    Q = Q_n_batch + 1e-10; Q = Q / Q.sum(axis=1, keepdims=True)
    K = np.exp(-M / epsilon)
    u = np.ones((N, n)); v = np.ones((N, n))
    for _ in range(n_iters):
        u = P[np.newaxis, :] / (v @ K.T + 1e-10)
        v = Q / (u @ K + 1e-10)
    return np.einsum('ij,jk,ik->i', u, K * M, v)

var_names = ['ceiling_height_m','cct_k','floor_material','room_color','wwr_ratio']
all_designs = [dict(zip(var_names, c)) for c in product(*[SEARCH_GRID[n] for n in var_names])]
X_all = np.array([encode(d) for d in all_designs], dtype=np.float32)
Q_all = model.predict(X_all).astype(np.float64)          # raw scores (6480 x 10)
Q_all_n = np.array([norm_dist(q) for q in Q_all])        # normalized

r = np.corrcoef(Q_all.T)
C = 1.0 - r
np.fill_diagonal(C, 0.0)
print(f"grid={len(all_designs)}  corr r: min={r[~np.eye(10,dtype=bool)].min():.2f}  max={r[~np.eye(10,dtype=bool)].max():.2f}")

