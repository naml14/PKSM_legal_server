#!/usr/bin/env python3
"""Muestra el veredicto de cada Pokemon de pokemons/ contra la API en marcha.

    python tools/pokemon-report.py [base_url]

Los nombres con el caracter * (estrella) no los puede abrir el curl nativo de
Windows, asi que cada archivo se copia antes a un nombre ASCII temporal.
"""
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080"


def post(url, path, headers=()):
    """POST multipart con el Pokemon; devuelve (json, texto crudo)."""
    with tempfile.TemporaryDirectory() as tmp:
        ascii_path = os.path.join(tmp, "pokemon.bin")
        shutil.copy(path, ascii_path)
        cmd = ["curl", "-s", "-X", "POST"]
        for header in headers:
            cmd += ["-H", header]
        cmd += ["-F", f"pkmn=@{ascii_path}", url]
        raw = subprocess.run(cmd, capture_output=True).stdout.decode("utf-8", "replace")
    try:
        return json.loads(raw), raw
    except Exception:
        return None, raw


def summary(payload):
    if payload is None:
        return "SIN RESPUESTA"
    if "legal" in payload:
        return "legal=%-5s razones=%d" % (payload.get("legal"), len(payload.get("report") or []))
    return json.dumps(payload, ensure_ascii=False)[:120]


def main():
    paths = sorted(glob.glob("pokemons/*.pk9")) + sorted(glob.glob("pokemons/*.pk7"))
    if not paths:
        print("no hay Pokemon en pokemons/")
        return 1

    print("%-42s | %-26s | %-26s | %s" % ("archivo", "legacy /legality/check", "v2 /pksm/legality", "v2 /pksm/legalize"))
    print("-" * 140)
    for path in paths:
        generation = "7" if path.endswith(".pk7") else "9"
        version = "ORAS" if path.endswith(".pk7") else "SV"

        legacy, legacy_raw = post(f"{BASE}/api/legality/check", path)
        v2, v2_raw = post(f"{BASE}/api/v2/pksm/legality", path, (f"generation: {generation}",))
        legalize, legalize_raw = post(
            f"{BASE}/api/v2/pksm/legalize", path, (f"generation: {generation}", f"version: {version}")
        )

        if legalize is None:
            outcome = "SIN RESPUESTA: " + legalize_raw[:80]
        elif "pokemon" in legalize and legalize.get("pokemon"):
            outcome = "pokemon=SI (legalizado)"
        elif "error" in legalize:
            outcome = "HTTP error: " + str(legality_error(legalize))[:60]
        else:
            reason = next(
                (r for r in (legalize.get("report") or []) if "Auto-legalization failed" in r),
                "sin motivo explicito",
            )
            outcome = "pokemon=no -> " + reason.strip()[:70]

        print("%-42s | %-26s | %-26s | %s" % (
            os.path.basename(path)[:42],
            summary(legacy),
            summary(v2),
            outcome,
        ))

    return 0


def legality_error(payload):
    return payload.get("error")


if __name__ == "__main__":
    sys.exit(main())
