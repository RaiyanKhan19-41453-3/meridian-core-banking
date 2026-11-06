import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError, type Account, type OverdraftFacilityDto, type OverdraftProductTerms } from '../api'

const money = (n: number) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n)

export default function OverdraftPanel({ accounts, onChanged }: { accounts: Account[]; onChanged: () => void }) {
  const [products, setProducts] = useState<OverdraftProductTerms[]>([])
  const [facilities, setFacilities] = useState<OverdraftFacilityDto[]>([])
  const [productCode, setProductCode] = useState<'ODL-VAR' | 'ODL-SEC'>('ODL-VAR')
  const [accountId, setAccountId] = useState('')
  const [collateralId, setCollateralId] = useState('')
  const [limit, setLimit] = useState('5000')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)

  const currentAccounts = useMemo(
    () => accounts.filter((a) => a.type === 'Current' && a.status === 'Active' && a.currency === 'USD'),
    [accounts],
  )
  const savingsAccounts = useMemo(
    () => accounts.filter((a) => a.type === 'Savings' && a.status === 'Active' && a.currency === 'USD'),
    [accounts],
  )
  const selectedProduct = products.find((p) => p.productCode === productCode)
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
        collateralAccountId: productCode === 'ODL-SEC' ? collateralId : undefined,
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
      setMsg(`${f.reference} closed.${f.productCode === 'ODL-SEC' ? ' Collateral released.' : ''}`)
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
                onChange={() => setProductCode(p.productCode as 'ODL-VAR' | 'ODL-SEC')} />
              <div className="od-product-body">
                <div className="od-product-title">
                  {p.secured ? '🔒 ODL-SEC — Secured' : '⚡ ODL-VAR — Unsecured'}
                </div>
                <div className="od-product-terms">
                  {money(p.minLimit)}–{money(p.maxLimit)} · {p.annualInterestRate.toFixed(0)}% p.a.
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
        <p className="muted">Open a USD Current account (Overview) to apply.</p>
      ) : (
        <form className="od-apply" onSubmit={apply}>
          <label className="field">
            <span>Account to attach</span>
            <select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
              {currentAccounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber}</option>)}
            </select>
          </label>
          {productCode === 'ODL-SEC' && (
            <label className="field">
              <span>Collateral savings account</span>
              <select value={collateralId} onChange={(e) => setCollateralId(e.target.value)}>
                {savingsAccounts.length === 0 && <option value="">No USD savings accounts</option>}
                {savingsAccounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber} · {money(a.balance)}</option>)}
              </select>
            </label>
          )}
          <label className="field">
            <span>Limit (USD)</span>
            <input type="number" min={selectedProduct?.minLimit ?? 500} max={selectedProduct?.maxLimit ?? 25000}
              step="100" value={limit} onChange={(e) => setLimit(e.target.value)} />
          </label>
          <button className="btn btn-primary" disabled={busy || !accountId || (productCode === 'ODL-SEC' && !pledgeCovered)}>
            {busy ? 'Applying…' : 'Apply for overdraft'}
          </button>
          {productCode === 'ODL-SEC' && selectedCollateral && requestedLimit > 0 && (
            <p className={pledgeCovered ? 'ok-text small' : 'neg small'} style={{ margin: '4px 0 0' }}>
              {pledgeCovered
                ? `✔ Pledge of ${money(requiredPledge)} covered by ${money(selectedCollateral.balance)}`
                : `✖ Need ${money(requiredPledge)} in the savings account, have ${money(selectedCollateral.balance)}`}
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
                    <div className="muted small">{f.productCode} · score {f.creditScore} · {f.accountNumber}{f.collateralAccountNumber ? ` · pledge ${f.collateralAccountNumber}` : ''}</div>
                  </div>
                  <span className={`pill ${f.status === 'Active' ? 'pill-active' : f.status === 'Rejected' ? 'pill-rejected' : 'pill-closed'}`}>
                    {f.status}
                  </span>
                </div>
                <div className="loan-metrics">
                  <div><span className="muted small">Limit</span><br /><strong>{money(f.limit)}</strong></div>
                  <div><span className="muted small">Available</span><br /><strong>{money(f.availableCredit)}</strong></div>
                  <div><span className="muted small">Accrued</span><br /><strong>{money(f.accruedInterestUncharged)}</strong></div>
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
                      {f.currentBalance < 0 && <span className="muted small">repay {money(-f.currentBalance)} to close</span>}
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
