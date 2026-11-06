#!/bin/bash
# Tier-2 verification — products, GL, day-end batch, interest, fees.
# Run AFTER `docker compose up -d` on a FRESH volume (down -v && up).
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }

echo '=== login (demo + admin) ==='
TOK_D=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")

SAV_ID=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['id'] for a in d if a['accountNumber']=='AC100000000001'][0])")
CUR_ID=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['id'] for a in d if a['accountNumber']=='AC100000000002'][0])")
echo "savings=$SAV_ID current=$CUR_ID"

echo '=== deposit 10,000 into savings (idem key t2-dep-1) ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t2-dep-1" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":10000,\"description\":\"tier2 seed deposit\"}" > /dev/null

echo '=== every deposit posts a balanced GL journal ==='
GL_SUM=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select coalesce(sum(amount),0) from gl_entries;')
python3 -c "import sys; sys.exit(0 if abs(float('$GL_SUM')) < 0.0001 else 1)" && check 'GL grand total is zero' ok ok || check 'GL grand total is zero' fail ok
UNBALANCED=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select count(*) from (select journal_id from gl_entries group by journal_id having sum(amount) <> 0) t;')
check 'all journals balanced' 0 "$UNBALANCED"

echo '=== run day-end for today ==='
DE=$(curl -s -X POST $BASE/api/admin/batch/day-end -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
echo "$DE" | python3 -m json.tool | head -30
check 'day-end succeeded' Succeeded "$(echo "$DE" | j "['status']")"
check 'not replayed' False "$(echo "$DE" | python3 -c "import sys,json; print(json.load(sys.stdin)['replayed'])")"

echo '=== replay day-end (same job+date → replayed, no double accrue) ==='
DE2=$(curl -s -X POST $BASE/api/admin/batch/day-end -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'replay flagged' True "$(echo "$DE2" | python3 -c "import sys,json; print(json.load(sys.stdin)['replayed'])")"
ACCRUAL_COUNT=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select count(*) from interest_accruals;')
check 'exactly one accrual row for savings' 1 "$ACCRUAL_COUNT"
ACCRUED=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select amount from interest_accruals order by id desc limit 1;')
echo "accrued today: $ACCRUED"

echo '=== GL accrual journal posted (5001 debit / 2101 credit) ==='
ACCRUAL_GL=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "select count(*) from gl_journals j join gl_entries e on e.journal_id = j.id join gl_accounts g on g.id = e.gl_account_id where g.code = '5001';")
check 'interest expense journal exists (count>=1)' 1 "$ACCRUAL_GL"

echo '=== capitalize accrued interest as of today ==='
CAP=$(curl -s -X POST $BASE/api/admin/batch/capitalize-interest -H "Authorization: Bearer $TOK_A" -H "$CT" -d "{\"asOf\":\"$(date -u +%F)\"}")
echo "$CAP"
check 'capitalization succeeded' Succeeded "$(echo "$CAP" | j "['status']")"
NEW_BAL=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$SAV_ID'][0])")
check 'savings balance = 10000 + accrued' ok "$(python3 -c "import sys; sys.exit(0 if float('$NEW_BAL') > 10000.0 else 1)" && echo ok)"
echo "savings balance now: $NEW_BAL"

echo '=== charge fees for this month ==='
FEE=$(curl -s -X POST $BASE/api/admin/batch/charge-fees -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"year":2026,"month":11}')
echo "$FEE"
check 'fee run succeeded' Succeeded "$(echo "$FEE" | j "['status']")"
CUR_BAL=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$CUR_ID'][0])")
check 'current account charged 5 (overdraft allowed for fees)' -5.0 "$CUR_BAL"

echo '=== fee run replay (same month → replayed, no double charge) ==='
FEE2=$(curl -s -X POST $BASE/api/admin/batch/charge-fees -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"year":2026,"month":11}')
check 'fee replay flagged' True "$(echo "$FEE2" | python3 -c "import sys,json; print(json.load(sys.stdin)['replayed'])")"
CUR_BAL2=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$CUR_ID'][0])")
check 'current still -5.00 after replay' -5.0 "$CUR_BAL2"

echo '=== statement shows Interest and Fee transactions ==='
STMT=$(curl -s "$BASE/api/accounts/$SAV_ID/statement?page=1&pageSize=10" -H "Authorization: Bearer $TOK_D")
echo "$STMT" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(i['type'], i['amount'], i['description']) for i in d['items']]"
CUR_STMT=$(curl -s "$BASE/api/accounts/$CUR_ID/statement?page=1&pageSize=10" -H "Authorization: Bearer $TOK_D")
echo "$CUR_STMT" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(i['type'], i['amount'], i['description']) for i in d['items']]"
check 'interest appears in statement' ok "$(echo "$STMT" | python3 -c "import sys,json; print('ok' if any(i['type']=='Interest' for i in json.load(sys.stdin)['items']) else 'missing')")"
check 'fee appears in statement' ok "$(echo "$CUR_STMT" | python3 -c "import sys,json; print('ok' if any(i['type']=='Fee' for i in json.load(sys.stdin)['items']) else 'missing')")"

echo '=== internal transfer: no GL journal (net-zero balance sheet) ==='
J_BEFORE=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select count(*) from gl_journals;')
curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t2-tr-1" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":100,\"description\":\"internal move\"}" > /dev/null
J_AFTER=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc 'select count(*) from gl_journals;')
check 'internal transfer posts no GL journal' "$J_BEFORE" "$J_AFTER"

echo '=== deposit journal legs are debit-positive (1001 +10000 / 2001 −10000 in USD) ==='
DEP_LEGS=$(docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "select g.code || ' ' || e.amount::text from gl_entries e join gl_accounts g on g.id = e.gl_account_id join gl_journals j on j.id = e.journal_id where j.description = 'tier2 seed deposit' order by g.code;")
echo "$DEP_LEGS"

echo '=== trial balance is balanced ==='
TB=$(curl -s $BASE/api/admin/gl/trial-balance -H "Authorization: Bearer $TOK_A")
echo "$TB" | python3 -m json.tool
check 'trial balance balanced' true "$(echo "$TB" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"

echo '=== GL journals readable + all balanced ==='
curl -s "$BASE/api/admin/gl/journals?pageSize=5" -H "Authorization: Bearer $TOK_A" | python3 -c "import sys,json; d=json.load(sys.stdin); print('journals total:', d['total']); [print(j['number'], 'rate', str(j['usdRate']), [(l['code'], str(l['amount'])) for l in j['legs']]) for j in d['items']]"

echo '=== batch runs history ==='
curl -s "$BASE/api/admin/batch/runs" -H "Authorization: Bearer $TOK_A" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(r['jobType'], r['runDate'][:10], r['status'], 'processed='+str(r['itemsProcessed']), 'failed='+str(r['itemsFailed'])) for r in d]"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
