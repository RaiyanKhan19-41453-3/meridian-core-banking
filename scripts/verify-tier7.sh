#!/bin/bash
# Tier-7 verification — BDT accounts + CIB-blended decisions + Bangladesh products.
# Run on a fresh volume (otherwise /api/auth/login etc works but demo seed state differs).
set -e
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
num_eq() { python3 -c "import sys; sys.exit(0 if abs(float(sys.argv[1]) - float(sys.argv[2])) < 0.01 else 1)" "$1" "$2"; }
RUN_ID=r$RANDOM$RANDOM
U7="bdt7_${RUN_ID}@example.com"

echo '=== 1. register fresh user, complete KYC, admin approves ==='
curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U7\",\"password\":\"Pass1234!\",\"fullName\":\"BDT Customer ${RUN_ID}\"}" > /dev/null
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U7\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
PENDING=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=50" -H "Authorization: Bearer $TOK_A")
U7_UID=$(echo "$PENDING" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U7'][0])")
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $TOK" -H "$CT" -d '{"fullLegalName":"BDT Customer Demo","dateOfBirth":"1990-04-04","nationality":"Bangladesh","phone":"+8801712345678","addressLine":"Banani 11","city":"Dhaka","country":"Bangladesh","idType":"NationalId","idNumber":"BD-1234567890","employmentStatus":"Employed","annualIncomeBand":"25k-50k"}' > /dev/null
code=$(code_of -X POST $BASE/api/admin/kyc/$U7_UID/approve -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'admin approves user' 204 "$code"

echo '=== 2. open BDT Savings + Current and verify seamless labelling ==='
SAV_ID=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK" -H "$CT" -d '{"type":"Savings","currency":"BDT"}' | j "['id']")
CURR_ID=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK" -H "$CT" -d '{"type":"Current","currency":"BDT"}' | j "['id']")
for id in $SAV_ID $CURR_ID; do
  CURR=$(DB "select currency from accounts where id='$id'")
  check "account $id is BDT" BDT "$CURR"
done

echo '=== 3. fund BDT savings 600,000; debt application gets CIB-rejected/internal-approved mixture ==='
code=$(code_of -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t7-fund" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":600000,\"description\":\"BDT funding\"}")
check 'BDT deposit posted' 201 "$code"
bal=$(DB "select balance from accounts where id='$SAV_ID'")
num_eq "$bal" 600000 && check 'savings balance 600,000 BDT' ok ok || check 'savings balance 600,000 BDT' fail ok
# 600,000 BDT ≈ USD 5,040 — below the USD 10,000 CTR threshold; no HighValue alert expected
ALERTS=$(curl -s "$BASE/api/admin/aml/alerts?status=Open&pageSize=50" -H "Authorization: Bearer $TOK_A")
HV=$(echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(sum(1 for a in d['items'] if a['alertType']=='HighValue'))")
check 'no CTR alert for 600,000 BDT (~$5k) deposit' 0 "$HV"

echo '=== 4. apply PLN-BDT 200,000 (internal only → low score, rejected) ==='
APP=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"loanProductCode\":\"PLN-BDT\",\"principal\":200000,\"termMonths\":12,\"purpose\":\"BDT consumer loan\",\"disbursementAccountId\":\"$CURR_ID\"}")
check 'low-income BDT loan rejected (internal)' Rejected "$(echo "$APP" | j "['decision']")"
check 'score source Internal' Internal "$(echo "$APP" | j "['scoreSource']")"

echo '=== 5. same application with a strong CIB score lifts through threshold ==='
APP_CIB=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"loanProductCode\":\"PLN-BDT\",\"principal\":50000,\"termMonths\":12,\"purpose\":\"BDT loan\",\"disbursementAccountId\":\"$CURR_ID\",\"cibScore\":820}")
echo "$APP_CIB" | python3 -c "import sys,json; d=json.load(sys.stdin); print('decision:', d['decision'], '| final score:', d['creditScore'], '| raw CIB:', d['cibScore'], '| source:', d['scoreSource'])"
check 'CIB-blended BDT loan approved' Approved "$(echo "$APP_CIB" | j "['decision']")"
check 'CIB bureau score surfaced' 820 "$(echo "$APP_CIB" | j "['cibScore']")"
check 'score source CIB Bureau' 'CIB Bureau' "$(echo "$APP_CIB" | j "['scoreSource']")"
LOAN_ID=$(echo "$APP_CIB" | python3 -c "import sys,json; print(json.load(sys.stdin)['loan']['summary']['id'])")
check 'loan schedule principals sum to 50,000 BDT' 50000.0 "$(echo "$APP_CIB" | python3 -c "import sys,json; d=json.load(sys.stdin); print(round(sum(i['principal'] for i in d['loan']['schedule']),4))")"

echo '=== 6. accept and disburse BDT loan ==='
code=$(code_of -X POST $BASE/api/loans/$LOAN_ID/accept-and-disburse -H "Authorization: Bearer $TOK" -H "$CT" -d '{}')
check 'BDT loan disbursement posted' 200 "$code"
bal=$(DB "select balance from accounts where id='$CURR_ID'")
num_eq "$bal" 45000 && check 'current credited 50,000 - 5,000 BDT fee' ok ok || check 'current credited 50,000 - 5,000 BDT fee' fail "$bal"

echo '=== 7. BDT overdraft apply ODL-SEC-BDT with pledged Savings ==='
OD_APP=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"productCode\":\"ODL-SEC-BDT\",\"requestedLimit\":500000,\"collateralAccountId\":\"$SAV_ID\"}")
echo "$OD_APP" | python3 -c "import sys,json; d=json.load(sys.stdin); print('status:', d['status'], '| rate:', d['annualInterestRate'], '| product:', d['productCode'])"
check 'ODL-SEC-BDT approved' Active "$(echo "$OD_APP" | j "['status']")"
check 'ODL-SEC-BDT product code' ODL-SEC-BDT "$(echo "$OD_APP" | j "['productCode']")"
check 'ODL-SEC-BDT rate 11% p.a.' 11.0 "$(echo "$OD_APP" | j "['annualInterestRate']")"

echo '=== 7. over-pledge withdrawal rejected; within headroom allowed ==='
# Savings = 600,000; pledge = 500,000 / 0.9 = 555,555.56; free = 44,444.44
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t7-wd-block" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":50000,\"description\":\"too much\"}")
check 'over-pledge withdrawal blocked' 422 "$code"
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t7-wd-ok" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":40000,\"description\":\"within headroom\"}")
check 'within-headroom withdrawal allowed' 201 "$code"
bal=$(DB "select balance from accounts where id='$SAV_ID'")
num_eq "$bal" 560000 && check 'savings 560,000 BDT after withdrawal' ok ok || check 'savings 560,000 BDT after withdrawal' fail ok

echo '=== 8. BDT overdraft utilization accrues → GL converts at 0.0084 USD/BDT ==='
# Savings = 600,000; pledge = 500,000 / 0.9 = 555,555.56; free = 44,444.44
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t7-wd-block2" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":50000,\"description\":\"should stay blocked\"}")
check 'over-pledge withdrawal stays blocked' 422 "$code"

# Current balance after loan disbursement is 45,000 BDT. Draw 60,000 into the ODL-SEC-BDT headroom → −15,000 BDT.
curl -s -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t7-draw-od" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"amount\":60000,\"description\":\"BDT overdraft draw\"}" > /dev/null
credit=$(DB "select balance from accounts where id='$CURR_ID'")
echo "current balance after draw: $credit"
python3 -c "import sys; sys.exit(0 if float('$credit') < 0 else 1)" && check 'current is overdrawn' ok ok || check 'current is overdrawn' fail "$credit"

TODAY=$(date -u +%F)
OUT=$(curl -s -X POST $BASE/api/admin/batch/accrue-overdraft-interest -H "Authorization: Bearer $TOK_A" -H "$CT" -d "{\"date\":\"$TODAY\"}")
echo "$OUT"
expect_accrual=$(python3 -c "print(round(15000 * 0.11 / 365, 4))")
accrued=$(DB "select amount from overdraft_accruals where facility_id = (select id from overdraft_facilities where collateral_account_id = '$SAV_ID') order by id desc limit 1;")
echo "accrued for facility: $accrued (expected ~$expect_accrual BDT)"
OD_GL=$(DB "select count(*) from gl_journals where transaction_id is null and description like 'Overdraft interest accrual%';")
python3 -c "import sys; sys.exit(0 if int('$OD_GL') >= 1 else 1)" && check 'interest receivable journal posted' ok ok || check 'interest receivable journal posted' fail ok
UNBALANCED=$(DB "select count(*) from (select journal_id from gl_entries group by journal_id having sum(amount) <> 0) t")
check 'all GL journals balanced' 0 "$UNBALANCED"
GRAND=$(DB "select coalesce(sum(amount),0) from gl_entries")
python3 -c "import sys; sys.exit(0 if abs(float('$GRAND')) < 0.0005 else 1)" && check 'GL grand total ~zero' ok ok || check 'GL grand total ~zero' fail ok

echo '=== 9. CIB blended score on overdraft (second Current + CIB 820) ==='
CURR2_ID=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK" -H "$CT" -d '{"type":"Current","currency":"BDT"}' | j "['id']")
OD_VAR=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"accountId\":\"$CURR2_ID\",\"productCode\":\"ODL-VAR-BDT\",\"requestedLimit\":50000,\"cibScore\":820}")
check 'ODL-VAR-BDT CIB-blended approved' Active "$(echo "$OD_VAR" | j "['status']")"
check 'ODL-VAR-BDT rate 22% p.a.' 22.0 "$(echo "$OD_VAR" | j "['annualInterestRate']")"
check 'CIB provenance retained' 820 "$(echo "$OD_VAR" | j "['cibScore']")"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
