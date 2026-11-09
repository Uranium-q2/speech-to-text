"""
Local offline translation service (stdin/stdout JSON lines) for the overlay app.
Uses Argos Translate (CTranslate2 under the hood) - fully local, no network at runtime.
Protocol: one JSON object per line on stdin -> one JSON object per line on stdout.
Request:  {"text": "...", "from": "pl", "to": "en"}
Response: {"ok": true, "text": "..."}  or  {"ok": false, "error": "..."}
A request with {"ping": true} gets {"ok": true, "ready": true} once models are loaded.
"""
import sys
import json

import argostranslate.package as pkg
import argostranslate.translate as tr

# Map Whisper's detected language codes (ISO 639-1, sometimes with region) to
# Argos Translate codes. Argos mostly uses plain ISO 639-1 codes too.
LANG_ALIAS = {
    "zh": "zh", "zt": "zh",
}


def normalize(code: str) -> str:
    code = (code or "en").lower().split("-")[0]
    return LANG_ALIAS.get(code, code)


def ensure_installed(languages: set[str]) -> None:
    """Install any missing pl<->X language pairs we might need, pivoting through English."""
    installed = pkg.get_installed_packages()
    have = {(p.from_code, p.to_code) for p in installed}
    needed = set()
    for lang in languages:
        if lang == "en":
            continue
        needed.add((lang, "en"))
        needed.add(("en", lang))

    missing = {pair for pair in needed if pair not in have}
    if not missing:
        return
    pkg.update_package_index()
    available = pkg.get_available_packages()
    for f, t in missing:
        candidate = next((p for p in available if p.from_code == f and p.to_code == t), None)
        if candidate:
            path = candidate.download()
            pkg.install_from_path(path)


def translate(text: str, src: str, dst: str) -> str:
    src = normalize(src)
    dst = normalize(dst)
    if src == dst or not text.strip():
        return text
    ensure_installed({src, dst})
    return tr.translate(text, src, dst)


def main() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            if req.get("ping"):
                resp = {"ok": True, "ready": True}
            else:
                result = translate(req.get("text", ""), req.get("from", "en"), req.get("to", "en"))
                resp = {"ok": True, "text": result}
        except Exception as ex:  # noqa: BLE001
            resp = {"ok": False, "error": str(ex)}
        sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
