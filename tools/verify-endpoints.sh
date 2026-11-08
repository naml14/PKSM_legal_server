#!/usr/bin/env bash
# Verificacion de endpoints del CoreAPI contra los Pokemon reales de pokemons/.
#
#   bash tools/verify-endpoints.sh [base_url]
#
# Los Pokemon de pokemons/ son los fixtures: cada uno se copia a un nombre ASCII
# (los nombres con el caracter * no los abre el curl nativo de Windows) y se
# compara con la linea base de tools/verify-baseline.txt (veredicto, numero de
# motivos y si la auto-legalizacion puede).
#
# Salida: una linea PASS/FAIL por comprobacion y un resumen; exit 1 si algo falla.
set -uo pipefail

BASE="${1:-http://localhost:8080}"
WORK=".verify-tmp"
BASELINE="tools/verify-baseline.txt"
PASS=0
FAIL=0
mkdir -p "$WORK"
trap 'rm -rf "$WORK"' EXIT

ok()  { PASS=$((PASS + 1)); printf 'PASS  %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf 'FAIL  %s   (esperado: %s | obtenido: %s)\n' "$1" "$2" "$3"; }

# La API limita a 100 peticiones por minuto y por IP (httprate.LimitByIP en http.go),
# asi que una tanda larga recibe algunos 429: se reintenta con una pausa.
call() {
  local code
  for _ in 1 2 3 4 5 6; do
    code=$(curl -s -o "$WORK/body" -w '%{http_code}' "$@")
    [ "$code" = "429" ] || break
    sleep 3
  done
  printf '%s' "$code"
}

# status <descripcion> <esperado> <curl args...>
status() {
  local desc="$1" want="$2"; shift 2
  local code
  code=$(call "$@")
  if [ "$code" = "$want" ]; then ok "$desc (HTTP $code)"
  else bad "$desc" "HTTP $want" "HTTP $code: $(head -c 120 "$WORK/body" | tr -d '\r\n')"; fi
}

# json_field <campo> <fichero>  -> valor (o "nojson")
json_field() {
  python -c "
import json,sys
try: d=json.load(open('$2',encoding='utf-8',errors='replace'))
except Exception: print('nojson'); raise SystemExit
v=d.get('$1')
if isinstance(v, bool): print('true' if v else 'false')
elif v is None: print('null')
else: print(v)
" 2>/dev/null
}

# report_len <fichero> -> numero de motivos del reporte
report_len() {
  python -c "
import json,sys
try: d=json.load(open('$1',encoding='utf-8',errors='replace'))
except Exception: print('-1'); raise SystemExit
print(len(d.get('report') or []))
" 2>/dev/null
}

echo "=== Verificacion de endpoints contra $BASE ==="
echo

echo "--- rutas GET ---"
status "GET /api/general/kuma-ping" 200 "$BASE/api/general/kuma-ping"
status "GET /api/general/ping" 200 "$BASE/api/general/ping"
status "GET /.well-known/security.txt" 200 "$BASE/.well-known/security.txt"
status "GET /debug/pprof/ (solo con --debug)" 301 "$BASE/debug/pprof/"

echo
echo "--- metodo y ruta incorrectos ---"
status "GET /api/info -> 405" 405 "$BASE/api/info"
status "GET /api/legality/check -> 405" 405 "$BASE/api/legality/check"

echo
echo "--- S3: pkmn ausente / cuerpo no multipart / cuerpo grande ---"
status "POST /api/info sin pkmn -> 400" 400 -X POST -F "generation=Gen8" "$BASE/api/info"
status "POST /api/legality/check sin pkmn -> 400" 400 -X POST -F "generation=Gen8" "$BASE/api/legality/check"
status "POST /api/legality/legalize sin pkmn -> 400" 400 -X POST -F "generation=Gen8" "$BASE/api/legality/legalize"
status "POST /api/info sin multipart -> 400" 400 -X POST -H 'Content-Type: application/json' -d '{}' "$BASE/api/info"
head -c 2000 /dev/urandom > "$WORK/big.bin"
status "POST /api/info cuerpo > 1KB -> 413" 413 -X POST -F "pkmn=@$WORK/big.bin" "$BASE/api/info"
printf 'no soy un pokemon\n' > "$WORK/text.txt"
status "POST /api/info contenido no-pokemon -> 400" 400 -X POST -F "pkmn=@$WORK/text.txt" "$BASE/api/info"

echo
echo "--- Pokemon reales: veredicto, ruta legacy y ruta v2 ---"
i=0
for f in pokemons/*.pk9 pokemons/*.pk7; do
  [ -e "$f" ] || continue
  i=$((i + 1))
  name=$(basename "$f")
  ext="${f##*.}"
  cp "$f" "$WORK/p$i.$ext"

  want_legal=$(awk -F'|' -v n="$name" '$1==n{print $2}' "$BASELINE")
  want_reasons=$(awk -F'|' -v n="$name" '$1==n{print $3}' "$BASELINE")
  want_legalize=$(awk -F'|' -v n="$name" '$1==n{print $4}' "$BASELINE")

  if [ -z "$want_legal" ]; then
    bad "linea base de '$name'" "una entrada en $BASELINE" "sin linea base: el arnes no puede juzgarla"
    continue
  fi

  generation=9
  version=SV
  if [ "$ext" = "pk7" ]; then generation=7; version=ORAS; fi

  # Ruta legacy
  code=$(call -X POST -F "pkmn=@$WORK/p$i.$ext" "$BASE/api/legality/check")
  got_legal=$(json_field legal "$WORK/body")
  got_reasons=$(report_len "$WORK/body")
  if [ "$code" = "200" ] && [ "$got_legal" = "$want_legal" ] && [ "$got_reasons" = "$want_reasons" ]; then
    ok "legacy /legality/check $name (legal=$got_legal razones=$got_reasons)"
  else
    bad "legacy /legality/check $name" "HTTP 200 legal=$want_legal razones=$want_reasons" "HTTP $code legal=$got_legal razones=$got_reasons"
  fi

  # Ruta v2 (la que usa el 2ds)
  code=$(call -X POST -H "generation: $generation" -F "pkmn=@$WORK/p$i.$ext" "$BASE/api/v2/pksm/legality")
  got_legal=$(json_field legal "$WORK/body")
  got_reasons=$(report_len "$WORK/body")
  if [ "$code" = "200" ] && [ "$got_legal" = "$want_legal" ] && [ "$got_reasons" = "$want_reasons" ]; then
    ok "v2 /pksm/legality $name (generation: $generation, legal=$got_legal razones=$got_reasons)"
  else
    bad "v2 /pksm/legality $name" "HTTP 200 legal=$want_legal razones=$want_reasons" "HTTP $code legal=$got_legal razones=$got_reasons"
  fi

  # Auto-legalizacion
  code=$(call -X POST -H "generation: $generation" -H "version: $version" -F "pkmn=@$WORK/p$i.$ext" "$BASE/api/v2/pksm/legalize")
  got_pokemon=$(json_field pokemon "$WORK/body")
  case "$want_legalize" in
    yes)
      if [ "$code" = "200" ] && [ "$got_pokemon" != "null" ] && [ "$got_pokemon" != "nojson" ]; then
        ok "v2 /pksm/legalize $name -> 200 con Pokemon legalizado"
      else
        bad "v2 /pksm/legalize $name" "HTTP 200 con pokemon no nulo" "HTTP $code pokemon=$got_pokemon"
      fi ;;
    already)
      if [ "$code" = "400" ] && grep -q "this pokemon is already legal" "$WORK/body"; then
        ok "v2 /pksm/legalize $name -> 400 'this pokemon is already legal!'"
      else
        bad "v2 /pksm/legalize $name" "HTTP 400 con 'this pokemon is already legal'" "HTTP $code: $(head -c 120 "$WORK/body" | tr -d '\r\n')"
      fi ;;
    no)
      if [ "$code" = "200" ] && grep -qi "auto-legalization failed" "$WORK/body"; then
        ok "v2 /pksm/legalize $name -> 200 explicando que ALM no pudo legalizarlo"
      else
        bad "v2 /pksm/legalize $name" "HTTP 200 con 'Auto-legalization failed' en el report" "HTTP $code: $(head -c 120 "$WORK/body" | tr -d '\r\n')"
      fi ;;
  esac
done

echo
echo "--- S4: forced_generation ya no devuelve texto de ayuda ---"
code=$(call -X POST -F "pkmn=@$WORK/p1.pk9" -F "forced_generation=Gen9" "$BASE/api/legality/legalize")
if [ "$code" = "200" ] && [ "$(json_field legal "$WORK/body")" != "nojson" ]; then
  ok "POST /api/legality/legalize forced_generation=Gen9 -> 200 JSON"
else
  bad "POST /api/legality/legalize forced_generation=Gen9" "HTTP 200 con JSON" "HTTP $code: $(head -c 120 "$WORK/body" | tr -d '\r\n')"
fi

echo
echo "--- S1/S2: contrato de la ruta v2 ---"
status "POST /api/v2/pksm/legality sin cabecera generation -> 400" 400 \
  -X POST -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legality"
status "POST /api/v2/pksm/legalize sin cabecera version -> 400" 400 \
  -X POST -H "generation: 9" -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legalize"
status "v2 legality generation: Gen8b -> 200" 200 -X POST -H "generation: Gen8b" -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legality"
status "v2 legality generation: gen9 (minusculas) -> 200" 200 -X POST -H "generation: gen9" -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legality"
status "v2 legality generation: 9 (numero) -> 200" 200 -X POST -H "generation: 9" -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legality"
status "v2 legality generation: Gen42 (inventada) -> 400" 400 -X POST -H "generation: Gen42" -F "pkmn=@$WORK/p1.pk9" "$BASE/api/v2/pksm/legality"

echo
echo "=== resumen: $PASS PASS / $FAIL FAIL ==="
[ "$FAIL" -eq 0 ] || exit 1
