# -*- coding: utf-8 -*-
"""
Evaluation of the text classifier used by NLP.py: 5-fold stratified cross-validation (seed 42) of
klue/bert-base with full fine-tuning (batch 16, lr 3e-5, dropout 0.1, Adam, 10 epochs, 10% warm-up).
Reports accuracy, weighted precision, recall and F1 per fold, and a per-class report from the
pooled out-of-fold predictions.
Environment: ALIS_TEXT_DATA (Excel file with one text column and one label column),
             ALIS_TEXT_COLUMN, ALIS_LABEL_COLUMN (column names). Output: classifier_cv_results.json
"""
import os, re, json, sys, io, time, copy
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", line_buffering=True)
import numpy as np
import pandas as pd
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader
from sklearn.model_selection import StratifiedKFold
from sklearn.metrics import classification_report, precision_recall_fscore_support, accuracy_score
from transformers import AutoTokenizer, AutoModel

SEED = 42
DATA = os.environ.get("ALIS_TEXT_DATA", "feedback.xlsx")
TEXT_COLUMN = os.environ.get("ALIS_TEXT_COLUMN", "feedback_text")   # must match your data file
LABEL_COLUMN = os.environ.get("ALIS_LABEL_COLUMN", "label")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "classifier_cv_results.json")
DEV = torch.device("cuda" if torch.cuda.is_available() else ("mps" if torch.backends.mps.is_available() else "cpu"))
MAX_LEN = 128

def set_seed(s):
    np.random.seed(s); torch.manual_seed(s); torch.cuda.manual_seed_all(s)

_re_url = re.compile(r'https?://\S+|www\.\S+')
_re_emoji = re.compile("["u"\U0001F600-\U0001F64F"u"\U0001F300-\U0001F5FF"u"\U0001F680-\U0001F6FF"u"\U0001F1E0-\U0001F1FF""]+", flags=re.UNICODE)
_re_repeat = re.compile(r'(.)\1{2,}')
def preprocess_text(t):
    t = str(t)
    t = _re_url.sub(' ', t); t = _re_emoji.sub(' ', t); t = _re_repeat.sub(r'\1\1', t)
    t = re.sub(r'[^0-9A-Za-z\uAC00-\uD7A3\u3131-\u314E\u314F-\u3163\s.,!?~\-]+', ' ', t)
    return re.sub(r'\s+', ' ', t).strip()

df = pd.read_excel(DATA)
texts = [preprocess_text(x) for x in df[TEXT_COLUMN].values]
label_map = {l: i for i, l in enumerate(sorted(df[LABEL_COLUMN].unique()))}
label_names = sorted(label_map.keys())
labels = df[LABEL_COLUMN].map(label_map).values
print(f"N={len(texts)}, classes={label_names}", flush=True)

class DS(Dataset):
    def __init__(self, texts, labels, tok):
        self.enc = tok(list(texts), truncation=True, padding='max_length', max_length=MAX_LEN, return_tensors='pt')
        self.labels = torch.tensor(labels, dtype=torch.long)
    def __len__(self): return len(self.labels)
    def __getitem__(self, i):
        return {'input_ids': self.enc['input_ids'][i], 'attention_mask': self.enc['attention_mask'][i], 'labels': self.labels[i]}

class Clf(nn.Module):
    def __init__(self, name, n_classes, dropout, freeze=False):
        super().__init__()
        self.encoder = AutoModel.from_pretrained(name)
        if freeze:
            for p in self.encoder.parameters(): p.requires_grad = False
        self.dropout = nn.Dropout(dropout)
        self.head = nn.Linear(self.encoder.config.hidden_size, n_classes)
    def forward(self, input_ids, attention_mask):
        out = self.encoder(input_ids=input_ids, attention_mask=attention_mask)
        cls = out.last_hidden_state[:, 0]   # [CLS] token
        return self.head(self.dropout(cls))

def run_fold(model_name, tr_idx, va_idx, cfg, fold_seed):
    set_seed(fold_seed)
    tok = AutoTokenizer.from_pretrained(model_name)
    tr = DataLoader(DS([texts[i] for i in tr_idx], labels[tr_idx], tok), batch_size=cfg['bs'], shuffle=True)
    va = DataLoader(DS([texts[i] for i in va_idx], labels[va_idx], tok), batch_size=64)
    model = Clf(model_name, len(label_names), cfg['dropout'], cfg['freeze']).to(DEV)
    params = [p for p in model.parameters() if p.requires_grad]
    opt = torch.optim.Adam(params, lr=cfg['lr'])
    total = len(tr) * cfg['epochs']; warm = int(0.1 * total)
    sched = torch.optim.lr_scheduler.LambdaLR(opt, lambda s: s/max(1,warm) if s < warm else max(0.0, (total-s)/max(1,total-warm)))
    crit = nn.CrossEntropyLoss()
    best_acc, best_state = -1, None
    for ep in range(cfg['epochs']):
        model.train()
        for b in tr:
            opt.zero_grad()
            out = model(b['input_ids'].to(DEV), b['attention_mask'].to(DEV))
            loss = crit(out, b['labels'].to(DEV))
            loss.backward(); nn.utils.clip_grad_norm_(params, 1.0); opt.step(); sched.step()
        model.eval(); preds = []
        with torch.no_grad():
            for b in va:
                out = model(b['input_ids'].to(DEV), b['attention_mask'].to(DEV))
                preds.append(out.argmax(1).cpu().numpy())
        preds = np.concatenate(preds)
        acc = accuracy_score(labels[va_idx], preds)
        if acc > best_acc:
            best_acc, best_preds = acc, preds
    return best_acc, best_preds

skf = StratifiedKFold(n_splits=5, shuffle=True, random_state=SEED)
folds = list(skf.split(np.zeros(len(labels)), labels))

TUNED = dict(bs=16, lr=3e-5, dropout=0.1, epochs=10, freeze=False)

MODELS = [
    ('klue_bert_finetuned', 'klue/bert-base', TUNED),
]

results = {}
for key, name, cfg in MODELS:
    t0 = time.time()
    fold_rows, oof_pred = [], np.zeros(len(labels), dtype=int)
    for fi, (tr_idx, va_idx) in enumerate(folds):
        acc, preds = run_fold(name, tr_idx, va_idx, cfg, SEED + fi)
        oof_pred[va_idx] = preds
        p, r, f1, _ = precision_recall_fscore_support(labels[va_idx], preds, average='weighted', zero_division=0)
        fold_rows.append(dict(fold=fi+1, accuracy=acc, precision=p, recall=r, f1=f1))
        print(f"[{key}] fold{fi+1}: acc={acc:.4f} P={p:.4f} R={r:.4f} F1={f1:.4f} ({time.time()-t0:.0f}s)", flush=True)
    accs = [x['accuracy'] for x in fold_rows]
    ps = [x['precision'] for x in fold_rows]; rs = [x['recall'] for x in fold_rows]; f1s = [x['f1'] for x in fold_rows]
    pooled_acc = accuracy_score(labels, oof_pred)
    rep = classification_report(labels, oof_pred, target_names=label_names, output_dict=True, zero_division=0)
    results[key] = dict(
        model=name, config={k: v for k, v in cfg.items()},
        folds=fold_rows,
        mean=dict(accuracy=float(np.mean(accs)), acc_sd=float(np.std(accs, ddof=1)),
                  precision=float(np.mean(ps)), prec_sd=float(np.std(ps, ddof=1)),
                  recall=float(np.mean(rs)), rec_sd=float(np.std(rs, ddof=1)),
                  f1=float(np.mean(f1s)), f1_sd=float(np.std(f1s, ddof=1))),
        pooled=dict(accuracy=float(pooled_acc), report=rep),
    )
    print(f"[{key}] MEAN acc={np.mean(accs):.4f}±{np.std(accs,ddof=1):.3f} F1={np.mean(f1s):.4f}±{np.std(f1s,ddof=1):.3f} pooled_acc={pooled_acc:.4f}", flush=True)
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(results, f, ensure_ascii=False, indent=1)

print("ALL DONE ->", OUT, flush=True)
