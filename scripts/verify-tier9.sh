#!/bin/bash
# Tier-9 verification — roadmap item 12: fee waiver tiers, ODL-TD,
# automatic limit review, loan penalty rates. Requires a FRESH volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
num_eq() { python3 -c "import sys; sys.exit(0 if abs(float(sys.argv[1])-float(sys.argv[2]))<0.01 else 1)" "$1" "$2"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")
ADM=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")

echo '=== 1. tiered fee waiver: half fee between 500 and 1000 ==='
ACCS=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK")
CUR=$(echo "$ACCS" | python3 -c "import sys,json; print([a['id'] for a in json.load(sys.stdin) if a['accountNumber']=='AC100000000002'][0])")
SAV=$(echo "$ACCS" | python3 -c "import sys,json; print([a['id'] for a in json.load(sys.stdin) if a['accountNumber']=='AC100000000001'][0])")
# Fund the Current account to 600 (above reduced tier 500, below full waiver 1000)
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t9-fund" -H "$CT" -d "{\"accountId\":\"$CUR\",\"amount\":600,\"description\":\"fund\"}"
M=$(date -u +%-m); Y=$(date -u +%Y)
curl -s -o /dev/null -X POST $BASE/api/admin/batch/charge-fees -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"year\":$Y,\"month\":$M}"
FEE=$(DB "select abs(amount) from ledger_entries l join transactions t on t.id=l.transaction_id where l.account_id='$CUR' and t.reference like 'FEE-%' and l.amount<0")
num_eq "$FEE" 2.5 && check 'half monthly fee at 600 balance' ok ok || check 'half monthly fee at 600 balance' fail "$FEE"

echo '=== 2. term-deposit account opens with TD product ==='
TD=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $TOK" -H "$CT" -d '{"type":"TermDeposit","currency":"USD"}' | j "['id']")
TDPROD=$(DB "select p.code from accounts a join products p on p.id=a.product_id where a.id='$TD'")
check 'TD account uses TD-STD product' TD-STD "$TDPROD"
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t9-td-fund" -H "$CT" -d "{\"accountId\":\"$TD\",\"amount\":20000,\"description\":\"TD fund\"}"

echo '=== 3. ODL-TD facility backed by term deposit ==='
APP=$(curl -s -X POST $BASE/api/overdraft/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"productCode\":\"ODL-TD\",\"accountId\":\"$CUR\",\"collateralAccountId\":\"$TD\",\"requestedLimit\":10000,\"purpose\":\"TD OD\"}")
check 'ODL-TD approved' Active "$(echo "$APP" | j "['status']")"
check 'ODL-TD rate 9%' 9.0 "$(echo "$APP" | j "['annualInterestRate']")"
FID=$(echo "$APP" | j "['id']")
COLL=$(DB "select collateral_account_id from overdraft_facilities where id='$FID'")
check 'collateral linked to TD account' "$TD" "$COLL"

echo '=== 4. pledged term deposit cannot be drawn below the lock ==='
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t9-td-draw" -H "$CT" -d "{\"accountId\":\"$TD\",\"amount\":15000,\"description\":\"break pledge\"}")
check 'withdraw below pledge rejected' 422 "$code"
RESP=$(curl -s -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t9-td-draw2" -H "$CT" -d "{\"accountId\":\"$TD\",\"amount\":15000,\"description\":\"break pledge\"}")
check 'collateral_locked error code' collateral_locked "$(echo "$RESP" | j "['code']")"

echo '=== 5. automatic limit review reduces unused limits once per month ==='
L0=$(DB "select f.\"limit\" from overdraft_facilities f where id='$FID'")
RV=$(curl -s -X POST $BASE/api/admin/batch/review-limits -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"year\":$Y,\"month\":$M}")
check 'review run succeeded' Succeeded "$(echo "$RV" | j "['status']")"
L1=$(DB "select f.\"limit\" from overdraft_facilities f where id='$FID'")
python3 -c "import sys; sys.exit(0 if abs(float('$L1')-float('$L0')*0.9)<0.01 else 1)" && check 'unused limit reduced by 10%' ok ok || check 'unused limit reduced by 10%' fail "$L0 -> $L1"
RV2=$(curl -s -X POST $BASE/api/admin/batch/review-limits -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"year\":$Y,\"month\":$M}")
check 'second review replayed' True "$(echo "$RV2" | python3 -c "import sys,json; print(json.load(sys.stdin)['replayed'])")"
L2=$(DB "select f.\"limit\" from overdraft_facilities f where id='$FID'")
check 'limit unchanged on replay' "$L1" "$L2"

echo '=== 6. loan penalty rate increases the late fee above the flat $25 ==='
LOAN=$(curl -s -X POST $BASE/api/loans/apply -H "Authorization: Bearer $TOK" -H "$CT" -d "{\"loanProductCode\":\"PLN-PER\",\"principal\":1000,\"termMonths\":6,\"purpose\":\"penalty\",\"disbursementAccountId\":\"$SAV\"}" | j "['loan']['summary']['id']")
curl -s -o /dev/null -X POST $BASE/api/loans/$LOAN/accept-and-disburse -H "Authorization: Bearer $TOK" -H "$CT" -d '{}'
DB "update loan_installments set due_date=current_date-1 where loan_id='$LOAN' and installment_no=1" > /dev/null
BAL=$(DB "select balance from accounts where id='$SAV'")
curl -s -o /dev/null -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}t9-drain" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":$BAL,\"description\":\"drain\"}"
curl -s -o /dev/null -X POST $BASE/api/admin/batch/process-repayments -H "Authorization: Bearer $ADM" -H "$CT" -d '{"date":"'$(date -u +%F)'"}'
LFEE=$(DB "select abs(amount) from ledger_entries l join transactions t on t.id=l.transaction_id where l.account_id='$SAV' and t.reference like 'LFEE-%' and l.amount<0 order by l.id desc limit 1")
python3 -c "import sys; sys.exit(0 if float('$LFEE') > 25 else 1)" && check 'late fee includes penalty above $25' ok ok || check 'late fee includes penalty above $25' fail "$LFEE"
PDESC=$(DB "select description from transactions where reference like 'LFEE-%' order by created_at desc limit 1")
check 'late fee describes penalty component' true "$(echo "$PDESC" | grep -q 'penalty' && echo true)"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
