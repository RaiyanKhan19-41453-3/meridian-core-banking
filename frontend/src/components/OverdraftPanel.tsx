import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError, type Account, type OverdraftFacilityDto, type OverdraftProductTerms } from '../api'

const money = (n: number, currency = 'USD') =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)

export default function OverdraftPanel({ accounts, onChanged }: { accounts: Account[]; onChanged: () => void }) {
  const [products, setProducts] = useState<OverdraftProductTerms[]>([])
  const [facilities, setFacilities] = useState<OverdraftFacilityDto[]>([])
  const [productCode, setProductCode] = useState<'ODL-VAR' | 'ODL-SEC' | 'ODL-VAR-BDT' | 'ODL-SEC-BDT'>('ODL-VAR')
  const [accountId, setAccountId] = useState('')
  const [collateralId, setCollateralId] = useState('')
  const [limit, setLimit] = useState('5000')
  const [cib, setCib] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)

  const selectedProduct = products.find((p) => p.productCode === productCode) ?? products[0]
  const productCurrency = selectedProduct?.currency ?? 'USD'
  const currentAccounts = useMemo(
    () => accounts.filter((a) => a.type === 'Current' && a.status === 'Active' && a.currency === productCurrency),
    [accounts, productCurrency],
  )
  const savingsAccounts = useMemo(
    () => accounts.filter((a) => a.type === 'Savings' && a.status === 'Active' && a.currency === productCurrency),
    [accounts, productCurrency],
  )
  const requestedLimit = Number(limit) || 0
  const requiredPledge = selectedProduct?.secured && requestedLimit > 0
    ? requestedLimit / 0.9
    : 0
  const selectedCollateral = savingsAccounts.find((a) => a.id === collateralId)
  const pledgeCovered = !!selectedCollateral && selectedCollateral.balance >= requiredPledge

  const reload = async () => {
    const [p, f] = await Promise.all([api.overdraftTerms(), api.myOverdrafts()])
    setProducts(p)
    setFacilities(f)
  }

  useEffect(() => {
    reload().catch(() => setError('Failed to load overdraft details'))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    setAccountId((cur) => cur || currentAccounts[0]?.id || '')
  }, [currentAccounts])

  useEffect(() => {
    setCollateralId((cur) => cur || savingsAccounts[0]?.id || '')
  }, [savingsAccounts])

  const apply = async (e: FormEvent) => {
    e.preventDefault()
    if (!Number.isFinite(requestedLimit) || !accountId) return
    setBusy(true)
    setError(null)
    setMsg(null)
    try {
      const f = await api.applyOverdraft({
        accountId,
        productCode,
        requestedLimit,
        collateralAccountId: selectedProduct?.secured ? collateralId : undefined,
        cibScore: cib.trim() ? Number(cib) : undefined,
      })
      setMsg(f.status === 'Active'
        ? `Facility ${f.reference} approved — ${money(f.limit)} of headroom unlocked.`
        : `Declined — ${f.decisionReason}`)
      await reload()
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Application failed')
    } finally {
      setBusy(false)
    }
  }

  const close = async (f: OverdraftFacilityDto) => {
    setBusy(true)
    setError(null)
    try {
      await api.closeOverdraft(f.id, 'Closed by customer')
      setMsg(`${f.reference} closed.${f.productCode.includes('SEC') ? ' Collateral released.' : ''}`)
      await reload()
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not close facility')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card overdraft-card">
      <div className="dashboard-head">
        <h2 className="section-title" style={{ margin: 0 }}>Overdraft facility</h2>
        <div className="btn-row" style={{ alignItems: 'center' }}>
          {selectedProduct && (
            <span className="muted small">{selectedProduct.productCode} · {selectedProduct.annualInterestRate.toFixed(0)}% p.a.</span>
          )}
        </div>
      </div>

      <p className="muted">Revolving credit on your Current account — interest only on what you use.</p>
      {error && <div className="alert">{error}</div>}
      {msg && <div className="alert alert-ok">{msg}</div>}

      {products.length > 0 && (
        <div className="od-products" role="radiogroup" aria-label="Overdraft product">
          {products.map((p) => (
            <label key={p.productCode} className={productCode === p.productCode ? 'od-product selected' : 'od-product'}>
              <input type="radio" name="od-product" value={p.productCode} checked={productCode === p.productCode}
                onChange={() => setProductCode(p.productCode)} />
              <div className="od-product-body">
                <div className="od-product-title">
                  {p.secured ? '🔒 ODL-SEC — Secured' : '⚡ ODL-VAR — Unsecured'}
                </div>
                <div className="od-product-terms">
                  {p.currency === 'BDT' ? 'BDT' : ''}{money(p.minLimit, p.currency)}–{money(p.maxLimit, p.currency)} · {p.annualInterestRate.toFixed(0)}% p.a.
                  {p.secured ? ` · pledge ≥ limit ÷ 0.9` : ` · credit score ≥ ${p.minCreditScore}`}
                </div>
                <div className="od-product-desc">
                  {p.secured
                    ? 'Backed by your Savings balance (stays locked until you close the facility). No credit score required.'
                    : 'No collateral. Approval depends on your credit score.'}
                </div>
              </div>
            </label>
          ))}
        </div>
      )}

      {currentAccounts.length === 0 ? (
        <p className="muted">Open a {productCurrency} Current account (Overview) to apply.</p>
      ) : (
        <form className="od-apply" onSubmit={apply}>
          <label className="field">
            <span>Account to attach</span>
            <select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
              {currentAccounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber}</option>)}
            </select>
          </label>
          {selectedProduct?.secured && (
            <label className="field">
              <span>Collateral savings account</span>
              <select value={collateralId} onChange={(e) => setCollateralId(e.target.value)}>
                {savingsAccounts.length === 0 && <option value="">No {productCurrency} savings accounts</option>}
                {savingsAccounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber} · {money(a.balance, productCurrency)}</option>)}
              </select>
            </label>
          )}
          <label className="field">
            <span>Limit ({productCurrency})</span>
            <input type="number" min={selectedProduct?.minLimit ?? 500} max={selectedProduct?.maxLimit ?? 25000}
              step="100" value={limit} onChange={(e) => setLimit(e.target.value)} />
          </label>
          <label className="field">
            <span>CIB bureau score <small>(optional)</small></span>
            <input type="number" min={300} max={850} value={cib} onChange={(e) => setCib(e.target.value)} placeholder="e.g. 780" />
          </label>
          <button className="btn btn-primary" disabled={busy || !accountId || (selectedProduct?.secured && !pledgeCovered)}>
            {busy ? 'Applying…' : 'Apply for overdraft'}
          </button>
          {selectedProduct?.secured && selectedCollateral && requestedLimit > 0 && (
            <p className={pledgeCovered ? 'ok-text small' : 'neg small'} style={{ margin: '4px 0 0' }}>
              {pledgeCovered
                ? `✔ Pledge of ${money(requiredPledge, productCurrency)} covered by ${money(selectedCollateral.balance, productCurrency)}`
                : `✖ Need ${money(requiredPledge, productCurrency)} in the savings account, have ${money(selectedCollateral.balance, productCurrency)}`}
            </p>
          )}
        </form>
      )}

      {facilities.length > 0 && (
        <div className="od-list">
          {facilities.map((f) => {
            const usedPct = f.limit === 0 ? 0 : Math.min(100, (Math.max(0, -f.currentBalance) / f.limit) * 100)
            return (
              <div key={f.id} className="od-item">
                <div className="od-item-head">
                  <div>
                    <div className="mono od-ref">{f.reference}</div>
                    <div className="muted small">{f.productCode} · score {f.creditScore}{f.cibScore !== null ? ` (CIB ${f.cibScore})` : ''} · {f.accountNumber}{f.collateralAccountNumber ? ` · pledge ${f.collateralAccountNumber}` : ''}</div>
                  </div>
                  <span className={`pill ${f.status === 'Active' ? 'pill-active' : f.status === 'Rejected' ? 'pill-rejected' : 'pill-closed'}`}>
                    {f.status}
                  </span>
                </div>
                <div className="loan-metrics">
                  <div><span className="muted small">Limit</span><br /><strong>{money(f.limit, f.productCode.endsWith('-BDT') ? 'BDT' : 'USD')}</strong></div>
                  <div><span className="muted small">Available</span><br /><strong>{money(f.availableCredit, f.productCode.endsWith('-BDT') ? 'BDT' : 'USD')}</strong></div>
                  <div><span className="muted small">Accrued</span><br /><strong>{money(f.accruedInterestUncharged, f.productCode.endsWith('-BDT') ? 'BDT' : 'USD')}</strong></div>
                </div>
                {f.status === 'Active' && (
                  <>
                    <div className="progress" aria-label="utilisation">
                      <div className="progress-bar progress-bar-od" style={{ width: `${usedPct}%` }} />
                    </div>
                    <div className="btn-row" style={{ marginTop: 8 }}>
                      <button className="btn btn-ghost btn-small" disabled={busy || f.currentBalance < 0} onClick={() => close(f)}
                        title={f.currentBalance < 0 ? 'Repay the overdrawn balance first' : 'Close facility'}>
                        Close facility
                      </button>
                      {f.currentBalance < 0 && <span className="muted small">repay {money(-f.currentBalance, f.productCode.endsWith('-BDT') ? 'BDT' : 'USD')} to close</span>}
                    </div>
                  </>
                )}
              </div>
            )
          })}
        </div>
      )}
    </section>
  )
}
