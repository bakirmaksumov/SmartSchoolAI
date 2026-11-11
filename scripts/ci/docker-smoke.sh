#!/usr/bin/env bash
# Black-box check of a running SmartSchoolAI container, used by .github/workflows/docker-ci.yml.
#
#   BASE_URL      where the app listens (default http://localhost:8080)
#   RECREATE_CMD  command that destroys and recreates the app container while KEEPING its
#                 volumes; required. The script verifies that data written before it survives.
#
# Flow: wait for /health/ready -> login page renders -> register a teacher (writes to SQLite)
# -> sign in -> recreate the container -> the old session cookie still works (Data Protection
# keys persisted) and the account can sign in again (database persisted).
set -uo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
: "${RECREATE_CMD:?RECREATE_CMD is required}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

email="smoke.$(date +%s)@example.test"
password="Smoke-Pass-$(date +%s)"

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok: $*"; }

wait_ready() {
  local attempts=60 code
  for ((i = 1; i <= attempts; i++)); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/health/ready" || true)"
    [[ "$code" == "200" ]] && return 0
    sleep 2
  done
  return 1
}

# Extracts the antiforgery token from a form page.
token_from() {
  sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' "$1" | head -n 1
}

# Prints the HTTP status of a form POST and leaves cookies in the given jar.
post_form() { # jar path token field=value...
  local jar="$1" path="$2" token="$3"; shift 3
  local args=()
  for kv in "$@"; do args+=(--data-urlencode "$kv"); done
  curl -s -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" \
    --data-urlencode "__RequestVerificationToken=$token" "${args[@]}" "$BASE_URL$path"
}

sign_in() { # jar -> echoes status
  local jar="$1"
  curl -s -o "$work/login.html" -b "$jar" -c "$jar" "$BASE_URL/Auth/Login"
  local token; token="$(token_from "$work/login.html")"
  [[ -n "$token" ]] || fail "no antiforgery token on the login page"
  post_form "$jar" /Auth/Login "$token" "email=$email" "password=$password" "rememberMe=true"
}

# The auth cookie is Secure (Program.cs declares every request HTTPS in production) and curl's
# jar would not replay it over plain http, so read its value out of the jar and send it by hand.
auth_cookie() {
  awk -F'\t' '$6 == ".AspNetCore.Cookies" { print $6 "=" $7 }' "$1" | head -n 1
}

# 1. Ready ---------------------------------------------------------------------------------
wait_ready || fail "/health/ready did not return 200 within 120s"
pass "/health/ready returned 200"

# 2. Login page ----------------------------------------------------------------------------
code="$(curl -s -o "$work/login.html" -w '%{http_code}' "$BASE_URL/Auth/Login")"
[[ "$code" == "200" ]] || fail "/Auth/Login returned $code (a 3xx would mean an HTTPS redirect loop)"
grep -q 'name="email"' "$work/login.html" || fail "login page has no email field"
[[ -n "$(token_from "$work/login.html")" ]] || fail "login page has no antiforgery token"
pass "login page renders over plain HTTP with a form and antiforgery token"

# 3. Write a record (register a teacher) ---------------------------------------------------
jar="$work/jar.txt"
curl -s -o "$work/register.html" -b "$jar" -c "$jar" "$BASE_URL/Auth/Register"
token="$(token_from "$work/register.html")"
[[ -n "$token" ]] || fail "no antiforgery token on the register page"
code="$(post_form "$jar" /Auth/Register "$token" \
  "fullName=Smoke Teacher" "email=$email" "password=$password" "confirmPassword=$password")"
[[ "$code" == "302" ]] || fail "registration returned $code, expected 302"
pass "teacher registered (row written to SQLite)"

# 4. Sign in and prove the session works ---------------------------------------------------
code="$(sign_in "$jar")"
[[ "$code" == "302" ]] || fail "sign-in returned $code, expected 302"
cookie="$(auth_cookie "$jar")"
[[ -n "$cookie" ]] || fail "sign-in did not set the auth cookie"
code="$(curl -s -o /dev/null -w '%{http_code}' -H "Cookie: $cookie" "$BASE_URL/Profile")"
[[ "$code" == "200" ]] || fail "signed-in /Profile returned $code, expected 200"
pass "signed in; protected page returns 200"
code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/Profile")"
[[ "$code" != "200" ]] || fail "/Profile returned 200 without a session, so the checks above prove nothing"

# 5. Recreate the container, keep the volumes ----------------------------------------------
echo "Recreating the container..."
bash -c "$RECREATE_CMD" || fail "RECREATE_CMD failed"
wait_ready || fail "/health/ready did not return 200 after recreation"
pass "/health/ready returned 200 after recreation"

# 6. Data survived -------------------------------------------------------------------------
code="$(curl -s -o /dev/null -w '%{http_code}' -H "Cookie: $cookie" "$BASE_URL/Profile")"
[[ "$code" == "200" ]] || fail "old session cookie returned $code after recreation: Data Protection keys were not persisted"
pass "old session cookie still valid (Data Protection keys persisted)"

jar2="$work/jar2.txt"
code="$(sign_in "$jar2")"
[[ "$code" == "302" ]] || fail "the registered account could not sign in after recreation (got $code): SQLite data was lost"
[[ -n "$(auth_cookie "$jar2")" ]] || fail "no auth cookie after signing in again"
pass "registered account signs in again (SQLite data persisted)"

echo "All container checks passed."
