#!/usr/bin/env bash
# Runs the Worker with `wrangler dev` on a fresh local D1 and checks what it accepts, rejects, names, caches and serves.
# Usage: npm test (from stats/). Exits non-zero if any check fails.
set -euo pipefail
cd "$(dirname "$0")/.."

PORT=${PORT:-8799}
B="http://127.0.0.1:$PORT"
STATE=$(mktemp -d)
WRANGLER=node_modules/.bin/wrangler
fails=0

"$WRANGLER" d1 execute mapvote-stats --local --persist-to "$STATE" --file schema.sql >/dev/null
# Naming cases: a real map on 3 days, a real map in a 1-day burst, a link-like name on 3 days, and a base-game map offered twice.
read -r d0 d1 d2 < <(node -e 'const d = (n) => new Date(Date.now() - n * 864e5).toISOString().slice(0, 10); console.log(d(0), d(1), d(2))')
"$WRANGLER" d1 execute mapvote-stats --local --persist-to "$STATE" --command "INSERT INTO offers VALUES
  ('$d0','Level - Manor',10,5),('$d1','Level - Manor',10,5),('$d2','Level - Manor',10,5),
  ('$d0','Level - Burst',40,40),
  ('$d0','Level - Desert',2,0),
  ('$d0','visit evil.com',10,1),('$d1','visit evil.com',10,1),('$d2','visit evil.com',10,1);" >/dev/null

"$WRANGLER" dev --port "$PORT" --ip 127.0.0.1 --persist-to "$STATE" --show-interactive-dev-session=false >"$STATE/dev.log" 2>&1 &
dev=$!
trap 'kill "$dev" 2>/dev/null || true; wait "$dev" 2>/dev/null || true; rm -rf "$STATE"' EXIT
# Poll the dashboard, not /v1/stats, so the stats cache starts empty.
for _ in $(seq 60); do curl -sf -o /dev/null "$B/" && break; sleep 1; done
curl -sf -o /dev/null "$B/" || { cat "$STATE/dev.log"; echo "wrangler dev didn't start"; exit 1; }

expect() { # name expected actual
  if [[ "$2" == "$3" ]]; then echo "PASS $1 ($3)"; else echo "FAIL $1: expected $2, got $3"; fails=$((fails + 1)); fi
}
post() { curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/ping" -H 'content-type: application/json' "$@"; }
field() { node -e 'let s = ""; process.stdin.on("data", (d) => (s += d)).on("end", () => console.log(eval(process.argv[1])(JSON.parse(s))))' "$1"; }
offers() {
  local o="" i
  for ((i = 1; i <= $1; i++)); do o+="\"Map $i\":0,"; done
  echo "{\"t\":\"vote\",\"v\":\"1.3.0\",\"mode\":\"Lottery\",\"players\":4,\"context\":\"lobby\",\"how\":\"none\",\"winner\":\"Map 1\",\"offers\":{${o%,}}}"
}

# POSTs 1-7. Every POST counts towards the 10-a-minute limit, whatever it returns.
expect heartbeat 204 "$(post -d '{"t":"hb","v":"1.3.0","mode":"Lottery","players":4,"mapsPerVote":3,"recentSkip":1}')"
expect vote 204 "$(post -d '{"t":"vote","v":"1.3.0","mode":"Lottery","players":4,"context":"lobby","how":"vote","winner":"Level - Manor","offers":{"Level - Manor":3,"__random__":1}}')"
expect inherited-winner 400 "$(post -d '{"t":"vote","v":"1.3.0","mode":"Lottery","players":4,"context":"lobby","how":"vote","winner":"toString","offers":{"Level - Manor":0}}')"
expect 100-offers 204 "$(post -d "$(offers 100)")"
expect 101-offers 400 "$(post -d "$(offers 101)")"
head -c 9000 /dev/zero | tr '\0' x >"$STATE/big.txt"
expect big-with-length 413 "$(post --data-binary @"$STATE/big.txt")"
expect big-chunked 413 "$(post -H 'transfer-encoding: chunked' --data-binary @"$STATE/big.txt")"

stats=$(curl -s "$B/v1/stats")
expect named-maps 'Level - Manor|Level - Desert|OTHER' "$(field '(s) => s.maps.map((m) => m.map ?? "OTHER").join("|")' <<<"$stats")"
# Level art is served from public/ for each base-game map the dashboard names.
for m in manor arctic museum desert wizard; do
  expect "art-$m" '200 image/webp' "$(curl -s -o /dev/null -w '%{http_code} %{content_type}' "$B/maps/$m.webp")"
done
expect cached-despite-query "$(field '(s) => s.generated' <<<"$stats")" "$(curl -s "$B/v1/stats?bust=$RANDOM" | field '(s) => s.generated')"

# POSTs 8-12
codes=""
for _ in 1 2 3 4 5; do codes+="$(post -d '{}') "; done
expect rate-limit '400 400 400 429 429' "${codes% }"

echo "$fails failed"
exit $((fails > 0))
