from __future__ import annotations

import importlib.util
import json
import os
import sys
import threading
from pathlib import Path

for _key, _val in (
    ("TF_CPP_MIN_LOG_LEVEL", "3"),
    ("CUDA_VISIBLE_DEVICES", "-1"),
    ("OMP_NUM_THREADS", "1"),
    ("OPENBLAS_NUM_THREADS", "1"),
    ("MKL_NUM_THREADS", "1"),
    ("NUMEXPR_NUM_THREADS", "1"),
    ("TF_NUM_INTRAOP_THREADS", "1"),
    ("TF_NUM_INTEROP_THREADS", "1"),
):
    os.environ.setdefault(_key, _val)

MODELS_DIR = Path(os.environ.get("ESSENTIA_MODELS_DIR", Path(__file__).resolve().parent / "models"))

_embedding_model = None
_genre_model = None
_heads = None
_labels = None
_essentia_logs_configured = False
_analyze_lock = threading.Lock()

MOODS = ("happy", "sad", "aggressive", "relaxed", "party")


def _configure_essentia_logging():
    global _essentia_logs_configured
    import essentia

    if _essentia_logs_configured:
        return
    essentia.log.infoActive = False
    essentia.log.warningActive = False
    _essentia_logs_configured = True


def _load_json(stem):
    with open(MODELS_DIR / f"{stem}.json", encoding="utf-8") as f:
        return json.load(f)


def _graph(name: str) -> str:
    path = MODELS_DIR / name
    if not path.is_file() or path.stat().st_size == 0:
        raise FileNotFoundError(
            f"Tensorflow graph not found: {path}. "
            "The .pb weights are not in git; run analyzer/download_models.py "
            "or rebuild the image so the models are baked in."
        )
    return str(path)


def _softmax_head(stem):
    from essentia.standard import TensorflowPredict2D

    return TensorflowPredict2D(
        graphFilename=_graph(f"{stem}.pb"),
        input="model/Placeholder",
        output="model/Softmax",
    )


def _identity_head(stem):
    from essentia.standard import TensorflowPredict2D

    return TensorflowPredict2D(
        graphFilename=_graph(f"{stem}.pb"),
        input="model/Placeholder",
        output="model/Identity",
    )


def _sigmoid_head(stem):
    from essentia.standard import TensorflowPredict2D

    return TensorflowPredict2D(
        graphFilename=_graph(f"{stem}.pb"),
        input="model/Placeholder",
        output="model/Sigmoid",
    )


def _ensure_models():
    global _embedding_model, _genre_model, _heads, _labels
    if _embedding_model is not None:
        return

    _configure_essentia_logging()
    from essentia.standard import TensorflowPredict2D, TensorflowPredictEffnetDiscogs

    _embedding_model = TensorflowPredictEffnetDiscogs(
        graphFilename=_graph("discogs-effnet-bs64-1.pb"),
        output="PartitionedCall:1",
    )
    _genre_model = TensorflowPredict2D(
        graphFilename=_graph("genre_discogs400-discogs-effnet-1.pb"),
        input="serving_default_model_Placeholder",
        output="PartitionedCall:0",
    )
    _heads = {
        "moods": {mood: _softmax_head(f"mood_{mood}-discogs-effnet-1") for mood in MOODS},
        "danceability": _softmax_head("danceability-discogs-effnet-1"),
        "acoustic": _softmax_head("mood_acoustic-discogs-effnet-1"),
        "electronic": _softmax_head("mood_electronic-discogs-effnet-1"),
        "voice": _softmax_head("voice_instrumental-discogs-effnet-1"),
        "timbre": _softmax_head("timbre-discogs-effnet-1"),
        "tonal": _softmax_head("tonal_atonal-discogs-effnet-1"),
        "approachability": _identity_head("approachability_regression-discogs-effnet-1"),
        "engagement": _identity_head("engagement_regression-discogs-effnet-1"),
        "themes": _sigmoid_head("mtg_jamendo_moodtheme-discogs-effnet-1"),
        "instruments": _sigmoid_head("mtg_jamendo_instrument-discogs-effnet-1"),
    }
    _labels = {
        "genre": _load_json("genre_discogs400-discogs-effnet-1")["classes"],
        "moods": {mood: _load_json(f"mood_{mood}-discogs-effnet-1")["classes"] for mood in MOODS},
        "danceability": _load_json("danceability-discogs-effnet-1")["classes"],
        "acoustic": _load_json("mood_acoustic-discogs-effnet-1")["classes"],
        "electronic": _load_json("mood_electronic-discogs-effnet-1")["classes"],
        "voice": _load_json("voice_instrumental-discogs-effnet-1")["classes"],
        "timbre": _load_json("timbre-discogs-effnet-1")["classes"],
        "tonal": _load_json("tonal_atonal-discogs-effnet-1")["classes"],
        "themes": _load_json("mtg_jamendo_moodtheme-discogs-effnet-1")["classes"],
        "instruments": _load_json("mtg_jamendo_instrument-discogs-effnet-1")["classes"],
    }


def essentia_available() -> bool:
    try:
        return importlib.util.find_spec("essentia") is not None
    except (ImportError, ValueError, ModuleNotFoundError):
        return False


def _jsonify(obj):
    if isinstance(obj, dict):
        return {str(key): _jsonify(value) for key, value in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_jsonify(value) for value in obj]
    if hasattr(obj, "item"):
        try:
            return obj.item()
        except (ValueError, AttributeError):
            pass
    if isinstance(obj, (str, int, float, bool)) or obj is None:
        return obj
    return str(obj)


def pretty_genre(label: str) -> str:
    return label.replace("---", "/")


def _probs(preds, classes):
    avg = preds.mean(axis=0)
    return {cls: float(avg[i]) for i, cls in enumerate(classes)}


def _scalar(preds):
    avg = preds.mean(axis=0)
    return float(avg[0] if hasattr(avg, "__len__") else avg)


def _topk(preds, classes, k=5):
    avg = preds.mean(axis=0)
    order = avg.argsort()[::-1][:k]
    return [(classes[i], float(avg[i])) for i in order]


def _dsp_profile(filepath):
    from essentia.standard import KeyExtractor, Loudness, MonoLoader, PercivalBpmEstimator

    audio = MonoLoader(filename=str(filepath), sampleRate=44100, resampleQuality=4)()
    profile = {"bpm": None, "key": None, "key_strength": None, "loudness": None}
    try:
        profile["bpm"] = round(float(PercivalBpmEstimator()(audio)), 2)
    except Exception:
        pass
    try:
        key, scale, strength = KeyExtractor()(audio)
        profile["key"] = f"{key} {scale}".strip()
        profile["key_strength"] = float(strength)
    except Exception:
        pass
    try:
        profile["loudness"] = float(Loudness()(audio))
    except Exception:
        pass
    return profile


def analyze(filepath):
    with _analyze_lock:
        return _analyze_locked(filepath)


def _analyze_locked(filepath):
    _configure_essentia_logging()
    from essentia.standard import MonoLoader

    _ensure_models()
    audio = MonoLoader(filename=str(filepath), sampleRate=16000, resampleQuality=4)()
    embeddings = _embedding_model(audio)

    genre_preds = _genre_model(embeddings).mean(axis=0)
    top_genre_idx = genre_preds.argsort()[-3:][::-1]
    genres = [_labels["genre"][i] for i in top_genre_idx]
    genre_scores = [
        (pretty_genre(_labels["genre"][i]), float(genre_preds[i])) for i in top_genre_idx
    ]

    moods = {}
    for mood, model in _heads["moods"].items():
        probs = _probs(model(embeddings), _labels["moods"][mood])
        moods[mood] = probs[mood]

    dance = _probs(_heads["danceability"](embeddings), _labels["danceability"])
    acoustic = _probs(_heads["acoustic"](embeddings), _labels["acoustic"])
    electronic = _probs(_heads["electronic"](embeddings), _labels["electronic"])
    voice = _probs(_heads["voice"](embeddings), _labels["voice"])
    timbre = _probs(_heads["timbre"](embeddings), _labels["timbre"])
    tonal = _probs(_heads["tonal"](embeddings), _labels["tonal"])

    result = {
        "genres": genres,
        "moods": moods,
        "top_mood": max(moods, key=moods.get),
        "danceability": dance["danceable"],
        "acoustic": acoustic["acoustic"],
        "electronic": electronic["electronic"],
        "voice": voice["voice"],
        "instrumental": voice["instrumental"],
        "timbre": max(timbre, key=timbre.get),
        "timbre_bright": timbre["bright"],
        "tonal": tonal["tonal"],
        "approachability": _scalar(_heads["approachability"](embeddings)),
        "engagement": _scalar(_heads["engagement"](embeddings)),
        "themes": _topk(_heads["themes"](embeddings), _labels["themes"]),
        "instruments": _topk(_heads["instruments"](embeddings), _labels["instruments"]),
        "genre_scores": genre_scores,
    }
    result.update(_dsp_profile(filepath))
    return _jsonify(result)


def format_profile(analysis):
    parts = [
        f"Dance {analysis['danceability']:.2f}",
        f"Voice {analysis['voice']:.2f}",
        f"Acoustic {analysis['acoustic']:.2f}",
        f"Electronic {analysis['electronic']:.2f}",
        analysis["timbre"].title(),
        f"Approach {analysis['approachability']:.2f}",
        f"Engage {analysis['engagement']:.2f}",
    ]
    if analysis.get("bpm"):
        parts.append(f"{analysis['bpm']:.0f} BPM")
    if analysis.get("key"):
        parts.append(analysis["key"])
    line = " · ".join(parts)
    themes = ", ".join(name for name, _ in analysis.get("themes") or [])
    instruments = ", ".join(name for name, _ in analysis.get("instruments") or [])
    extra = []
    if themes:
        extra.append(f"Themes: {themes}")
    if instruments:
        extra.append(f"Instruments: {instruments}")
    return line + (("\n" + "\n".join(extra)) if extra else "")


def analyze_file(filepath, title=None, artist=None):
    analysis = analyze(filepath)
    analysis["genres"] = [pretty_genre(g) for g in analysis.get("genres") or []]
    analysis["profile"] = format_profile(analysis)
    if title:
        analysis["title"] = title
    if artist:
        analysis["artist"] = artist
    return _jsonify(analysis)


def _label_payload(items):
    labels = []
    for item in items or []:
        if isinstance(item, str):
            name = pretty_genre(item)
            if name:
                labels.append({"name": name})
            continue
        if isinstance(item, (list, tuple)) and item:
            name = pretty_genre(str(item[0]))
            if not name:
                continue
            entry = {"name": name}
            if len(item) > 1:
                try:
                    entry["score"] = float(item[1])
                except (TypeError, ValueError):
                    pass
            labels.append(entry)
            continue
        if isinstance(item, dict) and item.get("name"):
            entry = {"name": pretty_genre(str(item["name"]))}
            if item.get("score") is not None:
                try:
                    entry["score"] = float(item["score"])
                except (TypeError, ValueError):
                    pass
            labels.append(entry)
    return labels


def to_galaxy_payload(analysis):
    genres = analysis.get("genre_scores") or analysis.get("genres") or []
    payload = {
        "bpm": analysis.get("bpm"),
        "key": analysis.get("key"),
        "keyStrength": analysis.get("key_strength"),
        "loudness": analysis.get("loudness"),
        "danceability": analysis.get("danceability"),
        "acoustic": analysis.get("acoustic"),
        "electronic": analysis.get("electronic"),
        "voice": analysis.get("voice"),
        "instrumental": analysis.get("instrumental"),
        "tonal": analysis.get("tonal"),
        "timbre": analysis.get("timbre"),
        "timbreBright": analysis.get("timbre_bright"),
        "approachability": analysis.get("approachability"),
        "engagement": analysis.get("engagement"),
        "moods": analysis.get("moods") or {},
        "genres": _label_payload(genres),
        "themes": _label_payload(analysis.get("themes")),
        "instruments": _label_payload(analysis.get("instruments")),
    }
    return {key: value for key, value in payload.items() if value is not None}


def run_worker() -> None:
    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue
        try:
            job = json.loads(line)
            result = analyze_file(
                job["path"],
                title=job.get("title"),
                artist=job.get("artist"),
            )
            galaxy = to_galaxy_payload(result)
            sys.stdout.write(
                json.dumps({"ok": True, "result": result, "galaxy": galaxy}, ensure_ascii=False)
                + "\n"
            )
        except Exception as exc:
            sys.stdout.write(
                json.dumps(
                    {
                        "ok": False,
                        "error": str(exc),
                        "error_type": type(exc).__name__,
                    },
                    ensure_ascii=False,
                )
                + "\n"
            )
        sys.stdout.flush()


if __name__ == "__main__":
    if "--worker" not in sys.argv:
        print("Usage: tag_library.py --worker", file=sys.stderr)
        raise SystemExit(2)
    run_worker()
