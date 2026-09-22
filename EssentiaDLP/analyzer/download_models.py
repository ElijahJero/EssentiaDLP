"""Download Essentia .pb graphs referenced by the JSON metadata in this folder."""

from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path

MODELS_DIR = Path(__file__).resolve().parent / "models"


def graph_urls(models: Path) -> dict[str, str]:
    urls: dict[str, str] = {}
    for meta in sorted(models.glob("*.json")):
        data = json.loads(meta.read_text(encoding="utf-8"))
        link = data.get("link")
        if isinstance(link, str) and link.endswith(".pb"):
            urls[Path(link).name] = link
        embedding = data.get("inference", {}).get("embedding_model", {})
        embedding_link = embedding.get("link") if isinstance(embedding, dict) else None
        if isinstance(embedding_link, str) and embedding_link.endswith(".pb"):
            urls[Path(embedding_link).name] = embedding_link
    return urls


def download_missing(models: Path) -> list[str]:
    models.mkdir(parents=True, exist_ok=True)
    fetched: list[str] = []
    for name, url in graph_urls(models).items():
        dest = models / name
        if dest.is_file() and dest.stat().st_size > 0:
            continue
        print(f"download {name}", file=sys.stderr, flush=True)
        tmp = dest.with_suffix(dest.suffix + ".part")
        try:
            urllib.request.urlretrieve(url, tmp)
            if not tmp.is_file() or tmp.stat().st_size == 0:
                raise OSError(f"empty download for {name}")
            tmp.replace(dest)
        except Exception:
            tmp.unlink(missing_ok=True)
            raise
        fetched.append(name)
    return fetched


def main() -> None:
    models = Path(sys.argv[1]) if len(sys.argv) > 1 else MODELS_DIR
    missing = [name for name, _ in graph_urls(models).items() if not (models / name).is_file()]
    download_missing(models)
    still_missing = [name for name in missing if not (models / name).is_file() or (models / name).stat().st_size == 0]
    if still_missing:
        raise SystemExit(f"missing graphs: {', '.join(still_missing)}")
    print(f"models ready in {models}", flush=True)


if __name__ == "__main__":
    main()
