import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError, type Account, type ApplyLoanResponse, type LoanDetailDto, type LoanProductDto, type LoanSummaryDto } from '../api'
import OverdraftPanel from './OverdraftPanel'

const money = (n: number, currency = 'USD') =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)
const pct = (rate: number) => `${(rate * 100).toFixed(2)}% p.a.`

export default function Loans() {
  const [products, setProducts] = useState<LoanProductDto[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [loans, setLoans] = useState<LoanSummaryDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [productCode, setProductCode] = useState('PLN-PER')
  const [principal, setPrincipal] = useState('5000')
  const [termMonths, setTermMonths] = useState('12')
  const [purpose, setPurpose] = useState('')
  const [accountId, setAccountId] = useState('')

  const [offer, setOffer] = useState<ApplyLoanResponse | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [detail, setDetail] = useState<LoanDetailDto | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)

  const selectedProduct = useMemo(
    () => products.find((p) => p.code === productCode) ?? products[0],
    [products, productCode],
  )

  const reload = async () => {
    const [p, a, l] = await Promise.all([api.loanProducts(), api.listAccounts(), api.listLoans()])
    setProducts(p)
    setAccounts(a.filter((x) => x.status === 'Active' && x.currency === 'USD'))
    setAccountId((cur) => cur || a.find((x) => x.status === 'Active' && x.currency === 'USD')?.id || '')
    setLoans(l)
  }

  useEffect(() => {
    setLoading(true)
    reload().catch(() => setError('Failed to load loans')).finally(() => setLoading(false))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const apply = async (e: FormEvent) => {
    e.preventDefault()
    const amount = Number(principal)
    if (!Number.isFinite(amount) || !selectedProduct) return
    setBusy(true)
    setError(null)
    setMsg(null)
    try {
      const res = await api.applyLoan({
        loanProductCode: selectedProduct.code,
        principal: amount,
        termMonths: Number(termMonths),
        purpose: purpose || 'General purpose',
        disbursementAccountId: accountId,
      })
      setOffer(res)
      setMsg(res.decision === 'Approved'
        ? 'Congratulations — your loan is approved. Review the offer and disburse when ready.'
        : 'Your application was declined based on our scoring policy.')
      await reload()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Application failed')
    } finally {
      setBusy(false)
    }
  }

  const accept = async (loanId: string) => {
    setBusy(true)
    setError(null)
    try {
      await api.acceptLoan(loanId)
      setMsg('Loan disbursed — funds are in your account.')
      setOffer(null)
      await reload()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Disbursement failed')
    } finally {
      setBusy(false)
    }
  }

  const cancel = async (loanId: string) => {
    setBusy(true)
    setError(null)
    try {
      await api.cancelLoan(loanId)
      setMsg('Loan application cancelled.')
      setOffer(null)
      await reload()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Cancel failed')
    } finally {
      setBusy(false)
    }
  }

  const toggleSchedule = async (loanId: string) => {
    if (expanded === loanId) {
      setExpanded(null)
      setDetail(null)
      return
    }
    setExpanded(loanId)
    setDetailLoading(true)
    try {
      setDetail(await api.getLoan(loanId))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load schedule')
    } finally {
      setDetailLoading(false)
    }
  }

  if (loading) return <p className="muted">Loading…</p>

  return (
    <>
    <div className="loans-grid">
      {/* ---------- apply panel ---------- */}
      <section className="card">
        <h2 className="section-title">Apply for a loan</h2>
        {error && <div className="alert">{error}</div>}
        {msg && <div className="alert alert-ok">{msg}</div>}

        {offer && (
          <div className={`offer ${offer.decision === 'Approved' ? 'offer-ok' : 'offer-bad'}`}>
            <div className="offer-title">
              {offer.decision === 'Approved' ? '✔ Approved' : '✖ Declined'} · score {offer.creditScore}
            </div>
            <div className="offer-reason">{offer.reason}</div>
            {offer.decision === 'Approved' && (
              <div className="offer-amounts">
                <span>EMI <strong>{money(offer.loan.schedule[0]?.total ?? 0)}</strong></span>
                <span>Fee <strong>{money(offer.loan.originationFee)}</strong></span>
                <span>Total repaid <strong>{money(offer.loan.schedule.reduce((s, i) => s + i.total, 0))}</strong></span>
              </div>
            )}
            <div className="btn-row" style={{ marginTop: 12 }}>
              {offer.decision === 'Approved' && (
                <>
                  <button className="btn btn-primary" disabled={busy} onClick={() => accept(offer.loan.summary.id)}>
                    Accept & disburse
                  </button>
                  <button className="btn btn-ghost" disabled={busy} onClick={() => cancel(offer.loan.summary.id)}>
                    Cancel
                  </button>
                </>
              )}
            </div>
          </div>
        )}

        <form onSubmit={apply}>
          <label className="field">
            <span>Product</span>
            <select value={productCode} onChange={(e) => setProductCode(e.target.value)}>
              {products.map((p) => (
                <option key={p.code} value={p.code}>
                  {p.name} — {pct(p.annualInterestRate)} · {money(p.minPrincipal)}–{money(p.maxPrincipal)}
                </option>
              ))}
            </select>
          </label>
          {selectedProduct && (
            <p className="product-terms">
              {selectedProduct.minTermMonths}–{selectedProduct.maxTermMonths} months · fee {money(selectedProduct.originationFee)} · min score {selectedProduct.minCreditScore}
            </p>
          )}
          <label className="field">
            <span>Amount (USD)</span>
            <input type="number" min={selectedProduct?.minPrincipal} max={selectedProduct?.maxPrincipal}
              step="1" value={principal} onChange={(e) => setPrincipal(e.target.value)} required />
          </label>
          <label className="field">
            <span>Term (months)</span>
            <input type="number" min={selectedProduct?.minTermMonths} max={selectedProduct?.maxTermMonths}
              value={termMonths} onChange={(e) => setTermMonths(e.target.value)} required />
          </label>
          <label className="field">
            <span>Disburse to</span>
            <select value={accountId} onChange={(e) => setAccountId(e.target.value)} required>
              {accounts.length === 0 && <option value="">No USD accounts</option>}
              {accounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber} · {a.type}</option>)}
            </select>
          </label>
          <label className="field">
            <span>Purpose <small>(optional)</small></span>
            <input value={purpose} onChange={(e) => setPurpose(e.target.value)} maxLength={500}
              placeholder="e.g. Home renovation" />
          </label>
          {accounts.length === 0 && <p className="muted small">Open a USD account first (Overview tab).</p>}
          <button className="btn btn-primary btn-block" disabled={busy || !accountId || !selectedProduct}>
            {busy ? 'Submitting…' : 'Submit application'}
          </button>
        </form>
      </section>

      {/* ---------- my loans ---------- */}
      <section className="card loans-list-card">
        <h2 className="section-title">My loans</h2>
        {loans.length === 0 && <p className="muted">No loans yet — apply on the left.</p>}
        {loans.map((l) => {
          const progress = l.installmentsTotal === 0 ? 0 : (l.installmentsPaid / l.installmentsTotal) * 100
          const statusClass = l.status === 'Active' ? 'pill-active'
            : l.status === 'Approved' ? 'pill-frozen'
            : l.status === 'Completed' ? 'pill-active'
            : 'pill-closed'
          return (
            <div key={l.id} className="loan-card">
              <div className="loan-card-head">
                <div>
                  <div className="loan-ref mono">{l.reference}</div>
                  <div className="muted small">{l.productName} · {l.annualInterestRate * 100}% p.a. · {l.termMonths} months</div>
                </div>
                <span className={`pill ${statusClass}`}>{l.status}</span>
              </div>
              <div className="loan-card-body">
                <div className="loan-metrics">
                  <div><span className="muted small">Principal</span><br /><strong>{money(l.principal)}</strong></div>
                  <div><span className="muted small">Outstanding</span><br /><strong>{money(l.totalOutstanding)}</strong></div>
                  <div><span className="muted small">Next due</span><br /><strong>{l.nextDueDate ? `${new Date(l.nextDueDate).toLocaleDateString()} · ${money(l.nextDueAmount ?? 0)}` : '—'}</strong></div>
                </div>
                {l.installmentsTotal > 0 && (
                  <div className="progress" aria-label="Repayment progress">
                    <div className="progress-bar" style={{ width: `${progress}%` }} />
                  </div>
                )}
                <div className="muted small">{l.installmentsPaid}/{l.installmentsTotal} installments paid{l.purpose ? ` · ${l.purpose}` : ''}</div>
              </div>
              <div className="btn-row">
                {l.status === 'Approved' && (
                  <>
                    <button className="btn btn-primary btn-small" disabled={busy} onClick={() => accept(l.id)}>Accept & disburse</button>
                    <button className="btn btn-ghost btn-small" disabled={busy} onClick={() => cancel(l.id)}>Cancel</button>
                  </>
                )}
                <button className="btn btn-ghost btn-small" onClick={() => toggleSchedule(l.id)}>
                  {expanded === l.id ? 'Hide schedule' : 'View schedule'}
                </button>
              </div>
              {expanded === l.id && (
                <div className="table-wrap schedule-wrap">
                  {detailLoading ? <p className="muted">Loading…</p> : detail && expanded === detail.summary.id ? (
                    <table className="table">
                      <thead>
                        <tr><th>#</th><th>Due</th><th className="num">Principal</th><th className="num">Interest</th><th className="num">Total</th><th>Status</th></tr>
                      </thead>
                      <tbody>
                        {detail.schedule.map((s) => (
                          <tr key={s.installmentNo}>
                            <td>{s.installmentNo}</td>
                            <td className="mono small">{new Date(s.dueDate).toLocaleDateString()}</td>
                            <td className="num mono">{money(s.principal)}</td>
                            <td className="num mono">{money(s.interest)}</td>
                            <td className="num mono">{money(s.total)}</td>
                            <td><span className={`pill ${s.status === 'Paid' ? 'pill-active' : s.status === 'Overdue' ? 'pill-rejected' : 'pill-closed'}`}>{s.status}</span></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  ) : <p className="muted">Schedule unavailable</p>}
                </div>
              )}
            </div>
          )
        })}
      </section>
    </div>
    <OverdraftPanel accounts={accounts} onChanged={() => void reload()} />
    </>
  )
}
