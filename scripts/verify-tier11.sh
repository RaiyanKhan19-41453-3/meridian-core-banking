#!/bin/bash
# Tier-11 verification — roadmap item 13: beneficiaries, scheduled payments,
# sandbox rail. Requires a FRESH volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }

TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")
ADM=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")
SAV=$(DB "select id from accounts where account_number='AC100000000001'")
CUR=$(DB "select id from accounts where account_number='AC100000000002'")
TODAY=$(date -u +%F)

curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t11-fund" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":1000,\"description\":\"fund\"}"

echo '=== 1. beneficiary CRUD ==='
B=$(curl -s -X POST $BASE/api/payments/beneficiaries -H "Authorization: Bearer $TOK" -H "$CT" -d '{"nickname":"My Current","accountNumber":"AC100000000002"}')
check 'beneficiary created' 201 "$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/payments/beneficiaries -H "Authorization: Bearer $TOK" -H "$CT" -d '{"nickname":"Other","accountNumber":"AC100000000002"}')"
BID=$(echo "$B" | j "['id']")
check 'beneficiary currency from account' USD "$(echo "$B" | j "['currency']")"
DUP=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/payments/beneficiaries -H "Authorization: Bearer $TOK" -H "$CT" -d '{"nickname":"My Current","accountNumber":"AC100000000002"}')
check 'duplicate nickname rejected' 409 "$DUP"
BAD=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/payments/beneficiaries -H "Authorization: Bearer $TOK" -H "$CT" -d '{"nickname":"Ghost","accountNumber":"AC999999999999"}')
check 'unknown beneficiary account rejected' 404 "$BAD"
for i in 1 2 3 4 5; do
  curl -s -o /tmp/t11-bf-$i.json -w '%{http_code}' -X POST $BASE/api/payments/beneficiaries -H "Authorization: Bearer $TOK" -H "$CT" -d '{"nickname":"Race Payee","accountNumber":"AC100000000002"}' > /tmp/t11-bf-$i.code &
done
wait
OK=$(grep -l '^201$' /tmp/t11-bf-*.code | wc -l)
CONF=$(grep -l 'beneficiary_exists' /tmp/t11-bf-*.json | wc -l)
check 'concurrent beneficiary: exactly one created' 1 "$OK"
check 'concurrent beneficiary: losers get 409' 4 "$CONF"

echo '=== 2. schedule and execute a one-off payment ==='
PAY=$(curl -s -X POST $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"fromAccountId\":\"$SAV\",\"beneficiaryId\":\"$BID\",\"amount\":100,\"frequency\":\"Once\",\"scheduledDate\":\"$TODAY\",\"description\":\"rent\"}")
PID=$(echo "$PAY" | j "['id']")
check 'payment scheduled' Pending "$(echo "$PAY" | j "['status']")"
check 'sandbox rail selected' sandbox "$(echo "$PAY" | j "['rail']")"
RES=$(curl -s -X POST $BASE/api/admin/batch/execute-scheduled-payments -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"date\":\"$TODAY\"}")
check 'one payment executed' 1 "$(echo "$RES" | python3 -c "import sys,json; print(json.load(sys.stdin)['executed'])")"
ST=$(curl -s $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" | python3 -c "import sys,json; print([p['status'] for p in json.load(sys.stdin) if p['id']=='$PID'][0])")
check 'payment marked Executed' Executed "$ST"
check 'savings debited 100' 900.0000 "$(DB "select balance from accounts where id='$SAV'")"
check 'current credited 100' 100.0000 "$(DB "select balance from accounts where id='$CUR'")"
TXN=$(DB "select count(*) from transactions where reference like 'PMT-$PID-%' and type='Transfer'")
check 'exactly one transfer posted' 1 "$TXN"

echo '=== 3. re-execution is idempotent ==='
RES2=$(curl -s -X POST $BASE/api/admin/batch/execute-scheduled-payments -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"date\":\"$TODAY\"}")
check 'no payment re-executed' 0 "$(echo "$RES2" | python3 -c "import sys,json; print(json.load(sys.stdin)['executed'])")"
check 'balance unchanged' 900.0000 "$(DB "select balance from accounts where id='$SAV'")"

echo '=== 4. recurring payment advances to next week ==='
REC=$(curl -s -X POST $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"fromAccountId\":\"$SAV\",\"toAccountNumber\":\"AC100000000002\",\"amount\":50,\"frequency\":\"Weekly\",\"scheduledDate\":\"$TODAY\"}")
RID=$(echo "$REC" | j "['id']")
curl -s -o /dev/null -X POST $BASE/api/admin/batch/execute-scheduled-payments -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"date\":\"$TODAY\"}"
NEXT=$(curl -s $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" | python3 -c "import sys,json; print([p['scheduledDate'][:10] for p in json.load(sys.stdin) if p['id']=='$RID'][0])")
EXPECT=$(date -u -d '+7 days' +%F)
check 'weekly payment advances 7 days' "$EXPECT" "$NEXT"
ST2=$(curl -s $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" | python3 -c "import sys,json; print([p['status'] for p in json.load(sys.stdin) if p['id']=='$RID'][0])")
check 'recurring payment stays Pending' Pending "$ST2"

echo '=== 5. cancel a pending payment ==='
CAN=$(curl -s -X POST $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"fromAccountId\":\"$SAV\",\"toAccountNumber\":\"AC100000000002\",\"amount\":25,\"frequency\":\"Once\",\"scheduledDate\":\"$(date -u -d '+2 days' +%F)\"}")
CID=$(echo "$CAN" | j "['id']")
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/payments/scheduled/$CID/cancel -H "Authorization: Bearer $TOK")
check 'cancel returns 204' 204 "$code"
ST3=$(curl -s $BASE/api/payments/scheduled -H "Authorization: Bearer $TOK" | python3 -c "import sys,json; print([p['status'] for p in json.load(sys.stdin) if p['id']=='$CID'][0])")
check 'payment Cancelled' Cancelled "$ST3"

echo '=== 6. RBAC: customer cannot execute the rail ==='
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/admin/batch/execute-scheduled-payments -H "Authorization: Bearer $TOK" -H "$CT" -d '{}')
check 'customer cannot execute rail' 403 "$code"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
