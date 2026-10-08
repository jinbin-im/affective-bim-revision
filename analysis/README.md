# Analysis

Two scripts check the main design choices of the system. They need your own data and trained
model files, which are not part of this repository.

| Script | What it checks | Output |
|---|---|---|
| `classifier_cv.py` | 5-fold cross-validation of the text classifier (`klue/bert-base`, fine-tuned) used by `python/NLP.py` | `classifier_cv_results.json` |
| `objective_comparison.py` | How each design-selection rule behaves on 30 scenarios: the earlier optimal-transport rule, simpler rules, and the composite rule used now in `python/Optimizer.py` | `objective_comparison_results.json` |
| `grid_loader.py` | Shared loader for the model, the 6,480 candidate designs and the cost matrix | – |

Settings are read from environment variables:

| Variable | Meaning |
|---|---|
| `ALIS_TEXT_DATA` | Excel file with one text column and one label column |
| `ALIS_TEXT_COLUMN`, `ALIS_LABEL_COLUMN` | Column names in that file (default `feedback_text`, `label`) |
| `ALIS_MODEL_DIR` | Folder with `preprocessor.pkl` and the trained affect model |
| `ALIS_MODEL_FILE` | Model file name (default `inference_transformer.pkl`) |

Example:
```
ALIS_MODEL_DIR=/path/to/models python3 objective_comparison.py
```

`pip install -r requirements.txt` (Python 3.10 or later). The classifier script runs faster on a GPU.
