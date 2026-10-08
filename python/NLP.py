#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
BERT emotion classification inference script.
Usage: python NLP.py "input text"
"""

import sys
import re
import torch
import torch.nn as nn
from transformers import BertTokenizer, BertModel
import os

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# ======================================================================
# Settings
# ======================================================================
class Config:
    MODEL_PATH = os.path.join(BASE_DIR, 'NLP_Klue_BERT.pt')
    TOKENIZER_PATH = os.path.join(BASE_DIR, 'NLP_tokenizer')
    MODEL_NAME = 'klue/bert-base'
    MAX_LENGTH = 128
    DEVICE = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    OUTPUT_FILE = r'C:\Temp\target_emotion.txt'

# Emotion labels (must match the order used in training)
LABEL_NAMES = [
    'attractive', 'bright', 'calm', 'comfortable', 'cozy', 'excitement', 'open', 'pleasant', 'simple', 'spacious'
]

# ======================================================================
# Preprocessing
# ======================================================================
_re_url = re.compile(r'https?://\S+|www\.\S+')
_re_emoji = re.compile(
    "["u"\U0001F600-\U0001F64F"
    u"\U0001F300-\U0001F5FF"
    u"\U0001F680-\U0001F6FF"
    u"\U0001F1E0-\U0001F1FF"
    "]+", flags=re.UNICODE
)
_re_repeat = re.compile(r'(.)\1{2,}')

def preprocess_text(text: str) -> str:
    """Clean input text."""
    if not isinstance(text, str):
        text = str(text)
    text = _re_url.sub(' ', text)
    text = _re_emoji.sub(' ', text)
    text = _re_repeat.sub(r'\1\1', text)
    text = re.sub(r'[^0-9A-Za-z\uAC00-\uD7A3\u3131-\u314E\u314F-\u3163\s.,!?~\-]+', ' ', text)
    text = re.sub(r'\s+', ' ', text).strip()
    return text

# ======================================================================
# Model definition
# ======================================================================
class EmotionBERTClassifier(nn.Module):
    def __init__(self, n_classes, dropout=0.3):
        super().__init__()
        self.bert = BertModel.from_pretrained(Config.MODEL_NAME)
        self.dropout = nn.Dropout(dropout)
        self.classifier = nn.Linear(self.bert.config.hidden_size, n_classes)
    
    def forward(self, input_ids, attention_mask):
        outputs = self.bert(input_ids=input_ids, attention_mask=attention_mask)
        return self.classifier(self.dropout(outputs.pooler_output))

# ======================================================================
# Inference
# ======================================================================
def predict_emotion(text: str) -> str:
    """Classify the emotion of the input text."""
    
    # Load tokenizer
    tokenizer = BertTokenizer.from_pretrained(Config.TOKENIZER_PATH)
    
    # Load model
    model = EmotionBERTClassifier(n_classes=len(LABEL_NAMES), dropout=0.3)
    model.load_state_dict(torch.load(Config.MODEL_PATH, map_location=Config.DEVICE))
    model = model.to(Config.DEVICE)
    model.eval()
    
    # Preprocess
    processed_text = preprocess_text(text)
    
    # Tokenize
    encoding = tokenizer.encode_plus(
        processed_text,
        add_special_tokens=True,
        max_length=Config.MAX_LENGTH,
        padding='max_length',
        truncation=True,
        return_attention_mask=True,
        return_tensors='pt'
    )
    
    input_ids = encoding['input_ids'].to(Config.DEVICE)
    attention_mask = encoding['attention_mask'].to(Config.DEVICE)
    
    # Predict
    with torch.no_grad():
        outputs = model(input_ids, attention_mask)
        _, prediction = torch.max(outputs, dim=1)
    
    predicted_label = LABEL_NAMES[prediction.item()]
    return predicted_label

# ======================================================================
# Main
# ======================================================================
if __name__ == "__main__":
    try:
        # Check command-line arguments
        if len(sys.argv) < 2:
            print("Error: input text is required.")
            print("Usage: python NLP.py \"input text\"")
            sys.exit(1)
        
        # Input text
        input_text = sys.argv[1]
        
        # Classify emotion
        predicted_emotion = predict_emotion(input_text)
        
        # Save result to file
        os.makedirs(os.path.dirname(Config.OUTPUT_FILE), exist_ok=True)
        with open(Config.OUTPUT_FILE, 'w', encoding='utf-8') as f:
            f.write(f"Target_Emotion: {predicted_emotion}")
        
        # Console output (for debugging)
        print(f"Target_Emotion: {predicted_emotion}")
        
    except Exception as e:
        print(f"Error: {str(e)}")
        sys.exit(1)
