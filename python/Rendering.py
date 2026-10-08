#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
LIRA Step 5 - rendering with the Gemini Image API (Nano Banana 2)
Reads after_variables.json and creates two versions of an interior rendering.
  - rendering_empty.png  : empty room (shown in the LIRA UI)
  - rendering_staged.png : furnished room (kept as a file)

[2026-05-06 patch] Added a warm-up call to avoid Gemini API cold starts.
A first call on the cold routing path could take 5+ minutes and ignore
the instruction to keep the room structure; the warm-up reduces this.
"""

import os
import sys
import json
import base64
import re
import time

# Reset Windows console from cp949 to utf-8 (safe output of non-ASCII characters)
if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except Exception:
        import codecs
        try:
            sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'replace')
            sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'replace')
        except Exception:
            pass

# ======================================================================
# Settings
# ======================================================================
class Config:
    # Gemini Image API
    # Nano Banana 2 = gemini-3.1-flash-image-preview (fast, cheap)
    # Nano Banana Pro = gemini-3-pro-image-preview (high quality, 4K)
    MODEL_EMPTY  = "gemini-3.1-flash-image-preview"   # empty room -> for LIRA UI
    MODEL_STAGED = "gemini-3.1-flash-image-preview"   # furnished -> for archive

    AFTER_VARIABLES  = r"C:\Temp\after_variables.json"
    OUTPUT_EMPTY     = r"C:\Temp\rendering_empty.png"
    OUTPUT_STAGED    = r"C:\Temp\rendering_staged.png"
    REFERENCE_CAMERA = r"C:\Temp\rendering_reference_camera.png"  # Revit viewport capture

    # One status file per mode. If `before` and `after` run at the same time (e.g. an earlier
    # fire-and-forget staged run is still alive), a shared file could be overwritten and the
    # poller could match the wrong run. The path is chosen by mode in main().
    @staticmethod
    def status_path(mode: str) -> str:
        return rf"C:\Temp\rendering_status_{mode}.json"

    # Warm-up settings
    # Off by default: the backend stays warm when called often, so waking it every time is pointless.
    # Enable with the env variable ALIS_RENDER_WARMUP=1 only in truly cold environments.
    WARMUP_ENABLED   = os.environ.get('ALIS_RENDER_WARMUP', '0') == '1'
    WARMUP_TIMEOUT_S = 10     # If warm-up takes too long, just continue (cumulative cap)


# ======================================================================
# Read after_variables.json
# ======================================================================
def load_current_variables():
    """Read current_analysis.txt and return space variables (for before mode)."""
    path = r"C:\Temp\current_analysis.txt"
    if not os.path.exists(path):
        raise FileNotFoundError(f"current_analysis.txt not found: {path}")
    data = {}
    with open(path, 'r', encoding='utf-8') as f:
        for line in f:
            if ':' in line:
                k, v = line.split(':', 1)
                data[k.strip()] = v.strip()
    wwr_val = float(data.get('WWR', '0.4'))
    if wwr_val >= 1.0:
        wwr_val /= 100.0
    return {
        "ceiling_height_mm": float(data.get('Ceiling Height', '2700').replace('m','').strip()) * 1000
                              if '.' in data.get('Ceiling Height','2700') 
                              else float(data.get('Ceiling Height','2700')),
        "wwr_ratio":     wwr_val,
        "cct_k":         float(data.get('CCT', '4000').replace('K','').strip()),
        "room_color":    data.get('Room Color', 'White'),
        "floor_material":data.get('Floor Material', 'Wood'),
    }



def load_after_variables():
    if not os.path.exists(Config.AFTER_VARIABLES):
        raise FileNotFoundError(f"after_variables.json not found: {Config.AFTER_VARIABLES}")
    with open(Config.AFTER_VARIABLES, 'r', encoding='utf-8') as f:
        return json.load(f)


# ======================================================================
# Space variables -> prompt
# ======================================================================
FLOOR_KO = {
    "wood":     "solid wood flooring",
    "tile":     "large porcelain tiles",
    "carpet":   "fabric carpet",
    "concrete": "polished concrete",
    "marble":   "marble",
}
COLOR_KO = {
    "white": "bright white",
    "beige": "warm beige",
    "gray":  "modern gray",
    "grey":  "modern gray",
}
CCT_DESC = {
    (0,    3200): "warm orange-tinted incandescent-style lighting",
    (3200, 4200): "natural neutral white lighting",
    (4200, 5500): "bright, crisp cool-white lighting",
    (5500, 10**4 - 1): "cool, fresh daylight lighting",
}

def cct_to_desc(cct):
    for (lo, hi), desc in CCT_DESC.items():
        if lo <= cct < hi:
            return desc
    return "natural white lighting"

def vars_to_prompt(v, staged: bool) -> str:
    ch_m   = v.get("ceiling_height_mm", 2700) / 1000
    wwr    = v.get("wwr_ratio", 0.4)
    cct    = int(v.get("cct_k", 4000))
    color  = COLOR_KO.get(v.get("room_color", "white").lower(),  "white")
    floor  = FLOOR_KO.get(v.get("floor_material", "wood").lower(), "solid wood flooring")
    light  = cct_to_desc(cct)

    # WWR -> actual window size
    wall_w_mm = 7000
    ceil_h_mm = int(ch_m * 1000)
    total_area = wall_w_mm * ceil_h_mm
    win_area = total_area * wwr

    # Width first: max wall width - 200mm; extend height if needed
    max_w = wall_w_mm - 200
    win_w_mm = min(int(win_area / (ceil_h_mm * 0.6)), max_w)
    win_h_mm = int(win_area / win_w_mm) if win_w_mm > 0 else ceil_h_mm
    # Height limit: ceiling
    if win_h_mm > ceil_h_mm - 100:
        win_h_mm = ceil_h_mm - 100
        win_w_mm = min(int(win_area / win_h_mm), max_w)

    # WWR >= 90%: full-height glazing covering the whole wall
    if wwr >= 0.9:
        win_desc = (
            f"A curtain-wall style full window that spans the wall from the left edge to the right edge. "
            f"Glass covers the entire wall, so no wall surface is visible. "
            f"Floor to ceiling, height {ceil_h_mm - 100}mm, width {wall_w_mm}mm"
        )
    else:
        win_desc = f"{win_w_mm}mm wide x {win_h_mm}mm high (centred on the wall)"

    base = (
        f"Living room of a Korean apartment, interior photograph, wide-angle shot. "
        f"Ceiling height {ch_m:.1f}m. "
        f"Window: {win_desc}. "
        f"Wall colour {color}, floor {floor}, {light} ({cct}K). "
        f"Balanced natural and artificial light, photorealistic rendering."
    )

    if staged:
        return (
            base +
            " Place typical living room furniture naturally, such as a sofa, "
            "coffee table, TV cabinet, rug and indirect lighting. "
            "Lifestyle interior magazine style."
        )
    else:
        return (
            base +
            " No furniture. Show only the empty space. "
            "An empty living room with only floor, walls, ceiling and windows. "
            "Architectural rendering style."
        )


# ======================================================================
# Gemini API warm-up (avoid cold start)
# ======================================================================
def warmup_gemini(client, model_names):
    """
    Pre-call to work around the Gemini API cold/warm routing issue.

    Problem: if the first call goes to the cold path, it can take 5+ minutes
             and tends to ignore the structure instruction ("keep the camera angle").
             This was the main reason the first before-staged call broke.

    Fix: send a light text-mode call just before the real call to wake the routing.
         A failure here does not affect the real job (wrapped in try/except).
    """
    if not Config.WARMUP_ENABLED:
        return

    from google.genai import types as _types

    print("\n" + "─" * 70)
    print("[Warm-up] Pre-call to avoid Gemini API cold start")
    print("─" * 70)

    # Remove duplicate models (warm up once if empty/staged use the same model)
    unique_models = list(dict.fromkeys(model_names))

    for m in unique_models:
        t0 = time.time()
        try:
            # Short text-mode call -> only wakes the model backend routing
            # (much faster and cheaper than image generation)
            client.models.generate_content(
                model=m,
                contents="ok",
                config=_types.GenerateContentConfig(
                    response_modalities=['TEXT'],
                ),
            )
            elapsed = time.time() - t0
            print(f"  [warmup:{m}] OK ({elapsed:.1f}s)")
        except Exception as e:
            elapsed = time.time() - t0
            # Warm-up is best-effort; continue even if it fails
            print(f"  [warmup:{m}] skip ({elapsed:.1f}s, reason: {type(e).__name__})")
            # Stop trying if it takes too long
            if elapsed > Config.WARMUP_TIMEOUT_S:
                print(f"  [warmup] timeout; ending warm-up")
                break

    print("─" * 70)


# ======================================================================
# Serialize multiple Python instances. Concurrent calls with the same GEMINI_API_KEY
# trigger rate-limit / high-demand errors and 503 retries pile up (observed: 7-9 min).
# A file-based lock makes sure only one ALIS Python process calls Gemini at a time.
# ======================================================================
LOCK_PATH = r'C:\Temp\.gemini_busy.lock'

def _pid_alive(pid: int) -> bool:
    """Windows: check whether a PID is alive. Returns True when unsure (conservative)."""
    try:
        import ctypes
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        h = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if h == 0:
            return False
        ctypes.windll.kernel32.CloseHandle(h)
        return True
    except Exception:
        return True


def acquire_gemini_lock(status_path: str = None, status: dict = None,
                        timeout_s: int = 900, poll_s: int = 3) -> None:
    """
    Spin-wait while another ALIS Python instance is calling Gemini.
    The waiting state is written to status_path so the C# UI can show it.

    Race-safe creation with `'x'` mode + os.O_EXCL. Stale locks (dead PID) are removed automatically.
    """
    pid = os.getpid()
    deadline = time.time() + timeout_s
    waited_for_pid = None
    notified = False

    while time.time() < deadline:
        try:
            # Race-safe file creation
            fd = os.open(LOCK_PATH, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            try:
                os.write(fd, str(pid).encode('utf-8'))
            finally:
                os.close(fd)
            if notified:
                print(f"[lock] acquired (wait over, PID {pid})")
            else:
                print(f"[lock] acquired immediately (PID {pid})")
            # Clear lock_waiting in status
            if status_path and status is not None and status.get('lock_waiting'):
                status['lock_waiting'] = None
                _safe_write_status(status_path, status)
            return
        except FileExistsError:
            # Lock held by another process; check if it is stale
            try:
                with open(LOCK_PATH, 'r') as f:
                    other_pid_str = f.read().strip()
                other_pid = int(other_pid_str) if other_pid_str.isdigit() else None
            except (IOError, ValueError):
                other_pid = None

            if other_pid is None or not _pid_alive(other_pid):
                # Stale -> force remove and retry
                print(f"[lock] removed stale lock (other PID={other_pid})")
                try: os.remove(LOCK_PATH)
                except OSError: pass
                continue

            # Another live process -> wait
            if not notified or other_pid != waited_for_pid:
                print(f"[lock] another ALIS Python (PID {other_pid}) is running, waiting...")
                waited_for_pid = other_pid
                notified = True
                if status_path and status is not None:
                    status['lock_waiting'] = {
                        'other_pid': other_pid,
                        'since': time.strftime('%Y-%m-%d %H:%M:%S'),
                    }
                    _safe_write_status(status_path, status)
            time.sleep(poll_s)

    # timeout
    raise TimeoutError(f"Gemini lock wait timed out ({timeout_s}s); another ALIS Python process seems hung.")


def release_gemini_lock() -> None:
    """Release the lock. Remove it only if our own PID wrote it (do not touch other processes' locks)."""
    try:
        with open(LOCK_PATH, 'r') as f:
            owner_pid_str = f.read().strip()
        if owner_pid_str.isdigit() and int(owner_pid_str) == os.getpid():
            os.remove(LOCK_PATH)
            print(f"[lock] released (PID {os.getpid()})")
        else:
            print(f"[lock] not released; owned by another PID ({owner_pid_str})")
    except FileNotFoundError:
        pass
    except Exception as e:
        print(f"[lock] release error (ignored): {e}")


def _safe_write_status(status_path: str, status: dict) -> None:
    """Save status JSON; errors are swallowed so the main job is not affected."""
    try:
        with open(status_path, 'w', encoding='utf-8') as f:
            json.dump(status, f, ensure_ascii=False, indent=2)
    except Exception as e:
        print(f"[status] write error (ignored): {e}")


# ======================================================================
# Gemini API call
# ======================================================================
def _make_client():
    """Create a Gemini client (checks the api_key)."""
    from google import genai
    api_key = os.environ.get('GEMINI_API_KEY') or os.environ.get('GOOGLE_AI_API_KEY')
    if not api_key:
        raise ValueError('GEMINI_API_KEY environment variable not set.\nSet it with: setx GEMINI_API_KEY "AIza..."')
    return genai.Client(api_key=api_key)


def _call_generate_with_timeout(client, model, contents, config, timeout_s: float):
    """
    Wrap generate_content in a daemon thread to apply a timeout.

    [WHY plain threading] A ThreadPoolExecutor `with` block calls shutdown(wait=True) on exit,
    so main also waits for the worker thread to finish -> the timeout has no effect.
    A daemon=True thread is cleaned up when the main process exits, so it is safe.

    Handles the high-latency case where the Gemini API gives no 503 but just answers slowly.
    On timeout it raises TimeoutError -> the caller's retry loop catches it and tries again.
    """
    import threading
    result = {'val': None, 'exc': None}

    def _worker():
        try:
            result['val'] = client.models.generate_content(
                model=model, contents=contents, config=config)
        except Exception as e:
            result['exc'] = e

    t = threading.Thread(target=_worker, daemon=True)
    t.start()
    t.join(timeout=timeout_s)

    if t.is_alive():
        # The daemon thread keeps running in the background until it gets a response,
        # but is cleaned up when the main process exits. Timeout triggered.
        raise TimeoutError(f"API response exceeded {timeout_s:.0f}s (high latency)")

    if result['exc'] is not None:
        raise result['exc']
    return result['val']


def generate_image(client, prompt: str, model: str, output_path: str, label: str,
                   reference_image_path: str = None,
                   mode: str = "empty",
                   status_path: str = None, status: dict = None):
    """
    Generate an image.
    mode="empty"  : non-staged version. Fix the camera angle from reference_image_path (Revit viewport), then change the design.
    mode="staged" : staged version. Add only furniture to reference_image_path (the empty result).
    """
    from google.genai import types
    from PIL import Image
    import io

    print(f'\n[{label}] model: {model}')
    print(f'  prompt: {prompt[:120]}...')

    if reference_image_path and os.path.exists(reference_image_path):
        with open(reference_image_path, 'rb') as f:
            ref_bytes = f.read()

        if mode == "staged":
            instruction = (
                "The photo below shows an empty room. Keep the room structure, camera angle, "
                "window position and size, wall colour, flooring, ceiling height and light fixture shapes "
                "exactly the same, and only add furniture naturally, such as a sofa, TV cabinet, "
                "coffee table and plants. "
                "[STRICTLY FORBIDDEN]: Never add a balcony, terrace, veranda or any outdoor space. Indoor living room only. "
                "Keep the light fixture shapes as they are; change only the colour temperature as given below. "
                "Realistic interior photograph style.\n"
                f"Additional conditions: {prompt}"
            )
        else:
            instruction = (
                "The photo below is a Revit viewport capture. "
                "Use this photo only for the camera angle (viewpoint, direction, field of view). "
                "For the design elements (wall colour, flooring, ceiling height, light colour temperature, window size), follow the [Conditions] below. "
                "[STRICTLY FORBIDDEN]: Never add a balcony, terrace, veranda or any outdoor space. Indoor living room only. "
                "Keep the light fixture shapes the same as in the photo; apply only the colour temperature from [Conditions]. "
                "Render only the empty interior space, with no furniture.\n"
                f"[Conditions]: {prompt}"
            )

        contents = [
            types.Part.from_bytes(data=ref_bytes, mime_type='image/png'),
            instruction,
        ]
        print(f'  reference: {reference_image_path} (mode={mode})')
    else:
        # No reference: generate from the text prompt only
        print(f'  no reference - using text prompt only')
        contents = prompt

    # Retry loop: automatic retry with exponential backoff for transient errors
    # from the Gemini Image API, such as 503 UNAVAILABLE / 429 RATE_LIMITED.
    # Retries can recover from a first cold start or high demand.
    max_retries = int(os.environ.get('ALIS_RENDER_RETRIES', '4'))
    # Per-call timeout to prevent Gemini high-latency hangs. Default 120s.
    # Warm calls take 30-60s; only the cold path takes 120s+ -> the second attempt after a timeout
    # is likely warm, so it recovers quickly.
    call_timeout_s = float(os.environ.get('ALIS_RENDER_CALL_TIMEOUT_S', '120'))

    response = None
    last_err = None
    cfg = types.GenerateContentConfig(response_modalities=['IMAGE', 'TEXT'])
    for attempt in range(1, max_retries + 1):
        # Write progress to status; the C# UI polls and shows it
        if status_path is not None and status is not None:
            status['rendering_progress'] = {
                'stage': label,         # "empty" / "staged"
                'mode_kind': mode,      # "empty" / "staged"
                'attempt': attempt,
                'max_retries': max_retries,
                'call_timeout_s': call_timeout_s,
                'started_at': time.strftime('%Y-%m-%d %H:%M:%S'),
            }
            _safe_write_status(status_path, status)

        t0 = time.time()
        try:
            response = _call_generate_with_timeout(client, model, contents, cfg, call_timeout_s)
            elapsed = time.time() - t0
            print(f'  API response time: {elapsed:.1f}s (attempt {attempt}/{max_retries})')
            break
        except Exception as e:
            elapsed = time.time() - t0
            err_str = str(e)
            err_upper = err_str.upper()
            is_transient = any(s in err_upper for s in ['503', '429', 'UNAVAILABLE', 'OVERLOADED',
                                                         'TIMEOUT', 'RESOURCE_EXHAUSTED',
                                                         'INTERNAL', 'DEADLINE_EXCEEDED',
                                                         'HIGH LATENCY'])
            last_err = e
            if attempt < max_retries and is_transient:
                wait = min(60, 5 * (2 ** (attempt - 1)))  # 5, 10, 20, 40 (cap 60)
                print(f'  [WARN] attempt {attempt}/{max_retries} failed ({elapsed:.1f}s, transient): '
                      f'{err_str[:120]}\n  -> retrying in {wait}s')
                # Also write the next-retry wait to status
                if status_path is not None and status is not None:
                    status['rendering_progress'] = dict(status['rendering_progress'])
                    status['rendering_progress']['next_wait_s'] = wait
                    status['rendering_progress']['last_error'] = err_str[:200]
                    _safe_write_status(status_path, status)
                time.sleep(wait)
                continue
            # Non-transient or last attempt -> re-raise
            raise
    if response is None:
        raise last_err if last_err else RuntimeError('Unknown API failure')

    # Extract image
    image_data = None
    for part in response.candidates[0].content.parts:
        if part.inline_data is not None:
            image_data = part.inline_data.data
            break

    if image_data is None:
        raise ValueError(f'[{label}] no image data - the model returned no image')

    # Save image
    if isinstance(image_data, (bytes, bytearray)):
        img_bytes = image_data
    else:
        img_bytes = base64.b64decode(image_data)

    img = Image.open(io.BytesIO(img_bytes))
    img.save(output_path)
    print(f'  saved: {output_path} ({img.size[0]}x{img.size[1]})')

def main():
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["before", "after"], default="before",
                        help="before: from current_analysis.txt / after: from after_variables.json")
    parser.add_argument('--no-staged', action='store_true',
                    help='Skip the staged image (create non-staged only)')
    parser.add_argument('--staged-only', action='store_true',
                    help='Create only the staged image (skip non-staged)')
    parser.add_argument('--no-warmup', action='store_true',
                    help='Skip the warm-up call (for debugging)')
    args = parser.parse_args()

    print("=" * 70)
    print(f"LIRA Step 5 - rendering (mode: {args.mode})")
    print("=" * 70)

    from datetime import datetime
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    mode = args.mode  # "before" or "after"

    # Output paths (timestamped files only)
    ts_empty  = os.path.join(r"C:\Temp", f"rendering_{mode}_{ts}.png")
    ts_staged = os.path.join(r"C:\Temp", f"rendering_{mode}_staged_{ts}.png")
    # Fixed paths: so that --staged-only can find the empty image
    fixed_empty  = Config.OUTPUT_EMPTY
    fixed_staged = Config.OUTPUT_STAGED

    # One status file per mode, to avoid a race when before/after run at the same time.
    status_path = Config.status_path(mode)

    status = {"success": False, "empty": None, "staged": None, "error": None, "mode": mode}

    # ── Acquire lock; wait if another ALIS Python instance is running ──
    # Write the status file once before this step so the C# poller can see the lock_waiting state.
    _safe_write_status(status_path, status)
    lock_acquired = False
    try:
        acquire_gemini_lock(status_path=status_path, status=status)
        lock_acquired = True
    except TimeoutError as e:
        status['error'] = str(e)
        _safe_write_status(status_path, status)
        print(f"\n[ERROR] {e}")
        sys.exit(1)

    try:
        # 0. Gemini client + warm-up
        client = _make_client()

        # Choose models to warm up (only those this run will call)
        if args.no_warmup:
            print("\n[Warm-up] skipped by --no-warmup flag")
        else:
            warmup_models = []
            if not args.staged_only:
                warmup_models.append(Config.MODEL_EMPTY)
            if not args.no_staged:
                warmup_models.append(Config.MODEL_STAGED)
            if warmup_models:
                warmup_gemini(client, warmup_models)

        # 1. Load variables (source depends on mode)
        if args.mode == "before":
            v = load_current_variables()   # current_analysis.txt
        else:
            v = load_after_variables()     # after_variables.json
        print(f"\n  Ceiling height: {v.get('ceiling_height_mm')}mm")
        print(f"  WWR:     {v.get('wwr_ratio')}")
        print(f"  CCT:     {v.get('cct_k')}K")
        print(f"  Wall colour: {v.get('room_color')}")
        print(f"  Floor:   {v.get('floor_material')}")

        # 2. Empty version - use the Revit viewport as the camera-angle reference
        import shutil
        if not args.staged_only:
            prompt_empty = vars_to_prompt(v, staged=False)
            revit_ref = Config.REFERENCE_CAMERA
            generate_image(client, prompt_empty, Config.MODEL_EMPTY, ts_empty, "empty",
                           reference_image_path=revit_ref if os.path.exists(revit_ref) else None,
                           mode="empty",
                           status_path=status_path, status=status)
            status["empty"] = ts_empty
            # Clear rendering_progress when empty is done
            status['rendering_progress'] = None
            # Also copy to the fixed path (so a --staged-only process can find it)
            shutil.copy2(ts_empty, fixed_empty)
            print(f"  copied to fixed path: {fixed_empty}")

            # Save status right after empty is done; the C# UI polls this as the signal to close the progress window
            # (in combined mode, staged keeps running in the background)
            _safe_write_status(status_path, status)
            print(f"  intermediate status saved (empty done, staged running): {status_path}")

        # 3. Furnished version - use the empty result as reference (keep angle + design, add furniture only)
        if not args.no_staged:
            # ts_empty may be created by another process -> timestamp may differ -> fall back to fixed path
            ref_for_staged = ts_empty if os.path.exists(ts_empty) else fixed_empty
            print(f"  [staged ref] {ref_for_staged} (exists: {os.path.exists(ref_for_staged)})")
            prompt_staged = vars_to_prompt(v, staged=True)
            generate_image(client, prompt_staged, Config.MODEL_STAGED, ts_staged, "staged",
                           reference_image_path=ref_for_staged,
                           mode="staged",
                           status_path=status_path, status=status)
            status["staged"] = ts_staged
            status['rendering_progress'] = None

        status["success"] = True
        print(f"\n[OK] Rendering done!")

    except Exception as e:
        status["error"] = str(e)
        print(f"\n[ERROR] {e}")
        import traceback
        traceback.print_exc()

    finally:
        _safe_write_status(status_path, status)
        print(f"  status saved: {status_path}")
        if lock_acquired:
            release_gemini_lock()

    sys.exit(0 if status["success"] else 1)


if __name__ == "__main__":
    main()
