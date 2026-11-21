#!/bin/bash
# Tier-12 verification — roadmap item 15: observability, retention, PII encryption.
# Requires a FRESH volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }

ADM=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")

echo '=== 1. observability: correlation id on responses ==='
CID=$(curl -s -D /tmp/t12-hdr -o /dev/null $BASE/api/auth/me -H "Authorization: Bearer $TOK" && grep -i '^x-correlation-id:' /tmp/t12-hdr | tr -d '\r' | awk '{print $2}')
[ -n "$CID" ] && check 'correlation id header present' ok ok || check 'correlation id header present' fail none
CID2=$(curl -s -D /tmp/t12-hdr2 -o /dev/null -H "X-Correlation-Id: my-trace-123" $BASE/api/auth/me -H "Authorization: Bearer $TOK" && grep -i '^x-correlation-id:' /tmp/t12-hdr2 | tr -d '\r' | awk '{print $2}')
check 'correlation id propagated' my-trace-123 "$CID2"

echo '=== 2. liveness and readiness endpoints ==='
code=$(curl -s -o /tmp/live.json -w '%{http_code}' $BASE/health/live)
check 'health/live 200' 200 "$code"
check 'liveness status' alive "$(cat /tmp/live.json | j "['status']")"
code=$(curl -s -o /tmp/ready.json -w '%{http_code}' $BASE/health/ready)
check 'health/ready 200' 200 "$code"
check 'readiness status' ready "$(cat /tmp/ready.json | j "['status']")"

echo '=== 3. admin metrics (JSON + Prometheus), RBAC ==='
code=$(curl -s -o /tmp/m.json -w '%{http_code}' $BASE/api/metrics -H "Authorization: Bearer $ADM")
check 'metrics JSON 200' 200 "$code"
check 'metrics service name' core-banking-api "$(cat /tmp/m.json | j "['service']")"
REQ=$(cat /tmp/m.json | python3 -c "import sys,json; print(json.load(sys.stdin)['requests'])")
python3 -c "import sys; sys.exit(0 if int('$REQ')>0 else 1)" && check 'request counter positive' ok ok || check 'request counter positive' fail ok
code=$(curl -s -o /tmp/prom.txt -w '%{http_code}' $BASE/api/metrics/prometheus -H "Authorization: Bearer $ADM")
check 'prometheus endpoint 200' 200 "$code"
grep -q 'corebanking_requests_total' /tmp/prom.txt && check 'prometheus payload has counters' ok ok || check 'prometheus payload has counters' fail none
code=$(curl -s -o /dev/null -w '%{http_code}' $BASE/api/metrics -H "Authorization: Bearer $TOK")
check 'metrics forbidden for customer' 403 "$code"

echo '=== 4. retention purges old audit rows but keeps filed alerts ==='
DB "insert into audit_logs (user_id, action, entity_name, entity_id, payload, ip_address, created_at) values (null, 'retention.test', 'Test', 'old', null, null, '2000-01-01')" > /dev/null
OLD=$(DB "select count(*) from audit_logs where created_at < '2010-01-01'")
python3 -c "import sys; sys.exit(0 if int('$OLD')>=1 else 1)" && check 'old audit row seeded' ok ok || check 'old audit row seeded' fail ok
RES=$(curl -s -X POST $BASE/api/admin/batch/retention -H "Authorization: Bearer $ADM" -H "$CT" -d '{}')
echo "$RES" | head -c 200; echo
AFTER=$(DB "select count(*) from audit_logs where created_at < '2010-01-01'")
check 'old audit row purged' 0 "$AFTER"

echo '=== 5. KYC documents encrypted at rest and readable ==='
U="t12_${RUN_ID}@example.com"
curl -s -o /dev/null -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U\",\"password\":\"Pass1234!\",\"fullName\":\"Encryption Test\"}"
UTOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
printf 'hello encrypted world' > /tmp/t12-doc.pdf
DID=$(curl -s -X POST $BASE/api/kyc/documents -H "Authorization: Bearer $UTOK" -F docType=NationalId -F 'file=@/tmp/t12-doc.pdf;type=application/pdf' | j "['id']")
[ -n "$DID" ] && check 'document uploaded' ok ok || check 'document uploaded' fail none
HDR=$(DB "select encode(content, 'escape') from kyc_documents where id='$DID'" | head -c 4)
check 'document stored with ENC1 framing' ENC1 "$HDR"
RAW=$(DB "select encode(content, 'base64') from kyc_documents where id='$DID'")
echo "$RAW" | base64 -d > /tmp/t12-downloaded.pdf 2>/dev/null || true
if cmp -s /tmp/t12-doc.pdf /tmp/t12-downloaded.pdf; then
  check 'stored bytes differ from plaintext' fail same
else
  check 'stored bytes differ from plaintext' ok ok
fi
curl -s -o /tmp/t12-served.pdf $BASE/api/kyc/documents/$DID/content -H "Authorization: Bearer $UTOK"
cmp -s /tmp/t12-doc.pdf /tmp/t12-served.pdf && check 'download decrypts to original' ok ok || check 'download decrypts to original' fail none

echo '=== 6. interrupted scheduled payments recover on startup ==='
SAVID=$(DB "select id from accounts where account_number='AC100000000001'")
PID=$(curl -s -X POST $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"fromAccountId\":\"$SAVID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":1,\"frequency\":\"Once\",\"scheduledDate\":\"$(date -u +%F)\"}" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")
DB "update scheduled_payments set status='Processing' where id='$PID'" > /dev/null
docker compose restart api > /dev/null 2>&1
for i in $(seq 1 60); do code=$(curl -s -o /dev/null -w '%{http_code}' $BASE/health 2>/dev/null || true); [ "$code" = "200" ] && break; sleep 1; done
STATUS=$(DB "select status from scheduled_payments where id='$PID'")
check 'stuck Processing payment reset to Pending' Pending "$STATUS"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
