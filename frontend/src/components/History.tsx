import { useEffect, useState } from 'react'
import { api, ApiError, type Account, type Paged, type StatementLine } from '../api'

const money = (n: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)

export default function History() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [accountId, setAccountId] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<Paged<StatementLine> | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const currency = accounts.find((a) => a.id === accountId)?.currency ?? 'USD'

  useEffect(() => {
    api.listAccounts().then((list) => {
      setAccounts(list)
      setAccountId((cur) => cur || list[0]?.id || '')
    }).catch(() => setError('Failed to load accounts'))
  }, [])

  useEffect(() => {
    if (!accountId) return
    setLoading(true)
    setError(null)
    api.statement(accountId, { from: from || undefined, to: to || undefined, page, pageSize: 10 })
      .then(setData)
      .catch((err) => setError(err instanceof ApiError ? err.message : 'Failed to load statement'))
      .finally(() => setLoading(false))
  }, [accountId, from, to, page])

  const downloadCsv = async () => {
    setError(null)
    try {
      await api.statementCsv(accountId, from || undefined, to || undefined)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'CSV export failed')
    }
  }

  return (
    <div className="card history-card">
      <div className="dashboard-head">
        <h1 className="section-title" style={{ margin: 0 }}>Transaction history</h1>
        <button className="btn btn-ghost" onClick={downloadCsv} disabled={!accountId}>Download CSV</button>
      </div>

      {error && <div className="alert">{error}</div>}

      <div className="filters">
        <label className="field">
          <span>Account</span>
          <select value={accountId} onChange={(e) => { setAccountId(e.target.value); setPage(1) }}>
            {accounts.map((a) => <option key={a.id} value={a.id}>{a.accountNumber} · {a.currency}</option>)}
          </select>
        </label>
        <label className="field">
          <span>From</span>
          <input type="date" value={from} onChange={(e) => { setFrom(e.target.value); setPage(1) }} />
        </label>
        <label className="field">
          <span>To</span>
          <input type="date" value={to} onChange={(e) => { setTo(e.target.value); setPage(1) }} />
        </label>
      </div>

      {loading ? (
        <p className="muted">Loading…</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Date</th><th>Reference</th><th>Type</th><th>Description</th><th className="num">Amount</th><th className="num">Balance</th></tr>
            </thead>
            <tbody>
              {data?.items.map((r) => (
                <tr key={r.entryId}>
                  <td className="mono">{new Date(r.postedAt).toLocaleString()}</td>
                  <td className="mono small">{r.reference}</td>
                  <td>{r.type}</td>
                  <td>{r.description}</td>
                  <td className={`num mono ${r.amount < 0 ? 'neg' : 'pos'}`}>{money(r.amount, currency)}</td>
                  <td className="num mono">{money(r.balanceAfter, currency)}</td>
                </tr>
              ))}
              {!data || data.items.length === 0 ? (
                <tr><td colSpan={6} className="muted center">No entries for this range</td></tr>
              ) : null}
            </tbody>
          </table>
        </div>
      )}

      {data && data.totalPages > 1 && (
        <div className="pager">
          <button className="btn btn-ghost" disabled={page <= 1} onClick={() => setPage(page - 1)}>← Prev</button>
          <span className="muted">Page {data.page} of {data.totalPages} · {data.total} entries</span>
          <button className="btn btn-ghost" disabled={page >= data.totalPages} onClick={() => setPage(page + 1)}>Next →</button>
        </div>
      )}
    </div>
  )
}
