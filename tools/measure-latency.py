#!/usr/bin/env python3
"""Mide el tiempo de respuesta de la API contra un servidor real.

    python tools/measure-latency.py [base_url]

Mide cada endpoint con una conexion persistente (para que el handshake TLS no
contamine la medida) y separa el tiempo de red del tiempo de servidor: el RTT
se mide con la respuesta estatica de /api/general/kuma-ping, que no toca el
motor, y el tiempo de servidor es la mediana de cada endpoint menos ese RTT.

    python tools/measure-latency.py http://localhost:8080
    python tools/measure-latency.py https://electrondevnaml.dev/
"""
import http.client
import json
import os
import ssl
import statistics
import sys
import time
import urllib.parse
import uuid

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080"
REPEATS = int(os.environ.get("LATENCY_REPEATS", "6"))

parsed = urllib.parse.urlparse(BASE)
SECURE = parsed.scheme == "https"
HOST = parsed.hostname or "localhost"
PORT = parsed.port or (443 if SECURE else 80)
PREFIX = parsed.path.rstrip("/")


def connect():
    if SECURE:
        connection = http.client.HTTPSConnection(HOST, PORT, context=ssl.create_default_context(), timeout=120)
    else:
        connection = http.client.HTTPConnection(HOST, PORT, timeout=120)
    connection.connect()
    return connection


def multipart(path_file):
    """El cuerpo multipart y su Content-Type, con el MISMO boundary."""
    boundary = uuid.uuid4().hex
    with open(path_file, "rb") as handle:
        data = handle.read()
    body = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="pkmn"; filename="pokemon.bin"\r\n'
        "Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + data + f"\r\n--{boundary}--\r\n".encode()
    return body, f"multipart/form-data; boundary={boundary}"


def measure(connection, method, path, fixture=None, headers=None, repeats=REPEATS):
    times, statuses = [], []
    for _ in range(repeats):
        request_headers = dict(headers or {})
        body = None
        if fixture is not None:
            body, content_type = multipart(fixture)
            request_headers["Content-Type"] = content_type

        started = time.perf_counter()
        connection.request(method, PREFIX + path, body=body, headers=request_headers)
        response = connection.getresponse()
        response.read()
        times.append((time.perf_counter() - started) * 1000)
        statuses.append(response.status)
    return times, statuses[-1]


def report(label, times, status, rtt):
    median = statistics.median(times)
    server = median - rtt
    print(
        "  %-34s mediana %6.0f ms  min %6.0f  max %6.0f  |  servidor %6.0f ms  (HTTP %s)"
        % (label, median, min(times), max(times), server, status)
    )
    return median


def main():
    fixtures = sorted(
        os.path.join("pokemons", name)
        for name in os.listdir("pokemons")
        if name.endswith(".pk7")
    ) if os.path.isdir("pokemons") else []

    latias = next((f for f in fixtures if "Latias" in f), None)
    bulbasaur = next((f for f in fixtures if "Bulbasaur" in f), None)

    connection = connect()
    print("=== %s ===" % BASE)

    ping, status = measure(connection, "GET", "/api/general/kuma-ping", repeats=3)
    rtt = statistics.median(ping)
    print("  RTT (respuesta estatica)                : %6.0f ms" % rtt)

    times, status = measure(connection, "GET", "/api/general/kuma-ping")
    report("GET /api/general/kuma-ping", times, status, rtt)

    times, status = measure(connection, "GET", "/api/general/ping", repeats=3)
    report("GET /api/general/ping (motor)", times, status, rtt)

    if latias is not None:
        times, status = measure(connection, "POST", "/api/v2/pksm/legality", latias, {"generation": "7"})
        report("POST /api/v2/pksm/legality", times, status, rtt)

        times, status = measure(connection, "POST", "/api/v2/pksm/legalize", latias, {"generation": "7", "version": "ORAS"})
        report("POST /api/v2/pksm/legalize", times, status, rtt)

    if bulbasaur is not None:
        times, status = measure(connection, "POST", "/api/v2/pksm/legalize", bulbasaur, {"generation": "7", "version": "ORAS"})
        report("POST /api/v2/pksm/legalize (Bulbasaur)", times, status, rtt)

    connection.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
