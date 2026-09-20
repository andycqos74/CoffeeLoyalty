#!/bin/bash
# Phase 1 smoke test. Every page must return 200 AND contain no unresolved {{tokens}}.
B=${1:-http://127.0.0.1:5199}
PIN='X-Pin: 9999'
fail=0
pass() { echo "  ok   $1"; }
bad()  { echo "  FAIL $1"; fail=$((fail+1)); }

check_page() {
  local path=$1 expect=$2
  local body code
  body=$(curl -s --noproxy '*' -w '\n%{http_code}' "$B$path")
  code=$(tail -1 <<< "$body")
  body=$(sed '$d' <<< "$body")
  [ "$code" = "$expect" ] || { bad "$path status $code (want $expect)"; return; }
  # Redirects legitimately have no body.
  if [ "$expect" != "302" ]; then
    [ -n "$body" ] || { bad "$path empty body"; return; }
  fi
  local left
  left=$(grep -o '{{[^}]*}}' <<< "$body" | sort -u | tr '\n' ' ')
  [ -z "$left" ] || { bad "$path unresolved tokens: $left"; return; }
  pass "$path ($code, $(wc -c <<< "$body") bytes, no stray tokens)"
}

echo "== pages =="
for p in / /join.html /admin /admin/ /shop /shop/ /theme.css /manifest.json /shop/manifest.json /sw.js /shop/sw.js; do
  [ "$p" = "/" ] && check_page "$p" 302 || check_page "$p" 200
done

echo "== brand assets =="
for a in logo hero favicon icon192 icon512 iconMaskable walletLogo; do
  code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' "$B/brand/$a")
  [ "$code" = "200" ] && pass "/brand/$a" || bad "/brand/$a status $code"
done
code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' "$B/brand/nonsense")
[ "$code" = "404" ] && pass "/brand/nonsense -> 404" || bad "/brand/nonsense status $code"

echo "== auth =="
code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' "$B/api/admin/branding")
[ "$code" = "401" ] && pass "branding API requires a PIN" || bad "branding API unauthenticated status $code"
code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -H "$PIN" "$B/api/admin/branding")
[ "$code" = "200" ] && pass "branding API with PIN" || bad "branding API with PIN status $code"

echo "== customer journey =="
tok=$(curl -s --noproxy '*' -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Smoke Test","email":"s@example.com","marketingOk":true}' "$B/api/join" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
[ -n "$tok" ] && pass "join -> token $tok" || { bad "join failed"; echo "FAILURES: $fail"; exit 1; }
check_page "/card/$tok" 200
check_page "/api/manifest/$tok" 200
# Expected points come from the client's own rate, not a fixed number.
rate=$(curl -s --noproxy '*' "$B/api/customer/$tok" | python3 -c 'import json,sys;print(json.load(sys.stdin)["pointsPerPound"])')
want=$((350 * rate / 100))
curl -s --noproxy '*' -X POST -H 'X-Pin: 1234' -H 'Content-Type: application/json' \
  -d "{\"token\":\"$tok\",\"amountPence\":350}" "$B/api/shop/earn" > /tmp/earn.json
pts=$(python3 -c 'import json;print(json.load(open("/tmp/earn.json")).get("pointsEarned"))')
[ "$pts" = "$want" ] && pass "earn 3.50 at $rate/unit -> $pts points" || bad "earn gave $pts, expected $want"
desc=$(curl -s --noproxy '*' "$B/api/customer/$tok" | python3 -c 'import json,sys;print(json.load(sys.stdin)["activity"][0]["description"])')
pass "transaction described as: $desc"

echo
[ $fail -eq 0 ] && echo "ALL PASSED" || echo "$fail FAILURE(S)"
exit $fail
