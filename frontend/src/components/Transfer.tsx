import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError, type Account, type MoneyResult } from '../api'

type Mode = 'deposit' | 'withdraw' | 'transfer'

const money = (n: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)

export default function Transfer() {
  const [mode, setMode] = useState<Mode>('transfer')
  const [accounts, setAccounts] = useState<Account[]>([])
  const [accountId, setAccountId] = useState('')
  const [toAccountNumber, setToAccountNumber] = useState('')
  const [amount, setAmount] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<MoneyResult | null>(null)
  // Stable per form mount — double submissions replay, never double-post.
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID())

  useEffect(() => {
    api.listAccounts().then((list) => {
      const active = list.filter((a) => a.status === 'Active')
      setAccounts(active)
      setAccountId((cur) => cur || active[0]?.id || '')
    }).catch(() => setError('Failed to load accounts'))
  }, [])

  const selected = useMemo(() => accounts.find((a) => a.id === accountId), [accounts, accountId])
  const currency = selected?.currency ?? 'USD'
  const parsedAmount = Number(amount)
  const amountValid = Number.isFinite(parsedAmount) && parsedAmount > 0 &&
    Math.round(parsedAmount * 10000) === parsedAmount * 10000

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!amountValid) {
      setError('Enter a positive amount with at most 4 decimal places.')
      return
    }
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      let res: MoneyResult
      if (mode === 'deposit') {
        res = await api.deposit(accountId, parsedAmount, description || 'Cash deposit', idempotencyKey)
      } else if (mode === 'withdraw') {
        res = await api.withdraw(accountId, parsedAmount, description || 'Cash withdrawal', idempotencyKey)
      } else {
        res = await api.transfer(accountId, toAccountNumber.trim().toUpperCase(), parsedAmount, description || 'Transfer', idempotencyKey)
      }
      setResult(res)
      setIdempotencyKey(crypto.randomUUID())
      setAmount('')
      setDescription('')
      setToAccountNumber('')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Something went wrong')
    } finally {
      setBusy(false)
    }
  }

  const modeButton = (m: Mode, label: string) => (
    <button type="button" key={m} className={mode === m ? 'segment active' : 'segment'} onClick={() => setMode(m)}>
      {label}
    </button>
  )

  return (
    <div className="move-grid">
      <form className="card move-card" onSubmit={submit}>
        <h1 className="section-title">Move money</h1>

        {error && <div className="alert">{error}</div>}

        <div className="segmented" role="group" aria-label="Transaction type">
          {modeButton('transfer', 'Transfer')}
          {modeButton('deposit', 'Deposit')}
          {modeButton('withdraw', 'Withdraw')}
        </div>

        <label className="field">
          <span>From account</span>
          <select value={accountId} onChange={(e) => setAccountId(e.target.value)} required>
            {accounts.length === 0 && <option value="">No active accounts</option>}
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.accountNumber} · {a.type} · {a.currency} · {money(a.balance, a.currency)}
              </option>
            ))}
          </select>
        </label>

        {mode === 'transfer' && (
          <label className="field">
            <span>Destination account number</span>
            <input value={toAccountNumber} onChange={(e) => setToAccountNumber(e.target.value)}
              placeholder="AC…" required autoComplete="off" spellCheck={false} />
          </label>
        )}

        <label className="field">
          <span>Amount ({currency})</span>
          <input type="number" min="0" step="0.0001" value={amount}
            onChange={(e) => setAmount(e.target.value)} placeholder="0.00" required inputMode="decimal" />
        </label>

        <label className="field">
          <span>Description <small>(optional)</small></span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500}
            placeholder="What's this for?" />
        </label>

        <button className="btn btn-primary btn-block" disabled={busy || accounts.length === 0 || !amountValid}>
          {busy ? 'Processing…' : mode === 'deposit' ? 'Deposit' : mode === 'withdraw' ? 'Withdraw' : 'Send transfer'}
        </button>
        <p className="demo-hint">Requests are idempotency-keyed — safe to retry after a network failure.</p>
      </form>

      {result && (
        <section className="card receipt">
          <h2 className="section-title">Receipt</h2>
          <div className="receipt-amount">{money(result.amount, result.currency)}</div>
          <dl className="receipt-meta">
            <div><dt>Type</dt><dd>{result.type}{result.replayed ? ' (replayed)' : ''}</dd></div>
            <div><dt>Reference</dt><dd className="mono">{result.reference}</dd></div>
            <div><dt>Transaction</dt><dd className="mono small">{result.transactionId}</dd></div>
            <div><dt>Posted</dt><dd>{new Date(result.postedAtUtc).toLocaleString()}</dd></div>
            <div><dt>Description</dt><dd>{result.description}</dd></div>
          </dl>
        </section>
      )}
    </div>
  )
}
