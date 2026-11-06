import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, type Account } from '../api'

const money = (n: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)

export default function Dashboard() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [openType, setOpenType] = useState<'Savings' | 'Current'>('Savings')
  const [openCurrency, setOpenCurrency] = useState('USD')
  const [opening, setOpening] = useState(false)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setAccounts(await api.listAccounts())
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load accounts')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])

  const openAccount = async (e: FormEvent) => {
    e.preventDefault()
    setOpening(true)
    setError(null)
    try {
      await api.openAccount(openType, openCurrency)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to open account')
    } finally {
      setOpening(false)
    }
  }

  const totalByCurrency = accounts.reduce<Record<string, number>>((acc, a) => {
    acc[a.currency] = (acc[a.currency] ?? 0) + a.balance
    return acc
  }, {})

  return (
    <div className="dashboard">
      <div className="dashboard-head">
        <h1>Accounts</h1>
        <button className="btn btn-ghost" onClick={() => void refresh()}>Refresh</button>
      </div>

      {error && <div className="alert">{error}</div>}

      <div className="summary-grid">
        {Object.entries(totalByCurrency).map(([cur, total]) => (
          <div className="card summary-card" key={cur}>
            <div className="summary-label">Total {cur}</div>
            <div className="summary-value">{money(total, cur)}</div>
          </div>
        ))}
        {accounts.length === 0 && !loading && (
          <div className="card summary-card">
            <div className="summary-label">No accounts yet</div>
            <div className="summary-value summary-empty">Open your first account below</div>
          </div>
        )}
      </div>

      <div className="content-grid">
        <section className="card">
          <h2 className="section-title">Your accounts</h2>
          {loading ? (
            <p className="muted">Loading…</p>
          ) : (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr><th>Account</th><th>Type</th><th>Status</th><th className="num">Balance</th></tr>
                </thead>
                <tbody>
                  {accounts.map((a) => (
                    <tr key={a.id}>
                      <td className="mono">{a.accountNumber}</td>
                      <td>{a.type}</td>
                      <td><span className={`pill pill-${a.status.toLowerCase()}`}>{a.status}</span></td>
                      <td className="num mono">{money(a.balance, a.currency)}</td>
                    </tr>
                  ))}
                  {accounts.length === 0 && (
                    <tr><td colSpan={4} className="muted center">No accounts found</td></tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="card">
          <h2 className="section-title">Open an account</h2>
          <form onSubmit={openAccount} className="open-form">
            <label className="field">
              <span>Account type</span>
              <select value={openType} onChange={(e) => setOpenType(e.target.value as 'Savings' | 'Current')}>
                <option value="Savings">Savings</option>
                <option value="Current">Current</option>
              </select>
            </label>
            <label className="field">
              <span>Currency</span>
              <select value={openCurrency} onChange={(e) => setOpenCurrency(e.target.value)}>
                {['USD', 'EUR', 'GBP', 'KES', 'NGN'].map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </label>
            <button className="btn btn-primary btn-block" disabled={opening}>
              {opening ? 'Opening…' : 'Open account'}
            </button>
          </form>
        </section>
      </div>

      <p className="demo-hint">Use the “Move money” tab to deposit, withdraw or transfer — every posting is double-entry and idempotent.</p>
    </div>
  )
}
