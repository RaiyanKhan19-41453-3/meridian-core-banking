#!/bin/bash
# Tier-5 verification — overdraft facility: apply, draw, accrue, charge, close.
# Run on a FRESH volume (after tiers 1–4, or standalone; it opens its own accounts).
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
num_eq() { python3 -c "import sys; a,b=float(sys.argv[1]),float(sys.argv[2]); sys.exit(0 if abs(a-b)<0.0001 else 1)" "$1" "$2"; }

RAND=$RANDOM$RANDOM
U5="odu5_${RAND}@example.com"
echo "test user: $U5"

echo '=== admin + demo login ==='
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
TOK_D=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")

echo '=== 0. terms endpoint requires auth, returns ODL-VAR ==='
code=$(code_of $BASE/api/overdraft/terms)
check 'terms anonymous rejected' 401 "$code"
TERMS=$(curl -s $BASE/api/overdraft/terms -H "Authorization: Bearer $TOK_D")
check 'terms include ODL-VAR' ok "$(echo "$TERMS" | python3 -c "import sys,json; d=json.load(sys.stdin); print('ok' if any(p['productCode']=='ODL-VAR' for p in d) else 'missing')")"
check 'terms include ODL-SEC' ok "$(echo "$TERMS" | python3 -c "import sys,json; d=json.load(sys.stdin); print('ok' if any(p['productCode']=='ODL-SEC' for p in d) else 'missing')")"

echo '=== 1. unverified user cannot apply ==='
curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U5\",\"password\":\"Pass1234!\",\"fullName\":\"Tier Five Pending\"}" > /dev/null
TOK_U5=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U5\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"00000000-0000-0000-0000-000000000000\",\"requestedLimit\":5000}")
check 'apply rejected pre-KYC' 403 "$code"

echo '=== 2. admin approves KYC; open Current USD account ==='
PENDING=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=10" -H "Authorization: Bearer $TOK_A")
U5_UID=$(echo "$PENDING" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U5'][0])")
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $TOK_U5" -H "$CT" -d '{"fullLegalName":"Tier Five Pending","dateOfBirth":"1991-11-11","nationality":"Kenya","phone":"+254733444555","addressLine":"15 Ngong Road","city":"Nairobi","country":"Kenya","idType":"NationalId","idNumber":"KE99887766","employmentStatus":"Employed","annualIncomeBand":"50k-100k"}' > /dev/null
code=$(code_of -X POST $BASE/api/admin/kyc/$U5_UID/approve -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'admin approves u5' 204 "$code"
CURR_U5=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_U5" -H "$CT" -d '{"type":"Current","currency":"USD"}' | j "['id']")

echo '=== 3. apply: Savings rejected (Current only), below-min & above-max rejected ==='
SAV_U5=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_U5" -H "$CT" -d '{"type":"Savings","currency":"USD"}' | j "['id']")
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$SAV_U5\",\"requestedLimit\":5000}")
check 'Savings account rejected' 422 "$code"
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"requestedLimit\":100}")
check 'below-min limit rejected' 400 "$code"
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"requestedLimit\":30000}")
check 'above-max limit rejected' 400 "$code"
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"requestedLimit\":'100'}")
check 'non-numeric handled' 400 "$code"

echo '=== 4. apply 10,000 → approved (score high, income 50k-100k) ==='
APP=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"requestedLimit\":10000}")
echo "$APP" | python3 -c "import sys,json; d=json.load(sys.stdin); print('status:', d['status'], '| score:', d['creditScore'], '| available:', d['availableCredit'])"
check 'facility approved' Active "$(echo "$APP" | j "['status']")"
check 'full headroom available' 10000.0 "$(echo "$APP" | j "['availableCredit']")"
FAC_ID=$(echo "$APP" | j "['id']")

echo '=== 5. duplicate application rejected ==='
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U5" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"requestedLimit\":6000}")
check 'second facility on same account rejected' 409 "$code"

echo '=== 6. draw the full 10,000 headroom in one go (account goes negative) ==='
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U5" -H "Idempotency-Key: ${RUN_ID}t5-wd-full" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"amount\":10000,\"description\":\"full overdraft draw\"}")
check 'withdraw into overdraft allowed' 201 "$code"
BAL=$(DB "select balance from accounts where id='$CURR_U5'")
num_eq "$BAL" -10000.0 && check 'balance is -10,000.00' ok ok || check 'balance is -10,000.00' fail ok

echo '=== 7. any further draw beyond headroom rejected ==='
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U5" -H "Idempotency-Key: ${RUN_ID}t5-wd-after-full" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"amount\":1,\"description\":\"beyond headroom\"}")
check 'over-draw rejected (422)' 422 "$code"
BAL2=$(DB "select balance from accounts where id='$CURR_U5'")
num_eq "$BAL2" -10000.0 && check 'rejected draw posted nothing' ok ok || check 'rejected draw posted nothing' fail ok

echo '=== 8. CTR-style AML rule fires on the ≥10,000 movement ==='
sleep 1
ALERTS=$(curl -s "$BASE/api/admin/aml/alerts?status=Open&pageSize=100" -H "Authorization: Bearer $TOK_A")
check 'HighValue alert raised for 10k draw' ok "$(echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print('ok' if any(i['alertType']=='HighValue' and i['userEmail']=='$U5' for i in d['items']) else 'missing')")"

echo '=== 9. forced overdraft accrual run posts 1010/6101 journal ==='
ACC=$(curl -s -X POST $BASE/api/admin/batch/accrue-overdraft-interest -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
echo "$ACC"
check 'accrual run succeeded' Succeeded "$(echo "$ACC" | j "['status']")"
python3 -c "import sys; sys.exit(0 if int('$(echo "$ACC" | python3 -c "import sys,json; print(json.load(sys.stdin)['summary']['overdraftAccruals'])")') >= 1 else 1)" \
  && check 'at least one overdraft accrual posted' ok ok || check 'at least one overdraft accrual posted' fail ok
ACCRUED=$(DB "select amount from overdraft_accruals order by id desc limit 1")
EXPECT=$(python3 -c "print(round(10000 * 0.24 / 365, 4))")
echo "accrued today: $ACCRUED (expected ≈ $EXPECT)"
python3 -c "import sys; sys.exit(0 if abs(float('$ACCRUED') - float('$EXPECT')) < 0.001 else 1)" \
  && check "accrued == 10,000 x 24% / 365" ok ok || check "accrual amount mismatch (got $ACCRUED, want $EXPECT)" fail ok

echo '=== 10. GL accrual journal posted (Dr 1010 / Cr 6101) ==='
OD_GL=$(DB "select count(*) from gl_journals j join gl_entries e on e.journal_id = j.id join gl_accounts g on g.id = e.gl_account_id where g.code = '1010' and j.transaction_id is null;")
python3 -c "import sys; sys.exit(0 if int('$OD_GL') >= 1 else 1)" && check 'interest receivable journal posted' ok ok || check 'interest receivable journal posted' fail ok

echo '=== 11. charge overdraft interest (forced run for this month) ==='
CUR_M=$(date -u +%m); CUR_Y=$(date -u +%Y)
CHG=$(curl -s -X POST $BASE/api/admin/batch/charge-overdraft-interest -H "Authorization: Bearer $TOK_A" -H "$CT" -d "{\"year\":$CUR_Y,\"month\":$CUR_M}")
echo "$CHG"
check 'overdraft charge run succeeded' Succeeded "$(echo "$CHG" | j "['status']")"
check 'at least one charge posted' ok "$(echo "$CHG" | python3 -c "import sys,json; print('ok' if json.load(sys.stdin)['summary']['charged'] >= 1 else 'zero')")"
CHG2=$(curl -s -X POST $BASE/api/admin/batch/charge-overdraft-interest -H "Authorization: Bearer $TOK_A" -H "$CT" -d "{\"year\":$CUR_Y,\"month\":$CUR_M}")
check 'charge replay flagged' true "$(echo "$CHG2" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['replayed']).lower())")"

echo '=== 12. overdraft charge posted to statement ==='
STMT=$(curl -s "$BASE/api/accounts/$CURR_U5/statement?page=1&pageSize=20" -H "Authorization: Bearer $TOK_U5")
echo "$STMT" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(i['type'], i['amount'], '|', i['description']) for i in d['items']]"
check 'interest charge in statement' ok "$(echo "$STMT" | python3 -c "import sys,json; print('ok' if any(i['type']=='Fee' and 'Overdraft interest' in i['description'] for i in json.load(sys.stdin)['items']) else 'missing')")"

echo '=== 13. trial balance still balanced after overdraft activity ==='
TB=$(curl -s $BASE/api/admin/gl/trial-balance -H "Authorization: Bearer $TOK_A")
check 'trial balance balanced' true "$(echo "$TB" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"

echo '=== 14. repay the overdrawn balance, close facility ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_U5" -H "Idempotency-Key: ${RUN_ID}t5-repay-1" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"amount\":20000,\"description\":\"repay overdraft\"}" > /dev/null
CL=$(curl -s -X POST $BASE/api/overdraft/$FAC_ID/close -H "Authorization: Bearer $TOK_U5" -H "$CT" -d '{}')
check 'facility closed' Closed "$(echo "$CL" | j "['status']")"
BAL3=$(DB "select balance from accounts where id='$CURR_U5'")
WITHDRAW_AMT=$(python3 -c "print(round(float('$BAL3') + 1, 2))")
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U5" -H "Idempotency-Key: ${RUN_ID}t5-wd-after" -H "$CT" -d "{\"accountId\":\"$CURR_U5\",\"amount\":$WITHDRAW_AMT,\"description\":\"beyond balance after close\"}")
check 'overdraft headroom revoked after close' 422 "$code"

echo '=== 15. admin overdraft portfolio lists the closed facility ==='
POLL=$(curl -s "$BASE/api/admin/overdraft?status=Closed&page=1&pageSize=10" -H "Authorization: Bearer $TOK_A")
check 'closed facility visible to admin' 1 "$(echo "$POLL" | python3 -c "import sys,json; print(sum(1 for f in json.load(sys.stdin)['items'] if f['id']=='$FAC_ID'))")"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
