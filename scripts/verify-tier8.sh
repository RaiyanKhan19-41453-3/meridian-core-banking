#!/bin/bash
# Concurrency & ledger-integrity suite. Requires a FRESH volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
pass=0; fail=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi
}

if docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "select count(*) from transactions where reference like '%c8-fund%'" 2>/dev/null | grep -vq '^0$'; then
  echo 'ERROR: this database already has test data. Run docker compose down -v && up -d first.'
  exit 2
fi

TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")
ACCS=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK")
SAV=$(echo "$ACCS" | j "[0]['id']")
CUR=$(echo "$ACCS" | j "[1]['id']")
CURNO=$(DB "select account_number from accounts where id='$CUR'")

echo '=== 1. fund savings 10000 ==='
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c8-fund" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":10000,\"description\":\"fund\"}"
check 'initial balance 10000' 10000.0000 "$(DB "select balance from accounts where id='$SAV'")"

echo '=== 2. five concurrent withdrawals of 3000 (should serialize: 3 ok, 2 insufficient) ==='
for i in 1 2 3 4 5; do
  curl -s -o /tmp/c8-w-$i.json -w '%{http_code}' -X POST $BASE/api/transactions/withdraw \
    -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c8-w-$i" -H "$CT" \
    -d "{\"accountId\":\"$SAV\",\"amount\":3000,\"description\":\"race $i\"}" > /tmp/c8-w-$i.code &
done
wait
OK=$(grep -l '^201$' /tmp/c8-w-*.code | wc -l)
INSUFF=$(grep -l 'insufficient_funds' /tmp/c8-w-*.json | wc -l)
CONFLICT=$(grep -l 'concurrent_modification' /tmp/c8-w-*.json | wc -l)
check '3 withdrawals succeed' 3 "$OK"
check '2 rejected insufficient funds' 2 "$INSUFF"
check 'no spurious 409 conflicts' 0 "$CONFLICT"
check 'balance 1000 after 3x3000' 1000.0000 "$(DB "select balance from accounts where id='$SAV'")"
check 'ledger sum matches balance' 1000.0000 "$(DB "select coalesce(sum(amount),0) from ledger_entries where account_id='$SAV'")"

echo '=== 3. five concurrent transfers of 1000 (all succeed, conservation) ==='
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c8-fund2" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":5000,\"description\":\"fund2\"}"
B1=$(DB "select balance from accounts where id='$SAV'"); B2=$(DB "select balance from accounts where id='$CUR'")
for i in 1 2 3 4 5; do
  curl -s -o /tmp/c8-t-$i.json -w '%{http_code}' -X POST $BASE/api/transactions/transfer \
    -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c8-t-$i" -H "$CT" \
    -d "{\"fromAccountId\":\"$SAV\",\"toAccountNumber\":\"$CURNO\",\"amount\":1000,\"description\":\"xfer $i\"}" > /tmp/c8-t-$i.code &
done
wait
OK=$(grep -l '^201$' /tmp/c8-t-*.code | wc -l)
check '5 transfers succeed' 5 "$OK"
A1=$(DB "select balance from accounts where id='$SAV'"); A2=$(DB "select balance from accounts where id='$CUR'")
python3 -c "print('ok' if abs(($A1+$A2)-($B1+$B2))<0.001 else 'broken')" > /tmp/c8-cons
check 'money conserved across transfers' ok "$(cat /tmp/c8-cons)"
check 'source not overdraw' true "$([ "$(python3 -c "print(1 if $A1>=0 else 0)")" = "1" ] && echo true)"

echo '=== 4. same idempotency key under concurrency: posted once ==='
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c8-fund3" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":10000,\"description\":\"fund3\"}"
KEY="${RUN_ID}c8-same-key"
C1=$(DB "select balance from accounts where id='$SAV'")
for i in 1 2 3 4 5; do
  curl -s -o /tmp/c8-s-$i.json -w '%{http_code}' -X POST $BASE/api/transactions/withdraw \
    -H "Authorization: Bearer $TOK" -H "Idempotency-Key: $KEY" -H "$CT" \
    -d "{\"accountId\":\"$SAV\",\"amount\":1000,\"description\":\"same\"}" > /tmp/c8-s-$i.code &
done
wait
C2=$(DB "select balance from accounts where id='$SAV'")
check 'same-key race debits exactly 1000' 1000.00 "$(python3 -c "print(f'{float(\"$C1\")-float(\"$C2\"):.2f}')")"
ROWS=$(DB "select count(*) from transactions where reference='$KEY'")
check 'exactly one transaction for key' 1 "$ROWS"

echo '=== 5. ledger integrity: every transaction sums to zero ==='
UNBAL=$(DB "select count(*) from (select transaction_id from ledger_entries group by transaction_id having sum(amount)<>0) t")
check 'all transactions balanced' 0 "$UNBAL"

echo
echo "RESULT: $pass passed, $fail failed"
