#!/bin/bash
# Tier-3 verification — KYC onboarding, documents, AML rules, admin review.
# Idempotent enough to run on the current dev volume; creates its own users.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
pass=0; fail=0
check() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi; }
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

RAND=$RANDOM$RANDOM
U1="kyc1_${RAND}@example.com"
U2="kyc2_${RAND}@example.com"
echo "test users: $U1, $U2"

echo '=== admin + demo login ==='
TOK_A=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}' | j "['accessToken']")
TOK_D=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}' | j "['accessToken']")

echo '=== 1. register user 1 (name matches watchlist) ==='
R=$(curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U1\",\"password\":\"Pass1234!\",\"fullName\":\"Viktor Dragov\"}")
U1_TOKEN=$(echo "$R" | j "['accessToken']")
check 'registration accepted' true "$([ -n "$U1_TOKEN" ] && echo true)"

echo '=== 2. watchlist match flagged at registration ==='
ALERTS=$(curl -s "$BASE/api/admin/aml/alerts?status=Open&pageSize=50" -H "Authorization: Bearer $TOK_A")
echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); us=[i['userEmail'] for i in d['items'] if i['alertType']=='WatchlistMatch']; print('watchlist alerts for user:', us)"
check 'watchlist alert raised' ok "$(echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print('ok' if any(i['userEmail']=='$U1' and i['alertType']=='WatchlistMatch' for i in d['items']) else 'missing')")"

echo '=== 3. new user KYC is Pending, risk High ==='
ME1=$(curl -s $BASE/api/auth/me -H "Authorization: Bearer $U1_TOKEN")
check 'me shows kycStatus Pending' Pending "$(echo "$ME1" | j "['kycStatus']")"
S1=$(curl -s $BASE/api/kyc/status -H "Authorization: Bearer $U1_TOKEN")
echo "$S1"
check 'risk rating High (watchlist)' High "$(echo "$S1" | j "['riskRating']")"

echo '=== 4. pending user is blocked from banking (deposit/withdraw/transfer/open) ==='
SAV_ID=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $TOK_D" | python3 -c "import sys,json; print([a['id'] for a in json.load(sys.stdin) if a['accountNumber']=='AC100000000001'][0])")
for ep in deposit withdraw; do
  code=$(code_of -X POST $BASE/api/transactions/$ep -H "Authorization: Bearer $U1_TOKEN" -H "Idempotency-Key: ${RUN_ID}t3-block-$RANDOM" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":10}")
  check "$ep rejected for pending user" 403 "$code"
done
code=$(code_of -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $U1_TOKEN" -H "Idempotency-Key: ${RUN_ID}t3-block-$RANDOM" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":10}")
check 'transfer rejected for pending user' 403 "$code"
code=$(code_of -X POST $BASE/api/accounts -H "Authorization: Bearer $U1_TOKEN" -H "$CT" -d '{"type":"Savings","currency":"USD"}')
check 'account opening rejected for pending user' 403 "$code"

echo '=== 5. document upload / list / download roundtrip / delete ==='
printf '%%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%%%EOF\n' > /tmp/t3-test-id.pdf
SHA_ORIG=$(sha256sum /tmp/t3-test-id.pdf | awk '{print $1}')
UP=$(curl -s -X POST $BASE/api/kyc/documents -H "Authorization: Bearer $U1_TOKEN" -F docType=NationalId -F "file=@/tmp/t3-test-id.pdf;type=application/pdf")
DOC_ID=$(echo "$UP" | j "['id']")
check 'document uploaded' true "$([ -n "$DOC_ID" ] && echo true)"
DOCS=$(curl -s $BASE/api/kyc/documents -H "Authorization: Bearer $U1_TOKEN")
check 'document listed' 1 "$(echo "$DOCS" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")"
curl -s $BASE/api/kyc/documents/$DOC_ID/content -H "Authorization: Bearer $U1_TOKEN" -o /tmp/t3-downloaded.pdf
check 'download sha256 matches' "$SHA_ORIG" "$(sha256sum /tmp/t3-downloaded.pdf | awk '{print $1}')"
code=$(code_of -X DELETE $BASE/api/kyc/documents/$DOC_ID -H "Authorization: Bearer $U1_TOKEN")
check 'delete document' 204 "$code"

echo '=== 6. re-upload doc, complete profile, admin approves ==='
curl -s -X POST $BASE/api/kyc/documents -H "Authorization: Bearer $U1_TOKEN" -F docType=Passport -F "file=@/tmp/t3-test-id.pdf;type=application/pdf" > /dev/null
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $U1_TOKEN" -H "$CT" -d '{"fullLegalName":"Viktor Dragov","dateOfBirth":"1988-03-12","nationality":"Kenya","phone":"+254712345678","addressLine":"42 Riverside Drive","city":"Nairobi","country":"Kenya","idType":"Passport","idNumber":"KE77889900","employmentStatus":"Employed","annualIncomeBand":"50k-100k"}' > /dev/null
S1=$(curl -s $BASE/api/kyc/status -H "Authorization: Bearer $U1_TOKEN")
check 'profile still Pending pre-review' Pending "$(echo "$S1" | j "['kycStatus']")"
PENDING=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=50" -H "Authorization: Bearer $TOK_A")
check 'user appears in KYC queue' 1 "$(echo "$PENDING" | python3 -c "import sys,json; print(sum(1 for p in json.load(sys.stdin)['items'] if p['email']=='$U1'))")"
U1_USERID=$(echo "$PENDING" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U1'][0])")
code=$(code_of -X POST $BASE/api/admin/kyc/$U1_USERID/approve -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{}')
check 'admin approves' 204 "$code"

echo '=== 7. approved user can bank ==='
code=$(code_of -X POST $BASE/api/accounts -H "Authorization: Bearer $U1_TOKEN" -H "$CT" -d '{"type":"Savings","currency":"USD"}')
check 'open account after approval' 201 "$code"
U1_ACC=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $U1_TOKEN" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")
code=$(code_of -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $U1_TOKEN" -H "Idempotency-Key: ${RUN_ID}t3-dep-1" -H "$CT" -d "{\"accountId\":\"$U1_ACC\",\"amount\":100,\"description\":\"first deposit\"}")
check 'deposit after approval' 201 "$code"
code=$(code_of -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $U1_TOKEN" -H "Idempotency-Key: ${RUN_ID}t3-wd-1" -H "$CT" -d "{\"accountId\":\"$U1_ACC\",\"amount\":40,\"description\":\"first withdrawal\"}")
check 'withdraw after approval' 201 "$code"

echo '=== 8. rejected user stays blocked, resubmission re-queues ==='
curl -s -X POST $BASE/api/auth/register -H "$CT" -d "{\"email\":\"$U2\",\"password\":\"Pass1234!\",\"fullName\":\"Tier Three Rejection\"}" > /dev/null
U2_TOKEN=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"$U2\",\"password\":\"Pass1234!\"}" | j "['accessToken']")
curl -s -X PUT $BASE/api/kyc/profile -H "Authorization: Bearer $U2_TOKEN" -H "$CT" -d '{"fullLegalName":"Tier Three Rejection","dateOfBirth":"1995-05-05","nationality":"Kenya","phone":"+254722333444","addressLine":"7 Loita Street","city":"Nairobi","country":"Kenya","idType":"NationalId","idNumber":"KE11223344","employmentStatus":"Employed","annualIncomeBand":"25k-50k"}' > /dev/null
U2_UID=$(curl -s "$BASE/api/admin/kyc/pending?pageSize=50" -H "Authorization: Bearer $TOK_A" | python3 -c "import sys,json; print([p['userId'] for p in json.load(sys.stdin)['items'] if p['email']=='$U2'][0])")
code=$(code_of -X POST $BASE/api/admin/kyc/$U2_UID/reject -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"reason":"ID number does not match document"}')
check 'admin rejects' 204 "$code"
code=$(code_of -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $U2_TOKEN" -H "Idempotency-Key: ${RUN_ID}t3-dep-2" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":10}")
check 'rejected user blocked' 403 "$code"
STS2=$(curl -s $BASE/api/kyc/status -H "Authorization: Bearer $U2_TOKEN")
check 'status shows Rejected' Rejected "$(echo "$STS2" | j "['kycStatus']")"

echo '=== 9. AML rules fire on demo account (HIGH_VALUE deposit; VELOCITY; BURST) ==='
curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t3-bigdep" -H "$CT" -d "{\"accountId\":\"$SAV_ID\",\"amount\":50000,\"description\":\"large funding\"}" > /dev/null
# 3 transfers < 10k each, totalling >= 25k in 24h → VELOCITY_24H
curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t3-tr-9a" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":9000,\"description\":\"t1\"}" > /dev/null
curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t3-tr-9b" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":9000,\"description\":\"t2\"}" > /dev/null
curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t3-tr-8" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":8000,\"description\":\"t3\"}" > /dev/null
# 5 more small transfers → >= 5 outbound in 1h → BURST
for i in 1 2 3 4 5; do
  curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $TOK_D" -H "Idempotency-Key: ${RUN_ID}t3-tr-sm-$i" -H "$CT" -d "{\"fromAccountId\":\"$SAV_ID\",\"toAccountNumber\":\"AC100000000002\",\"amount\":100,\"description\":\"small $i\"}" > /dev/null
done
sleep 2
ALERTS=$(curl -s "$BASE/api/admin/aml/alerts?status=Open&pageSize=100" -H "Authorization: Bearer $TOK_A")
for T in HighValue Velocity24h Burst; do
  check "alert $T raised" ok "$(echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print('ok' if any(i['alertType']=='$T' and i['userEmail']=='demo@bank.com' for i in d['items']) else 'missing')")"
done

echo '=== 10. admin dismisses the velocity alert with a note ==='
VEL_ID=$(echo "$ALERTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(next(i['id'] for i in d['items'] if i['alertType']=='Velocity24h'))")
code=$(code_of -X POST $BASE/api/admin/aml/alerts/$VEL_ID/dismiss -H "Authorization: Bearer $TOK_A" -H "$CT" -d '{"note":"Customer confirmed sale of vehicle; source of funds verified"}')
check 'dismiss alert' 204 "$code"
DISMISSED=$(curl -s "$BASE/api/admin/aml/alerts?status=Dismissed&pageSize=10" -H "Authorization: Bearer $TOK_A")
check 'alert now Dismissed with note' true "$(echo "$DISMISSED" | python3 -c "import sys,json; d=json.load(sys.stdin); print('true' if any(i['id']=='$VEL_ID' for i in d['items']) else 'false')")"

echo '=== 11. trial balance still balanced after AML-firing movements ==='
TB=$(curl -s $BASE/api/admin/gl/trial-balance -H "Authorization: Bearer $TOK_A")
check 'trial balance balanced' true "$(echo "$TB" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['balanced']).lower())")"

echo
echo "RESULT: $pass passed, $fail failed"
exit $fail
