#!/bin/bash
# Tier-6 verification — secured overdraft (collateral lock) + late-payment fees on missed installments.
# Run on a fresh volume after tiers 1–5, or standalone.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
num_eq() { python3 -c "import sys; sys.exit(0 if abs(float(sys.argv[1]) - float(sys.argv[2])) < 0.0001 else 1)" "$1" "$2"; }

RAND=$RANDOM$RANDOM
U6="secod6_${RAND}@example.com"
echo "test user: $U6"

echo '=== 1. register a fresh user & open savings + current USD accounts ==='
curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U6\",\"password\":\"Pass1234!\",\"fullName\":\"Tier Six Secured\"}" > /dev/null
TOK_U6=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U6\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")

echo '=== 2. admin approves KYC for the user ==='
PENDING=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=10" -H "Authorization: Bearer $TOK_A")
U6_UID=$(echo "$PENDING" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U6'][0])")
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $TOK_U6" -H "$CT" -d '{"fullLegalName":"Tier Six Secured","dateOfBirth":"1989-07-07","nationality":"Kenya","phone":"+254744555666","addressLine":"21 Koinange Street","city":"Nairobi","country":"Kenya","idType":"NationalId","idNumber":"KE11122233","employmentStatus":"Employed","annualIncomeBand":"50k-100k"}' > /dev/null
code=$(code_of -X POST $BASE/api/admin/kyc/$U6_UID/approve -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'admin approves user' 204 "$code"
SAV_ID=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_U6" -H "$CT" -d '{"type":"Savings","currency":"USD"}' | j "['id']")
CURR_ID=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_U6" -H "$CT" -d '{"type":"Current","currency":"USD"}' | j "['id']")

echo '=== 3. fund savings with 20,000 ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-fund-1" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":20000,\"description\":\"seed savings\"}" > /dev/null

echo '=== 4. ODL-SEC requires collateral — apply without it → 400 ==='
code=$(code_of -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U6" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"productCode\":\"ODL-SEC\",\"requestedLimit\":10000}")
check 'missing collateral rejected' 400 "$code"

echo '=== 5. ODL-SEC with insufficient collateral → Rejected decision (no facility) ==='
# limit 15,000 → requires 16,666.67; balance 20,000 so it is fine; use 25,000 → requires 27,777.78 > 20,000 → rejected
APP_REJ=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U6" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"productCode\":\"ODL-SEC\",\"requestedLimit\":25000,\"collateralAccountId\":\"$SAV_ID\"}")
echo "$APP_REJ" | python3 -c "import sys,json; d=json.load(sys.stdin); print('status:', d['status'], '|', d['decisionReason'])"
check 'insufficient-pledge application is Rejected (not an open facility)' Rejected "$(echo "$APP_REJ" | j "['status']")"

echo '=== 6. ODL-SEC with sufficient pledge → Active at 12% APR ==='
APP_OK=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK_U6" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"productCode\":\"ODL-SEC\",\"requestedLimit\":15000,\"collateralAccountId\":\"$SAV_ID\"}")
echo "$APP_OK" | python3 -c "import sys,json; d=json.load(sys.stdin); print('status:', d['status'], '| rate:', d['annualInterestRate'], '| pledge:', d['collateralAccountNumber'])"
check 'secured facility approved' Active "$(echo "$APP_OK" | j "['status']")"
check 'secured rate is 12% p.a.' 12.0 "$(echo "$APP_OK" | j "['annualInterestRate']")"
FAC_SEC=$(echo "$APP_OK" | j "['id']")

echo '=== 7. withdrawing below the pledge is rejected (collateral_locked) ==='
# Pledge = 15,000 / 0.9 = 16,666.6667 → withdrawable headroom on savings = 3,333.33
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-wd-1" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":4000,\"description\":\"too much\"}")
check 'over-pledge withdrawal rejected' 422 "$code"

echo '=== 8. withdrawing within free headroom allowed (3,333.33) ==='
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-wd-2" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":3000,\"description\":\"within headroom\"}")
check 'within-headroom withdrawal allowed' 201 "$code"
BAL=$(DB "select balance from accounts where id='$SAV_ID'")
num_eq "$BAL" 17000.0 && check 'savings balance is 17,000.00' ok ok || check 'savings balance is 17,000.00' fail ok

echo '=== 9. transfer out of the collateral account is also locked ==='
code=$(code_of -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-tr-1" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000001\",\"amount\":500,\"description\":\"pledge test\"}")
check 'transfer out of locked collateral rejected' 422 "$code"

echo '=== 10. close facility (balance ≥ 0) → pledge released, withdrawals unlocked ==='
CL=$(curl -s -X POST $BASE/api/overdraft/$FAC_SEC/close -H "Authorization: Bearer $TOK_U6" -H "$CT" -d '{"reason":"test close"}')
check 'facility closed' Closed "$(echo "$CL" | j "['status']")"
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-wd-3" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":4000,\"description\":\"now unfrozen\"}")
check 'after close, larger withdrawal allowed' 201 "$code"

echo '=== 11. loan for late-fee test: apply 2,000 PLN-PER, disburse ==='
LOAN_APP=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK_U6" -H "$CT" -d "{\"loanProductCode\":\"PLN-PER\",\"principal\":2000,\"termMonths\":6,\"purpose\":\"late-fee test\",\"disbursementAccountId\":\"$CURR_ID\"}")
check 'loan approved' Approved "$(echo "$LOAN_APP" | j "['decision']")"
LOAN_ID=$(echo "$LOAN_APP" | j "['loan']['summary']['id']")
LOAN_REF=$(echo "$LOAN_APP" | j "['loan']['summary']['reference']")
curl -s -X POST $BASE/api/loans/$LOAN_ID/accept-and-disburse -H "Authorization: Bearer $TOK_U6" -H "$CT" -d '{}' > /dev/null

echo '=== 12. move installment 1 due date to yesterday; drain repayment account ==='
DB "update loan_installments set due_date = current_date - 1 where loan_id = '$LOAN_ID' and installment_no = 1" > /dev/null
BAL_CURR=$(DB "select balance from accounts where id='$CURR_ID'")
DRAIN=$(python3 -c "print(max(0.0, round(float('$BAL_CURR') - 50, 4)))")
if [ "$(python3 -c "print(1 if float('$DRAIN') > 0 else 0)")" = "1" ]; then
  code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-drain" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"amount\":$DRAIN,\"description\":\"drain for late fee\"}")
  check 'drained for missed-payment test' 201 "$code"
fi

echo '=== 13. run repayments → installment 1 Overdue + late fee posted ==='
RP=$(curl -s -X POST $BASE/api/admin/batch/process-repayments -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"date":"'"$(date -u +%F)"'"}')
echo "$RP"
check 'repayment run succeeded' Succeeded "$(echo "$RP" | j "['status']")"
INST1=$(DB "select status from loan_installments where loan_id = '$LOAN_ID' and installment_no = 1")
check 'installment 1 is Overdue' Overdue "$INST1"
LATE_FEE_TXN=$(DB "select count(*) from transactions where reference like 'LFEE-$LOAN_REF-1-%'")
python3 -c "import sys; sys.exit(0 if int('$LATE_FEE_TXN') >= 1 else 1)" && check 'late-fee transaction posted' ok ok || check 'late-fee transaction posted' fail ok
LATE_FEE_AMT=$(DB "select (select amount from ledger_entries where transaction_id = (select id from transactions where reference like 'LFEE-$LOAN_REF-1-%' order by created_at desc limit 1) limit 1);")
echo "late fee amount: ${LATE_FEE_AMT:-?}"
LATE_FEE_GL=$(DB "select count(*) from gl_journals j join transactions t on t.id = j.transaction_id where t.reference like 'LFEE-$LOAN_REF-1-%'")
python3 -c "import sys; sys.exit(0 if int('$LATE_FEE_GL') >= 1 else 1)" && check 'late fee posted to GL (balanced journal)' ok ok || check 'late fee posted to GL (balanced journal)' fail ok

echo '=== 14. top up, rerun repayments → installment Paid (no second late fee same month) ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_U6" -H "Idempotency-Key: ${RUN_ID}t6-refill-1" -H "$CT" -d "{\"accountId\":\"$CURR_ID\",\"amount\":2000,\"description\":\"refill\"}" > /dev/null
TOMORROW=$(date -u -d '+1 day' +%F)
RP2=$(curl -s -X POST $BASE/api/admin/batch/process-repayments -H "Authorization: Bearer $TOK_A" -H "$CT" -d "{\"date\":\"$TOMORROW\"}")
echo "$RP2"
check 'repayment run 2 succeeded' Succeeded "$(echo "$RP2" | j "['status']")"
INST1B=$(DB "select status from loan_installments where loan_id = '$LOAN_ID' and installment_no = 1")
check 'installment 1 paid after top-up' Paid "$INST1B"
LATE_FEE_TXN2=$(DB "select count(*) from transactions where reference like 'LFEE-$LOAN_REF-1-%'")
check 'exactly one late-fee charge this month' 1 "$LATE_FEE_TXN2"

echo '=== 15. statement shows the late fee ==='
STMT=$(curl -s "$BASE/api/accounts/$CURR_ID/statement?page=1&pageSize=10" -H "Authorization: Bearer $TOK_U6")
check 'late fee in statement' ok "$(echo "$STMT" | python3 -c "import sys,json; print('ok' if any(i['type']=='Fee' and 'Late payment fee' in i['description'] for i in json.load(sys.stdin)['items']) else 'missing')")"

echo '=== 16. GL is consistent — grand total zero, all journals balanced, trial balance OK ==='
GL_SUM=$(DB "select coalesce(sum(amount),0) from gl_entries")
python3 -c "import sys; sys.exit(0 if abs(float('$GL_SUM')) < 0.0001 else 1)" && check 'GL grand total zero' ok ok || check 'GL grand total zero' fail ok
UNBALANCED=$(DB "select count(*) from (select journal_id from gl_entries group by journal_id having sum(amount) <> 0) t")
check 'all journals balanced' 0 "$UNBALANCED"
TB=$(curl -s $BASE/api/admin/gl/trial-balance -H "Authorization: Bearer $TOK_A")
check 'trial balance balanced' true "$(echo "$TB" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"

echo '=== 17. admin portfolio shows the secured facility with pledge ==='
POLL=$(curl -s "$BASE/api/admin/overdraft?status=Closed&page=1&pageSize=10" -H "Authorization: Bearer $TOK_A")
check 'closed secured facility visible' ok "$(echo "$POLL" | python3 -c "import sys,json; print('ok' if any(f['reference'] and f['productCode']=='ODL-SEC' for f in json.load(sys.stdin)['items']) else 'missing')")"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
