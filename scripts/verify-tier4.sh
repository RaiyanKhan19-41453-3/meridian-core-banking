#!/bin/bash
# Tier-4 verification — loan products, origination, amortization, batch repayments.
# Run on a FRESH volume after tiers 1–3 (scripts/verify*.sh) or standalone; it creates its own users.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

RAND=$RANDOM$RANDOM
U4="loanu4_${RAND}@example.com"
echo "subprime user: $U4"

echo '=== admin + demo login ==='
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
TOK_D=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")

echo '=== products on offer ==='
PRODUCTS=$(curl -s $BASE/api/loans/products -H "Authorization: Bearer $TOK_D")
echo "$PRODUCTS" | python3 -m json.tool | head -30
check 'PLN-PER and HML-STD listed' 2 "$(echo "$PRODUCTS" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")"

echo '=== 1. KYC gate blocks unverified users ==='
code=$(code_of -X POST $BASE/api/loans/apply -H "Authorization: Bearer $(curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U4\",\"password\":\"Pass1234!\",\"fullName\":\"Tier Four Subprime\"}" | j "['accessToken']")" -H "$CT" -d '{"loanProductCode":"PLN-PER","principal":5000,"termMonths":12,"purpose":"test","disbursementAccountId":"00000000-0000-0000-0000-000000000000"}')
check 'apply rejected pre-KYC' 403 "$code"

echo '=== 2. admin approves KYC, but exposure + income band too weak for big loan ==='
PENDING=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=10" -H "Authorization: Bearer $TOK_A")
U4_UID=$(echo "$PENDING" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U4'][0])")
TOK_U4=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U4\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $TOK_U4" -H "$CT" -d '{"fullLegalName":"Tier Four Subprime","dateOfBirth":"1993-09-09","nationality":"Kenya","phone":"+254700111222","addressLine":"9 Juja Road","city":"Nairobi","country":"Kenya","idType":"NationalId","idNumber":"KE55667788","employmentStatus":"Employed","annualIncomeBand":"<25k"}' > /dev/null
code=$(code_of -X POST $BASE/api/admin/kyc/$U4_UID/approve -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'admin approves u4' 204 "$code"
SAV_U4=""  # create account requires verified KYC — should work now
SAV_U4=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_U4" -H "$CT" -d '{"type":"Savings","currency":"USD"}' | j "['id']")
APP=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK_U4" -H "$CT" -d "{\"loanProductCode\":\"PLN-PER\",\"principal\":40000,\"termMonths\":24,\"purpose\":\"Too ambitious\",\"disbursementAccountId\":\"$SAV_U4\"}")
echo "$APP" | python3 -c "import sys,json; d=json.load(sys.stdin); print('decision:', d['decision'], '| score:', d['creditScore'], '| reason:', d['reason'])"
check 'big loan rejected' Rejected "$(echo "$APP" | j "['decision']")"
check 'no schedule for rejected loan' 0 "$(echo "$APP" | python3 -c "import sys,json; print(len(json.load(sys.stdin)['loan']['schedule']))")"

echo '=== 3. demo customer applies for a sensible loan → approved ==='
NEW_ACC=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK_D" -H "$CT" -d '{"type":"Savings","currency":"USD"}' | j "['id']")
APP2=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK_D" -H "$CT" -d "{\"loanProductCode\":\"PLN-PER\",\"principal\":5000,\"termMonths\":12,\"purpose\":\"Home renovation\",\"disbursementAccountId\":\"$NEW_ACC\"}")
LOAN_ID=$(echo "$APP2" | j "['loan']['summary']['id']")
check 'demo loan approved' Approved "$(echo "$APP2" | j "['decision']")"
check 'schedule has 12 installments' 12 "$(echo "$APP2" | python3 -c "import sys,json; print(len(json.load(sys.stdin)['loan']['schedule']))")"
check 'installment principals sum to principal' 5000.0 "$(echo "$APP2" | python3 -c "import sys,json; d=json.load(sys.stdin); print(round(sum(i['principal'] for i in d['loan']['schedule']),4))")"
EMI=$(echo "$APP2" | python3 -c "import sys,json; d=json.load(sys.stdin); s=d['loan']['schedule']; print(s[0]['total'])")
echo "EMI: $EMI"

echo '=== 4. accept & disburse (fee deducted, GL posts) ==='
BEFORE=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; print([a['balance'] for a in json.load(sys.stdin) if a['id']=='$NEW_ACC'][0])")
DISB=$(curl -s -X POST $BASE/api/loans/$LOAN_ID/accept-and-disburse -H "Authorization: Bearer $TOK_D" -H "$CT" -d '{}')
AFTER=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; print([a['balance'] for a in json.load(sys.stdin) if a['id']=='$NEW_ACC'][0])")
python3 -c "import sys; sys.exit(0 if abs(($AFTER - $BEFORE) - 4950.0) < 0.001 else 1)" && check 'disbursed 5000, fee 50 deducted' ok ok || check 'disbursed 5000, fee 50 deducted' "$AFTER - $BEFORE" 'expected 4950'
check 'loan Active after disbursement' Active "$(echo "$DISB" | j "['summary']['status']")"

echo '=== 5. disbursement idempotent (retry returns same state) ==='
DISB2=$(curl -s -X POST $BASE/api/loans/$LOAN_ID/accept-and-disburse -H "Authorization: Bearer $TOK_D" -H "$CT" -d '{}')
BAL2=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; print([a['balance'] for a in json.load(sys.stdin) if a['id']=='$NEW_ACC'][0])")
python3 -c "import sys; sys.exit(0 if abs(float('$BAL2') - float('$AFTER')) < 0.001 else 1)" && check 'retry did not double-disburse' ok ok || check 'retry did not double-disburse' fail ok

echo '=== 6. move first installment due date to yesterday, drain account, run repayments → Overdue ==='
DB "update loan_installments set due_date = current_date - 1 where loan_id = '$LOAN_ID' and installment_no = 1" > /dev/null
# drain NEW_ACC below EMI (withdraw everything above 100)
CUR=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; print([a['balance'] for a in json.load(sys.stdin) if a['id']=='$NEW_ACC'][0])")
DRAIN=$(python3 -c "print(round(float('$CUR') - 100, 4))")
curl -s -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t4-drain-1" -H "$CT" -d "{\"accountId\":\"$NEW_ACC\",\"amount\":$DRAIN,\"description\":\"drain for missed-payment test\"}" > /dev/null
RP=$(curl -s -X POST $BASE/api/admin/batch/process-repayments -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"date":"'$(date -u +%F)'"}')
echo "$RP"
check 'repayment run completes' Succeeded "$(echo "$RP" | j "['status']")"
INST1=$(DB "select status from loan_installments where loan_id = '$LOAN_ID' and installment_no = 1")
check 'installment 1 marked Overdue' Overdue "$INST1"

echo '=== 7. fund the account, rerun repayments (next day) → Paid ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t4-refill-1" -H "$CT" -d "{\"accountId\":\"$NEW_ACC\",\"amount\":2000,\"description\":\"refill for repayment\"}" > /dev/null
TOMORROW=$(date -u -d '+1 day' +%F)
RP2=$(curl -s -X POST $BASE/api/admin/batch/process-repayments -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"date":"'$TOMORROW'"}')
echo "$RP2"
check 'repayment run 2 completes' Succeeded "$(echo "$RP2" | j "['status']")"
INST1B=$(DB "select status from loan_installments where loan_id = '$LOAN_ID' and installment_no = 1")
check 'installment 1 now Paid' Paid "$INST1B"

echo '=== 8. loan repayment posts double-entry + GL journal balanced ==='
UNBALANCED=$(DB "select count(*) from (select journal_id from gl_entries group by journal_id having sum(amount) <> 0) t")
check 'all GL journals balanced' 0 "$UNBALANCED"
GRAND=$(DB "select coalesce(sum(amount),0) from gl_entries")
python3 -c "import sys; sys.exit(0 if abs(float('$GRAND')) < 0.0001 else 1)" && check 'GL grand total zero' ok ok || check 'GL grand total zero' fail ok
TB=$(curl -s $BASE/api/admin/gl/trial-balance -H "Authorization: Bearer $TOK_A")
check 'trial balance balanced' true "$(echo "$TB" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"

echo '=== 9. statement shows disbursement, fee and repayment ==='
STMT=$(curl -s "$BASE/api/accounts/$NEW_ACC/statement?page=1&pageSize=20" -H "Authorization: Bearer $TOK_D")
echo "$STMT" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(i['type'], i['amount'], '|', i['description']) for i in d['items']]"
for T in LoanDisbursement LoanRepayment Fee; do
  check "statement contains $T" ok "$(echo "$STMT" | python3 -c "import sys,json; print('ok' if any(i['type']=='$T' for i in json.load(sys.stdin)['items']) else 'missing')")"
done

echo '=== 10. admin portfolio view ==='
PORT=$(curl -s "$BASE/api/admin/loans?status=Active&page=1&pageSize=10" -H "Authorization: Bearer $TOK_A")
check 'loan visible in portfolio' 1 "$(echo "$PORT" | python3 -c "import sys,json; print(sum(1 for l in json.load(sys.stdin)['items'] if l['reference'].startswith('LN-')))")"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
