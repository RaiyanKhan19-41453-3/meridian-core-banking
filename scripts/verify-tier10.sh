#!/bin/bash
# Tier-10 verification — roadmap item 14: CTR/SAR, filings, tax statements,
# reconciliation and sanctions-list refresh. Requires a FRESH volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
DB() { docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "$1"; }
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }

TOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")
ADM=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
SAV=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK" | python3 -c "import sys,json; print([a['id'] for a in json.load(sys.stdin) if a['accountNumber']=='AC100000000001'][0])")

echo '=== 1. large deposit creates a HighValue CTR candidate ==='
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c10-big" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":50000,\"description\":\"large deposit\"}"
CTR=$(curl -s $BASE/api/compliance/ctr -H "Authorization: Bearer $ADM")
NCTR=$(echo "$CTR" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")
python3 -c "import sys; sys.exit(0 if int('$NCTR')>=1 else 1)" && check 'CTR reports at least one alert' ok ok || check 'CTR reports at least one alert' fail ok
check 'CTR category' CTR "$(echo "$CTR" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['category'])")"

echo '=== 2. large withdrawal creates a high-severity SAR candidate ==='
curl -s -o /dev/null -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c10-out" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":30000,\"description\":\"large outflow\"}"
SAR=$(curl -s $BASE/api/compliance/sar -H "Authorization: Bearer $ADM")
NSAR=$(echo "$SAR" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")
python3 -c "import sys; sys.exit(0 if int('$NSAR')>=1 else 1)" && check 'SAR reports at least one alert' ok ok || check 'SAR reports at least one alert' fail ok
check 'SAR category' SAR "$(echo "$SAR" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['category'])")"

echo '=== 3. filing an alert is recorded and idempotent ==='
AID=$(echo "$CTR" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")
R1=$(curl -s -X POST $BASE/api/compliance/alerts/$AID/file -H "Authorization: Bearer $ADM" -H "$CT" -d '{"reference":"CTR-TEST-001"}')
check 'first filing succeeds' true "$(echo "$R1" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['filed']).lower())")"
R2=$(curl -s -X POST $BASE/api/compliance/alerts/$AID/file -H "Authorization: Bearer $ADM" -H "$CT" -d '{"reference":"CTR-TEST-001"}')
check 're-filing is idempotent' false "$(echo "$R2" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['filed']).lower())")"
FR=$(curl -s $BASE/api/compliance/ctr -H "Authorization: Bearer $ADM" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['filedReference'] for a in d if a['id']=='$AID'][0])")
check 'filing reference retained' CTR-TEST-001 "$FR"

# Concurrent filings must produce exactly one filing, not five audit rows.
curl -s -o /dev/null -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK" -H "Idempotency-Key: ${RUN_ID}c10-big2" -H "$CT" -d "{\"accountId\":\"$SAV\",\"amount\":20000,\"description\":\"another large deposit\"}"
AID2=$(DB "select id from aml_alerts where alert_type='HighValue' and filed_at is null order by created_at desc limit 1")
for i in 1 2 3 4 5; do
  curl -s -o /tmp/c10-f-$i.json -X POST $BASE/api/compliance/alerts/$AID2/file -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"reference\":\"${RUN_ID}-F$i\"}" &
done
wait
SUCCESS=$(python3 -c "
import json
print(sum(1 for i in range(1,6) if json.load(open(f'/tmp/c10-f-{i}.json')).get('filed')))")
check 'concurrent filings: exactly one winner' 1 "$SUCCESS"
AUD=$(DB "select count(*) from audit_logs where action='compliance.filed' and entity_id='$AID2'")
check 'concurrent filings: one audit row' 1 "$AUD"

echo '=== 4. customers cannot read CTR; admin only ==='
code=$(curl -s -o /dev/null -w '%{http_code}' $BASE/api/compliance/ctr -H "Authorization: Bearer $TOK")
check 'CTR forbidden for customer' 403 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' $BASE/api/compliance/reconciliation -H "Authorization: Bearer $TOK")
check 'reconciliation forbidden for customer' 403 "$code"

echo '=== 5. reconciliation is balanced and pairs subledger with GL ==='
REC=$(curl -s $BASE/api/compliance/reconciliation -H "Authorization: Bearer $ADM")
check 'reconciliation balanced overall' true "$(echo "$REC" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"
check 'no missing GL journals' 0 "$(echo "$REC" | python3 -c "import sys,json; print(json.load(sys.stdin)['missingGlJournals'])")"
check 'no orphan GL journals' 0 "$(echo "$REC" | python3 -c "import sys,json; print(json.load(sys.stdin)['orphanGlJournals'])")"

echo '=== 6. tax statement covers interest earned ==='
TODAY=$(date -u +%F)
curl -s -o /dev/null -X POST $BASE/api/admin/batch/day-end -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"date\":\"$TODAY\"}"
curl -s -o /dev/null -X POST $BASE/api/admin/batch/capitalize-interest -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"asOf\":\"$TODAY\"}"
TAX=$(curl -s "$BASE/api/compliance/tax-statement?year=$(date -u +%Y)" -H "Authorization: Bearer $ADM")
EARNED=$(echo "$TAX" | python3 -c "import sys,json; d=json.load(sys.stdin); print(sum(a['lines'][0]['interestEarned'] for a in d))")
python3 -c "import sys; sys.exit(0 if float('$EARNED')>0 else 1)" && check 'tax statement shows interest earned' ok ok || check 'tax statement shows interest earned' fail "$EARNED"
NACCT=$(echo "$TAX" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")
python3 -c "import sys; sys.exit(0 if int('$NACCT')<=6 else 1)" && check 'tax statement excludes system accounts' ok ok || check 'tax statement excludes system accounts' fail "$NACCT"

# A transaction posted later on Dec 31 used to fall outside the inclusive
# year-end bound and disappear from the tax statement.
Y=$(date -u +%Y)
DB "update transactions set created_at='$Y-12-31 12:00:00+00' where type='Interest' and id=(select id from transactions where type='Interest' order by created_at desc limit 1)" > /dev/null
DB "update ledger_entries set posted_at='$Y-12-31 12:00:00+00' where transaction_id=(select id from transactions where type='Interest' order by created_at desc limit 1)" > /dev/null
TAX2=$(curl -s "$BASE/api/compliance/tax-statement?year=$Y" -H "Authorization: Bearer $ADM")
EARNED2=$(echo "$TAX2" | python3 -c "import sys,json; d=json.load(sys.stdin); print(sum(a['lines'][0]['interestEarned'] for a in d))")
python3 -c "import sys; sys.exit(0 if float('$EARNED2')>0 else 1)" && check 'Dec 31 afternoon interest included in tax statement' ok ok || check 'Dec 31 afternoon interest included in tax statement' fail "$EARNED2"

echo '=== 7. sanctions-list refresh is idempotent and screened ==='
NAME="Sanction Target ${RUN_ID}"
WL=$(curl -s -X POST $BASE/api/compliance/watchlist/refresh -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"entries\":[{\"fullName\":\"$NAME\",\"source\":\"OFAC (demo)\"}]}")
check 'watchlist entry added' 1 "$(echo "$WL" | python3 -c "import sys,json; print(json.load(sys.stdin)['added'])")"
WL2=$(curl -s -X POST $BASE/api/compliance/watchlist/refresh -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"entries\":[{\"fullName\":\"$NAME\",\"source\":\"OFAC (demo)\"}]}")
check 'duplicate watchlist entry skipped' 1 "$(echo "$WL2" | python3 -c "import sys,json; print(json.load(sys.stdin)['skipped'])")"
CNAME="Concurrent ${RUN_ID}"
for i in 1 2 3 4 5; do
  curl -s -o /dev/null -X POST $BASE/api/compliance/watchlist/refresh -H "Authorization: Bearer $ADM" -H "$CT" -d "{\"entries\":[{\"fullName\":\"$CNAME\",\"source\":\"OFAC (demo)\"}]}" &
done
wait
CCOUNT=$(DB "select count(*) from watchlist_entries where full_name='$CNAME'")
check 'concurrent refresh creates one entry' 1 "$CCOUNT"
U="c10_${RUN_ID}@example.com"
curl -s -o /dev/null -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U\",\"password\":\"Pass1234!\",\"fullName\":\"Sanction Target\"}"
UTOK=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
curl -s -o /dev/null -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $UTOK" -H "$CT" -d "{\"fullLegalName\":\"$NAME\",\"dateOfBirth\":\"1990-01-01\",\"nationality\":\"US\",\"phone\":\"+15550000000\",\"addressLine\":\"1 Main St\",\"city\":\"NY\",\"country\":\"US\",\"idType\":\"Passport\",\"idNumber\":\"X123\",\"employmentStatus\":\"Employed\",\"annualIncomeBand\":\"50k-100k\"}"
RISK=$(curl -s $BASE/api/kyc/profile -H "Authorization: Bearer $UTOK" | j "['riskRating']")
check 'refreshed sanctions entry is screened' High "$RISK"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
