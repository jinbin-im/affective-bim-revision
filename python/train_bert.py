#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Fine-tune KLUE-BERT for emotion classification -> NLP_Klue_BERT.pt

- Input: an .xlsx file with a free-text feedback column and an emotion label column.
- Labels: 10 affects (`pleasant, spacious, bright, ...`).
  Rows with other labels (e.g. 'others') are dropped, because `LABEL_NAMES`
  in inference (`NLP.py`) is fixed at 10 classes.
- Model: `klue/bert-base` + Linear classifier (same structure as
  `EmotionBERTClassifier` in NLP.py: pooler_output -> Dropout -> Linear).
- Outputs:
    - `NLP_Klue_BERT.pt`  (state_dict at best val acc)
    - `NLP_tokenizer/`     (saved BertTokenizer)

Usage:
  python train_bert.py --data feedback.xlsx
  python train_bert.py --data feedback.xlsx --epochs 5 --batch 16
"""

import os
import sys
import argparse
import re
import random
import numpy as np
import pandas as pd
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader
from transformers import BertTokenizer, BertModel
from sklearn.model_selection import train_test_split
from sklearn.metrics import accuracy_score, classification_report, confusion_matrix


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Column names must match your own data file (override with --text-col / --label-col).
TEXT_COLUMN = "feedback_text"
LABEL_COLUMN = "label"

# Same alphabetical order as inference (10 classes, 'others' excluded)
LABEL_NAMES = [
    'attractive', 'bright', 'calm', 'comfortable', 'cozy',
    'excitement', 'open', 'pleasant', 'simple', 'spacious'
]
LABEL2IDX = {l: i for i, l in enumerate(LABEL_NAMES)}


# ===== Text preprocessing (same as inference) =====
_re_url = re.compile(r'https?://\S+|www\.\S+')
_re_emoji = re.compile(
    "["u"\U0001F600-\U0001F64F"
    u"\U0001F300-\U0001F5FF"
    u"\U0001F680-\U0001F6FF"
    u"\U0001F1E0-\U0001F1FF"
    "]+", flags=re.UNICODE
)
_re_repeat = re.compile(r'(.)\1{2,}')


def preprocess_text(t):
    if not isinstance(t, str): t = str(t)
    t = _re_url.sub(' ', t)
    t = _re_emoji.sub(' ', t)
    t = _re_repeat.sub(r'\1\1', t)
    t = re.sub(r'[^0-9A-Za-z\uAC00-\uD7A3\u3131-\u314E\u314F-\u3163\s.,!?~\-]+', ' ', t)
    t = re.sub(r'\s+', ' ', t).strip()
    return t


# ===== Dataset =====
class EmotionDataset(Dataset):
    def __init__(self, texts, labels, tokenizer, max_len=128):
        self.texts = texts; self.labels = labels
        self.tok = tokenizer; self.max_len = max_len

    def __len__(self):
        return len(self.texts)

    def __getitem__(self, i):
        enc = self.tok.encode_plus(
            preprocess_text(self.texts[i]),
            add_special_tokens=True,
            max_length=self.max_len,
            padding='max_length',
            truncation=True,
            return_attention_mask=True,
            return_tensors='pt')
        return {
            'input_ids': enc['input_ids'].squeeze(0),
            'attention_mask': enc['attention_mask'].squeeze(0),
            'label': torch.tensor(self.labels[i], dtype=torch.long),
        }


# ===== Model (same signature as inference) =====
class EmotionBERTClassifier(nn.Module):
    def __init__(self, model_name, n_classes, dropout=0.3):
        super().__init__()
        self.bert = BertModel.from_pretrained(model_name)
        self.dropout = nn.Dropout(dropout)
        self.classifier = nn.Linear(self.bert.config.hidden_size, n_classes)

    def forward(self, input_ids, attention_mask):
        out = self.bert(input_ids=input_ids, attention_mask=attention_mask)
        return self.classifier(self.dropout(out.pooler_output))


def set_seed(s):
    random.seed(s); np.random.seed(s)
    torch.manual_seed(s); torch.cuda.manual_seed_all(s)


def run_epoch(model, loader, optimizer, criterion, device, train=True):
    model.train() if train else model.eval()
    losses, ys, yhats = [], [], []
    ctx = torch.enable_grad() if train else torch.no_grad()
    with ctx:
        for batch in loader:
            ids = batch['input_ids'].to(device)
            mask = batch['attention_mask'].to(device)
            y = batch['label'].to(device)
            logits = model(ids, mask)
            loss = criterion(logits, y)
            if train:
                optimizer.zero_grad(); loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                optimizer.step()
            losses.append(loss.item())
            ys.extend(y.detach().cpu().numpy().tolist())
            yhats.extend(logits.argmax(dim=1).detach().cpu().numpy().tolist())
    return float(np.mean(losses)), accuracy_score(ys, yhats), ys, yhats


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--data", default=os.path.join(SCRIPT_DIR, "feedback.xlsx"))
    p.add_argument("--text-col", default=TEXT_COLUMN, help="Name of the text column")
    p.add_argument("--label-col", default=LABEL_COLUMN, help="Name of the label column")
    p.add_argument("--model-name", default="klue/bert-base")
    p.add_argument("--max-len", type=int, default=128)
    p.add_argument("--batch", type=int, default=16)
    p.add_argument("--epochs", type=int, default=5)
    p.add_argument("--lr", type=float, default=2e-5)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--val-size", type=float, default=0.2)
    p.add_argument("--out-model", default=os.path.join(SCRIPT_DIR, "NLP_Klue_BERT.pt"))
    p.add_argument("--out-tokenizer", default=os.path.join(SCRIPT_DIR, "NLP_tokenizer"))
    args = p.parse_args()

    set_seed(args.seed)
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"Device: {device}")
    if device.type == 'cuda':
        print(f"GPU: {torch.cuda.get_device_name(0)}")

    # ---- Load data ----
    df = pd.read_excel(args.data)
    df.columns = [c.strip() for c in df.columns]
    text_col = args.text_col
    label_col = args.label_col
    df = df[[text_col, label_col]].dropna()
    df[label_col] = df[label_col].astype(str).str.strip().str.lower()
    print(f"[OK] Raw rows: {len(df)}")

    # Drop 'others' -> 10 classes
    n_before = len(df)
    df = df[df[label_col].isin(LABEL_NAMES)].reset_index(drop=True)
    print(f"[FIX] Dropped unused labels such as 'others': {n_before} -> {len(df)} rows")

    df['label_idx'] = df[label_col].map(LABEL2IDX)
    print("\n[Class distribution]")
    print(df[label_col].value_counts().to_string())

    # ---- split ----
    X = df[text_col].tolist()
    y = df['label_idx'].tolist()
    X_tr, X_va, y_tr, y_va = train_test_split(
        X, y, test_size=args.val_size, random_state=args.seed,
        stratify=y if min(pd.Series(y).value_counts()) >= 2 else None)
    print(f"\n[Split] train={len(X_tr)}, val={len(X_va)}")

    # ---- tokenizer / model ----
    tokenizer = BertTokenizer.from_pretrained(args.model_name)
    model = EmotionBERTClassifier(args.model_name, n_classes=len(LABEL_NAMES)).to(device)

    # Class weights (to correct imbalance)
    counts = np.bincount(y_tr, minlength=len(LABEL_NAMES)).astype(np.float32)
    weights = (counts.sum() / (len(LABEL_NAMES) * np.clip(counts, 1, None))).astype(np.float32)
    print(f"[Class weights] {dict(zip(LABEL_NAMES, np.round(weights, 3).tolist()))}")
    criterion = nn.CrossEntropyLoss(weight=torch.tensor(weights).to(device))

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)

    tr_loader = DataLoader(
        EmotionDataset(X_tr, y_tr, tokenizer, args.max_len),
        batch_size=args.batch, shuffle=True)
    va_loader = DataLoader(
        EmotionDataset(X_va, y_va, tokenizer, args.max_len),
        batch_size=args.batch, shuffle=False)

    # ---- Train ----
    best_acc = -1.0
    best_state = None
    for ep in range(1, args.epochs + 1):
        tl, ta, _, _ = run_epoch(model, tr_loader, optimizer, criterion, device, train=True)
        vl, va, ys, yhats = run_epoch(model, va_loader, optimizer, criterion, device, train=False)
        flag = ""
        if va > best_acc:
            best_acc = va
            best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
            flag = " <-- best"
        print(f"  Ep {ep:02d} | train loss={tl:.4f} acc={ta:.4f} | val loss={vl:.4f} acc={va:.4f}{flag}")

    # ---- Save best ----
    if best_state is None:
        best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
    torch.save(best_state, args.out_model)
    tokenizer.save_pretrained(args.out_tokenizer)
    print(f"\n[OK] Saved")
    print(f"  - {args.out_model} ({os.path.getsize(args.out_model)/1024/1024:.1f} MB)")
    print(f"  - {args.out_tokenizer}/")
    print(f"  best val acc = {best_acc:.4f}")

    # ---- Final validation report (load best weights) ----
    model.load_state_dict(best_state)
    _, va_final, ys, yhats = run_epoch(model, va_loader, optimizer, criterion, device, train=False)
    print("\n[Validation report]")
    print(classification_report(ys, yhats, target_names=LABEL_NAMES, zero_division=0))


if __name__ == "__main__":
    main()
