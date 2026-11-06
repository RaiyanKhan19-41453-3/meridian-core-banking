#!/bin/bash
# End-to-end smoke/security suite — run AFTER `docker compose up -d`.
# NOTE: clears demo@bank.com lockout at the end via docker exec. Destructive to demo data: run on a fresh volume.
set -e
RUN_ID=r$RANDOM$RANDOM
BASE=http://localhost:8090
CT='Content-Type: application/json'
j() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)" 2>/dev/null; }
pass=0; fail=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "PASS  $1"; else fail=$((fail+1)); echo "FAIL  $1 (expected=$2 got=$3)"; fi
}

# ---------- preflight: this suite asserts exact postings, so it needs a fresh volume ----------
if docker exec corebanking-postgres psql -U corebanking -d corebanking -tAc "select count(*) from transactions where reference like '%dep-verify-1'" 2>/dev/null | grep -vq '^0$'; then
  echo 'ERROR: this database already has test data (dep-verify-1 exists).'
  echo 'Run:  docker compose down -v && docker compose up -d   then re-run this suite.'
  exit 2
fi

echo '=== 1. demo login ==='
LOGIN=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}')
AT=$(echo "$LOGIN" | j "['accessToken']"); RT=$(echo "$LOGIN" | j "['refreshToken']")
check 'login returns access token' true "$([ -n "$AT" ] && echo true)"
echo "accessTokenExpiresAtUtc: $(echo "$LOGIN" | j "['accessTokenExpiresAtUtc']")"

echo '=== 2. me ==='
curl -s $BASE/api/auth/me -H "Authorization: Bearer $AT" | head -c 300; echo

ACC_ID=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['id']")
echo "savings account id: $ACC_ID"

echo '=== 3. deposit 1000 (idempotent) ==='
BAL0=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['balance']")
D1=$(curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}dep-verify-1" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":1000,\"description\":\"test deposit\"}")
echo "$D1"
BAL1=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['balance']")
python3 -c "import sys; sys.exit(0 if abs(float('$BAL1') - float('$BAL0') - 1000.0) < 0.0001 else 1)" && check 'balance increased by exactly 1000' ok ok || check 'balance increased by exactly 1000' fail ok

echo '=== 4. replay same key + payload (must NOT double-credit) ==='
D2=$(curl -s -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}dep-verify-1" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":1000,\"description\":\"test deposit\"}")
RPL=$(echo "$D2" | python3 -c "import sys,json; print(str(json.load(sys.stdin)['replayed']).lower())"); check 'replay returns replayed=true' true "$RPL"
BAL2=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['balance']")
python3 -c "import sys; sys.exit(0 if abs(float('$BAL2') - float('$BAL1')) < 0.0001 else 1)" && check 'balance unchanged after replay' ok ok || check 'balance unchanged after replay' fail ok

echo '=== 5. same key, different payload → 409 ==='
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}dep-verify-1" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":999,\"description\":\"different\"}")
check 'mismatched replay rejected' 409 "$code"

echo '=== 6. missing Idempotency-Key → 400 ==='
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $AT" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":5}")
check 'missing key rejected' 400 "$code"

echo '=== 7. withdraw 250 ==='
BAL2B=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['balance']")
curl -s -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}wd-verify-1" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":250,\"description\":\"test withdraw\"}" > /dev/null
BAL3=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | j "[0]['balance']")
python3 -c "import sys; sys.exit(0 if abs(float('$BAL3') - float('$BAL2B') + 250.0) < 0.0001 else 1)" && check 'balance decreased by exactly 250' ok ok || check 'balance decreased by exactly 250' fail ok

echo '=== 8. withdraw more than balance → 422 ==='
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/transactions/withdraw -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}wd-verify-2" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":200000}")
check 'overdraft rejected' 422 "$code"

TKEY=${RUN_ID}tr-verify-1
echo '=== 9. open 2nd savings USD + transfer between them ==='
ACC2=$(curl -s -X POST $BASE/api/accounts -H "Authorization: Bearer $AT" -H "$CT" -d '{"type":"Savings","currency":"USD"}' | j "['id']")
ACC2_NUM=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['accountNumber'] for a in d if a['id']=='$ACC2'][0])")
BAL3B=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$ACC2'][0])")
curl -s -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${TKEY}" -H "$CT" -d "{\"fromAccountId\":\"$ACC_ID\",\"toAccountNumber\":\"$ACC2_NUM\",\"amount\":200,\"description\":\"move money\"}" > /dev/null
BAL4=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$ACC2'][0])")
python3 -c "import sys; sys.exit(0 if abs(float('$BAL4') - float('$BAL3B') - 200.0) < 0.0001 else 1)" && check 'destination credited 200' ok ok || check 'destination credited 200' fail ok

echo '=== 10. transfer to own account number (same account) → 400 ==='
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/transactions/transfer -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}tr-verify-2" -H "$CT" -d "{\"fromAccountId\":\"$ACC_ID\",\"toAccountNumber\":\"AC100000000001\",\"amount\":1}")
check 'same account rejected' 400 "$code"

echo '=== 11. statement JSON + CSV ==='
curl -s "$BASE/api/accounts/$ACC_ID/statement?page=1&pageSize=5" -H "Authorization: Bearer $AT" | j "['total']"
curl -s "$BASE/api/accounts/$ACC_ID/statement?format=csv" -H "Authorization: Bearer $AT"

echo '=== 12. admin login + freeze account → ops blocked → unfreeze ==='
ALOG=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"admin@bank.com","password":"Admin123!"}')
ADM=$(echo "$ALOG" | j "['accessToken']")
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/admin/accounts/$ACC_ID/freeze -H "Authorization: Bearer $ADM" -H "$CT" -d '{"reason":"verification test"}'); check 'freeze ok' 204 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/transactions/deposit -H "Authorization: Bearer $AT" -H "Idempotency-Key: ${RUN_ID}dep-frozen-1" -H "$CT" -d "{\"accountId\":\"$ACC_ID\",\"amount\":10}"); check 'deposit on frozen rejected' 422 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/admin/accounts/$ACC_ID/unfreeze -H "Authorization: Bearer $ADM" -H "$CT" -d '{"reason":"done"}'); check 'unfreeze ok' 204 "$code"

echo '=== 13. reverse the transfer via admin ==='
TR_ID=$(curl -s "$BASE/api/accounts/$ACC_ID/statement?page=1&pageSize=10" -H "Authorization: Bearer $AT" | python3 -c "import sys,json; d=json.load(sys.stdin); print([i['transactionId'] for i in d['items'] if i['reference']=='${TKEY}'][0])")
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/admin/transactions/$TR_ID/reverse -H "Authorization: Bearer $ADM" -H "Idempotency-Key: ${RUN_ID}rev-verify-1" -H "$CT" -d '{"description":"test reversal"}'); check 'reversal posted' 201 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/admin/transactions/$TR_ID/reverse -H "Authorization: Bearer $ADM" -H "Idempotency-Key: ${RUN_ID}rev-verify-1b" -H "$CT" -d '{"description":"again"}'); check 'double reversal rejected' 409 "$code"
BAL5=$(curl -s $BASE/api/accounts -H "Authorization: Bearer $AT" | python3 -c "import sys,json; d=json.load(sys.stdin); print([a['balance'] for a in d if a['id']=='$ACC2'][0])")
python3 -c "import sys; sys.exit(0 if float('$BAL5')==0.0 else 1)" && check 'destination back to 0 after reversal' ok ok || check 'destination back to 0 after reversal' fail ok

echo '=== 14. refresh rotation + reuse detection ==='
R1=$(curl -s -X POST $BASE/api/auth/refresh -H "$CT" -d "{\"refreshToken\":\"$RT\"}")
RT2=$(echo "$R1" | j "['refreshToken']"); AT2=$(echo "$R1" | j "['accessToken']")
check 'rotation returns new tokens' true "$([ -n "$RT2" ] && echo true)"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/refresh -H "$CT" -d "{\"refreshToken\":\"$RT\"}"); check 'reuse of rotated token rejected' 409 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' $BASE/api/auth/me -H "Authorization: Bearer $AT2"); check 'access token from rotated pair invalid after reuse (family revoked? no—this one was valid)' 200 "$code"
# after reuse detection, ALL sessions for the user are revoked — AT2's refresh token should now fail too
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/refresh -H "$CT" -d "{\"refreshToken\":\"$RT2\"}"); check 'family revocation: rotated refresh also dead' 409 "$code"

echo '=== 15. 2FA flow (setup → enable → login with code → disable) ==='
L=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}')
AT3=$(echo "$L" | j "['accessToken']")
code=$(curl -s -X POST $BASE/api/auth/2fa/setup -H "Authorization: Bearer $AT3")
SECRET=$(echo "$code" | j "['secretBase32']"); echo "secret: ${SECRET:0:8}..." 
TOTP=$(node -e "
const crypto=require('crypto');
const b32='$SECRET'.replace(/=+$/,'');
const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
let bits='';for(const c of b32){bits+=alphabet.indexOf(c).toString(2).padStart(5,'0')}
const bytes=[];for(let i=0;i+8<=bits.length;i+=8)bytes.push(parseInt(bits.slice(i,i+8),2));
const key=Buffer.from(bytes);
const counter=Math.floor(Date.now()/1000/30);
const buf=Buffer.alloc(8);buf.writeBigUInt64BE(BigInt(counter));
const hmac=crypto.createHmac('sha1',key).update(buf).digest();
const off=hmac[hmac.length-1]&0xf;
const code=((hmac.readUInt32BE(off)&0x7fffffff)%1000000).toString().padStart(6,'0');
console.log(code);")
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/2fa/enable -H "Authorization: Bearer $AT3" -H "$CT" -d "{\"code\":\"$TOTP\"}"); check '2fa enable ok' 204 "$code"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}'); check 'login without code rejected' 422 "$code"
TOTP2=$(node -e "
const crypto=require('crypto');
const b32='$SECRET'.replace(/=+$/,'');
const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
let bits='';for(const c of b32){bits+=alphabet.indexOf(c).toString(2).padStart(5,'0')}
const bytes=[];for(let i=0;i+8<=bits.length;i+=8)bytes.push(parseInt(bits.slice(i,i+8),2));
const key=Buffer.from(bytes);
const counter=Math.floor(Date.now()/1000/30);
const buf=Buffer.alloc(8);buf.writeBigUInt64BE(BigInt(counter));
const hmac=crypto.createHmac('sha1',key).update(buf).digest();
const off=hmac[hmac.length-1]&0xf;
const code=((hmac.readUInt32BE(off)&0x7fffffff)%1000000).toString().padStart(6,'0');
console.log(code);")
L2=$(curl -s -X POST $BASE/api/auth/login -H "$CT" -d "{\"email\":\"demo@bank.com\",\"password\":\"Demo123!\",\"totpCode\":\"$TOTP2\"}")
check 'login with code works' true "$(echo "$L2" | j "['accessToken']" | grep -q . && echo true)"
AT4=$(echo "$L2" | j "['accessToken']")
TOTP3=$(node -e "
const crypto=require('crypto');
const b32='$SECRET'.replace(/=+$/,'');
const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
let bits='';for(const c of b32){bits+=alphabet.indexOf(c).toString(2).padStart(5,'0')}
const bytes=[];for(let i=0;i+8<=bits.length;i+=8)bytes.push(parseInt(bits.slice(i,i+8),2));
const key=Buffer.from(bytes);
const counter=Math.floor(Date.now()/1000/30);
const buf=Buffer.alloc(8);buf.writeBigUInt64BE(BigInt(counter));
const hmac=crypto.createHmac('sha1',key).update(buf).digest();
const off=hmac[hmac.length-1]&0xf;
const code=((hmac.readUInt32BE(off)&0x7fffffff)%1000000).toString().padStart(6,'0');
console.log(code);")
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/2fa/disable -H "Authorization: Bearer $AT4" -H "$CT" -d "{\"code\":\"$TOTP3\"}"); check '2fa disable ok' 204 "$code"

echo '=== waiting 65s for rate-limit window reset ==='; sleep 65
echo '=== 16. wrong password x5 → lockout ==='
for i in 1 2 3 4 5; do curl -s -o /dev/null -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"wrongpass1"}'; done
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/api/auth/login -H "$CT" -d '{"email":"demo@bank.com","password":"Demo123!"}')
check 'locked out after 5 failures' 423 "$code"
docker exec corebanking-postgres psql -U corebanking -d corebanking -c "update users set lockout_end=null, failed_login_attempts=0 where email='demo@bank.com'" > /dev/null
echo '(lockout cleared by admin for test)'

echo '=== 17. admin audit log ==='
curl -s "$BASE/api/admin/audit?page=1&pageSize=5" -H "Authorization: Bearer $ADM" | j "['total']"

echo
echo "RESULT: $pass passed, $fail failed"
