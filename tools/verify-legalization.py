#!/usr/bin/env python3
"""Prueba completa de la auto-legalizacion: para cada Pokemon de pokemons/

    1. el endpoint de CHECK dice que esta mal (motivos)
    2. el endpoint de LEGALIZE devuelve un Pokemon y la lista de cambios
    3. el Pokemon devuelto se vuelve a pasar por el CHECK

Criterio: el Pokemon devuelto no puede dejar NINGUN motivo del check sin
resolver, es decir su reporte tiene que quedar en ["Legal!"].

    python tools/verify-legalization.py [base_url]
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080"


def post(url, path, headers=()):
    """POST multipart con el Pokemon; devuelve el JSON de la respuesta.

    Reintenta ante 429: la API limita a 100 peticiones por minuto y por IP
    (httprate.LimitByIP) y el criterio hace mas de 100 sobre los fixtures.
    """
    for _ in range(6):
        with tempfile.TemporaryDirectory() as tmp:
            ascii_path = os.path.join(tmp, "pokemon.bin")
            shutil.copy(path, ascii_path)
            cmd = ["curl", "-s", "-w", "\n%{http_code}", "-X", "POST"]
            for header in headers:
                cmd += ["-H", header]
            cmd += ["-F", f"pkmn=@{ascii_path}", url]
            out = subprocess.run(cmd, capture_output=True).stdout.decode("utf-8", "replace")
        body, _, code = out.rpartition("\n")
        try:
            payload = json.loads(body)
        except Exception:
            payload = None
        try:
            status = int(code)
        except ValueError:
            status = 0
        if status == 429:
            time.sleep(3)
            continue
        return status, payload
    return status, payload


def write_temp(data):
    handle = tempfile.NamedTemporaryFile(suffix=".bin", delete=False)
    handle.write(data)
    handle.close()
    return handle.name


def main():
    paths = sorted(os.path.join("pokemons", n) for n in os.listdir("pokemons")
                   if n.endswith(".pk9") or n.endswith(".pk7"))
    if not paths:
        print("no hay Pokemon en pokemons/")
        return 1

    failures = 0
    print("%-42s | %-28s | %-30s | %s" % ("archivo", "check (motivos)", "cambios", "check del resultado"))
    print("-" * 150)

    for path in paths:
        name = os.path.basename(path)
        generation = "7" if path.endswith(".pk7") else "9"
        version = "ORAS" if path.endswith(".pk7") else "SV"
        headers = (f"generation: {generation}", f"version: {version}")

        _, before = post(f"{BASE}/api/v2/pksm/legality", path, (f"generation: {generation}",))
        reasons_before = [r.strip() for r in ((before or {}).get("report") or [])]
        problems_before = [r for r in reasons_before if r and r != "Legal!"]

        status, legalize = post(f"{BASE}/api/v2/pksm/legalize", path, headers)

        if status == 400 and (legalize or {}).get("error", "").startswith("this pokemon is already legal"):
            print("%-42s | %-28s | %-30s | %s" % (name[:42],
                                                  "ya era legal", "-", "OK (nada que legalizar)"))
            continue

        if status != 200 or not (legalize or {}).get("pokemon"):
            print("%-42s | %-28s | %-30s | FALLO (HTTP %s: %s)" % (
                name[:42], ("%d motivos" % len(problems_before)) if problems_before else "legal",
                "-", status, str((legalize or {}).get("error"))[:40]))
            failures += 1
            continue

        changes = legalize.get("changes") or []
        tmp = write_temp(__import__("base64").b64decode(legalize["pokemon"]))
        _, after = post(f"{BASE}/api/v2/pksm/legality", tmp, (f"generation: {generation}",))
        os.unlink(tmp)
        reasons_after = [r.strip() for r in ((after or {}).get("report") or [])]
        problems_after = [r for r in reasons_after if r and r != "Legal!"]

        ok = not problems_after
        if not ok:
            failures += 1

        print("%-42s | %-28s | %-30s | %s" % (
            name[:42],
            ("%d: %s" % (len(problems_before), problems_before[0][:24])) if problems_before else "legal",
            ("%d cambios" % len(changes)) if changes else "0 cambios",
            "OK (reporte vacio)" if ok else "FALLO: %s" % problems_after[:2]))
        for change in changes:
            print("        cambio: %s" % change)

    print()
    print("=== resumen: %d Pokemon con algun motivo sin resolver ===" % failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
