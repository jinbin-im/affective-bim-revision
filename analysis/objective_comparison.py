# -*- coding: utf-8 -*-
"""
Comparison of design-selection rules (why the current version replaced optimal transport).

For three starting rooms (A, B, C) and each of the 10 target feelings (30 scenarios), every rule
picks one of the 6,480 candidate designs. All rules keep only candidates that raise the target feeling.
  optimal_transport   earlier version: smallest Sinkhorn distance to a target profile (initial + 1.0 on the target)
  target_max          S = Q_t
  penalized(lam)      S = Q_t - lam * mean_j |Q_j - Q0_j|      (lam = 0.5, 1, 2, 4)
  composite           S = 2 * Q_t + mean_j Q_j                 (current version, Optimizer.optimize)
Reported per rule: mean change of the target, alignment with the requested direction, mean change
of the other nine feelings, share of scenarios that moved the target up, and number of distinct designs.
Environment: see grid_loader.py. Output: objective_comparison_results.json
"""
import os, json, collections, sys
import numpy as np
sys.argv = [sys.argv[0]]
exec(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'grid_loader.py'), encoding='utf-8').read())
labels = list(labels)

def rules():
    d = {}
    def ot(Qi, t):
        P = Qi.copy(); P[t] += DELTA_RAW
        return -sinkhorn_cost(norm_dist(P), Q_all_n, C)
    d['optimal_transport'] = ot
    d['target_max'] = lambda Qi, t: Q_all[:, t]
    for lam in (0.5, 1.0, 2.0, 4.0):
        d[f'penalized_{lam}'] = lambda Qi, t, lam=lam: Q_all[:, t] - lam * np.mean(np.abs(np.delete(Q_all - Qi, t, axis=1)), axis=1)
    d['composite'] = lambda Qi, t: 2 * Q_all[:, t] + np.delete(Q_all, t, axis=1).mean(1)
    return d

Qinit = {c: model.predict(encode(cfg).reshape(1, -1)).flatten().astype(np.float64) for c, cfg in CONFIGS.items()}
res = {'scenarios': 30, 'rules': {}}
for name, fn in rules().items():
    dt, al, nt, up, picks = [], [], [], [], set()
    for c, Qi in Qinit.items():
        for t in range(10):
            mask = Q_all[:, t] > Qi[t]
            sel = int(np.argmax(np.where(mask, fn(Qi, t), -np.inf)))
            dQ = Q_all[sel] - Qi; e = np.zeros(10); e[t] = 1
            dt.append(dQ[t]); al.append(dQ @ e / (np.linalg.norm(dQ) + 1e-12))
            nt.append(np.mean(np.abs(np.delete(dQ, t)))); up.append(dQ[t] > 0); picks.add(sel)
    res['rules'][name] = {'d_target': float(np.mean(dt)), 'alignment': float(np.mean(al)),
                          'other_change': float(np.mean(nt)), 'target_up_pct': float(np.mean(up) * 100),
                          'distinct_designs': len(picks)}
json.dump(res, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'objective_comparison_results.json'), 'w'), indent=1)
print(f"{'rule':18s} {'d_target':>9s} {'align':>6s} {'other':>6s} {'up%':>6s} {'#designs':>8s}")
for n, v in res['rules'].items():
    print(f"{n:18s} {v['d_target']:+9.3f} {v['alignment']:6.3f} {v['other_change']:6.3f} {v['target_up_pct']:6.1f} {v['distinct_designs']:8d}")
