#!/usr/bin/env python3
"""Regenera tools/verify-baseline.txt a partir del estado real de la API.

    python tools/update-baseline.py [base_url]

Para cada Pokemon de pokemons/ se toma:
  - legal      : lo que dice el endpoint de check
  - razones    : numero de motivos del reporte del check
  - legalize   : yes (se legaliza y el check del resultado queda vacio),
                 already (ya era legal) o no (no se puede)

El script IMPRIME lo que cambia para que se revise a mano antes de
aceptarlo; el fichero de la linea base solo se reescribe si se le pasa
--write.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
BASE = ARGS[0] if ARGS else "http://localhost:8080"
BASELINE = "tools/verify-baseline.txt"
WRITE = "--write" in sys.argv


def post(url, path, headers=()):
    """POST multipart con el Pokemon; devuelve (status, payload).

    Reintenta si la respuesta viene vacia: una llamada fallida no puede
    confundirse con un Pokemon sin motivos, y el numero de motivos es
    justamente lo que se fija en la linea base. Tambien reintenta ante un
    429: la API limita a 100 peticiones por minuto y por IP, y con los 59
    fixtures la tanda se pasa de ese limite.
    """
    last = ("", None)
    for attempt in range(6):
        with tempfile.TemporaryDirectory() as tmp:
            ascii_path = os.path.join(tmp, "pokemon.bin")
            shutil.copy(path, ascii_path)
            cmd = ["curl", "-s", "--max-time", "120", "-w", "\n%{http_code}", "-X", "POST"]
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
        if payload is not None:
            return status, payload
        last = (body[:120], None)
        time.sleep(1)
    EMPTY.append((url, path, last[0]))
    return 0, None


EMPTY = []


def main():
    paths = sorted(os.path.join("pokemons", n) for n in os.listdir("pokemons")
                   if n.endswith(".pk9") or n.endswith(".pk7"))
    current = {}
    if os.path.exists(BASELINE):
        for line in io_lines(BASELINE):
            if "|" in line and not line.startswith("#"):
                parts = line.split("|")
                current[parts[0]] = "|".join(parts[1:])

    rows = []
    changes = 0
    for path in paths:
        name = os.path.basename(path)
        generation = "7" if path.endswith(".pk7") else "9"
        version = "ORAS" if path.endswith(".pk7") else "SV"

        _, check = post(f"{BASE}/api/v2/pksm/legality", path, (f"generation: {generation}",))
        reasons = [r.strip() for r in ((check or {}).get("report") or [])]
        problems = [r for r in reasons if r and r != "Legal!"]
        legal = "true" if not problems else "false"

        status, legalize = post(f"{BASE}/api/v2/pksm/legalize", path,
                                (f"generation: {generation}", f"version: {version}"))
        if status == 400 and (legalize or {}).get("error", "").startswith("this pokemon is already legal"):
            outcome = "already"
        elif status == 200 and (legalize or {}).get("pokemon"):
            outcome = "yes"
        else:
            outcome = "no"

        value = "%s|%d|%s" % (legal, len(reasons), outcome)
        rows.append("%s|%s" % (name, value))
        if current.get(name) != value:
            changes += 1
            print("cambio  %-44s %s -> %s" % (name[:44], current.get(name, "(nuevo)"), value))

    print()
    print("Pokemon: %d | entradas que cambian: %d" % (len(rows), changes))
    if EMPTY:
        print("AVISO: %d peticiones devolvieron una respuesta vacia (se reintentaron 3 veces); "
              "sus entradas NO son fiables:" % len(EMPTY))
        for url, path, raw in EMPTY[:5]:
            print("   %s %s -> %r" % (url, os.path.basename(path), raw))
        print("   no se escribe la linea base con datos incompletos")
        return 1
    if WRITE:
        header = [
            "# Linea base de los Pokemon de pokemons/ frente al motor ALM 26.10.18.",
            "# Formato: nombre|legal|numero de razones|legalize",
            "#   legal    -> true | false",
            "#   razones  -> numero de motivos del reporte de legalidad",
            "#   legalize -> yes  (ALM devuelve un Pokemon legal)",
            "#             no   (ALM no puede y el reporte lo explica)",
            "#             already (el Pokemon ya es legal; la API responde 400)",
            "# Anadir un Pokemon nuevo obliga a decidir su linea base aqui: el arnes falla",
            "# mientras no exista una entrada para el.",
        ]
        with open(BASELINE, "w", encoding="utf-8") as handle:
            handle.write("\n".join(header + rows) + "\n")
        print("linea base escrita en %s" % BASELINE)
    else:
        print("(no se escribe nada: pasa --write para escribirla)")
    return 0


def io_lines(path):
    with open(path, encoding="utf-8") as handle:
        return [line.strip() for line in handle if line.strip()]


if __name__ == "__main__":
    sys.exit(main())
