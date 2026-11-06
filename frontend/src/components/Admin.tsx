import { Fragment, useEffect, useState, type FormEvent } from 'react'
import {
  api, ApiError,
  type BatchRunDto, type TrialBalance, type PendingProfileDto, type KycDocumentMeta, type AmlAlertDto, type AdminLoanRow, type AdminOverdraftRow,
} from '../api'

interface AuditItem {
  id: number
  userId: string | null
  action: string
  entityName: string
  entityId: string | null
  ipAddress: string | null
  createdAt: string
}

const runDate = (d: string) => d.slice(0, 10)

export default function Admin() {
  // --- account lifecycle state ---
  const [accountId, setAccountId] = useState('')
  const [reason, setReason] = useState('')
  const [adminMsg, setAdminMsg] = useState<string | null>(null)
  const [adminError, setAdminError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [txId, setTxId] = useState('')
  const [txDesc, setTxDesc] = useState('')

  // --- batch state ---
  const today = new Date().toISOString().slice(0, 10)
  const [batchDate, setBatchDate] = useState(today)
  const [batchResult, setBatchResult] = useState<BatchRunDto | null>(null)
  const [batchBusy, setBatchBusy] = useState(false)
  const [runs, setRuns] = useState<BatchRunDto[]>([])

  // --- trial balance state ---
  const [trialBalance, setTrialBalance] = useState<TrialBalance | null>(null)
  const [tbBusy, setTbBusy] = useState(false)

  // --- KYC queue state ---
  const [kycQueue, setKycQueue] = useState<PendingProfileDto[]>([])
  const [kycTotal, setKycTotal] = useState(0)
  const [expandedUserId, setExpandedUserId] = useState<string | null>(null)
  const [expandedDocs, setExpandedDocs] = useState<KycDocumentMeta[]>([])
  const [rejectReason, setRejectReason] = useState('')

  // --- AML alerts state ---
  const [alerts, setAlerts] = useState<AmlAlertDto[]>([])
  const [alertsTotal, setAlertsTotal] = useState(0)
  const [alertFilter, setAlertFilter] = useState<'Open' | 'Reviewed' | 'Dismissed' | ''>('Open')
  const [alertNote, setAlertNote] = useState('')

  // --- loan portfolio state ---
  const [loanRows, setLoanRows] = useState<AdminLoanRow[]>([])
  const [loanTotal, setLoanTotal] = useState(0)
  const [loanFilter, setLoanFilter] = useState<'Active' | 'Approved' | 'Completed' | 'Rejected' | 'Cancelled' | ''>('Active')

  // --- overdraft portfolio state ---
  const [odRows, setOdRows] = useState<AdminOverdraftRow[]>([])
  const [odTotal, setOdTotal] = useState(0)
  const [odFilter, setOdFilter] = useState<'Active' | 'Closed' | 'Rejected' | ''>('Active')

  // --- audit state ---
  const [audit, setAudit] = useState<AuditItem[]>([])
  const [auditTotal, setAuditTotal] = useState(0)

  const loadAudit = () => {
    api.adminAudit(1, 50)
      .then((r) => {
        setAudit(r.items as unknown as AuditItem[])
        setAuditTotal(r.total)
      })
      .catch(() => setAdminError('Failed to load audit log'))
  }

  const loadLoans = () => {
    api.adminLoans(loanFilter, 1, 50)
      .then((r) => {
        setLoanRows(r.items)
        setLoanTotal(r.total)
      })
      .catch(() => { /* non-fatal */ })
  }

  const loadOverdrafts = () => {
    api.adminOverdrafts(odFilter, 1, 50)
      .then((r) => {
        setOdRows(r.items)
        setOdTotal(r.total)
      })
      .catch(() => { /* non-fatal */ })
  }

  const loadKycQueue = () => {
    api.adminPendingKyc(1, 50)
      .then((r) => {
        setKycQueue(r.items)
        setKycTotal(r.total)
      })
      .catch(() => { /* non-fatal */ })
  }

  const loadAlerts = () => {
    api.adminAmlAlerts(alertFilter, 1, 50)
      .then((r) => {
        setAlerts(r.items)
        setAlertsTotal(r.total)
      })
      .catch(() => { /* non-fatal */ })
  }

  const toggleDocs = async (userId: string) => {
    if (expandedUserId === userId) {
      setExpandedUserId(null)
      setExpandedDocs([])
      return
    }
    setExpandedUserId(userId)
    try {
      setExpandedDocs(await api.adminCustomerDocuments(userId))
    } catch {
      setExpandedDocs([])
    }
  }

  const loadRuns = () => {
    api.adminBatchRuns(10).then(setRuns).catch(() => { /* non-fatal */ })
  }

  const loadTrialBalance = () => {
    setTbBusy(true)
    api.adminTrialBalance()
      .then(setTrialBalance)
      .catch(() => setAdminError('Failed to load trial balance'))
      .finally(() => setTbBusy(false))
  }

  useEffect(() => {
    loadAudit()
    loadRuns()
    loadTrialBalance()
    loadKycQueue()
    loadLoans()
    loadOverdrafts()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    loadAlerts()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [alertFilter])

  useEffect(() => {
    loadLoans()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loanFilter])

  useEffect(() => {
    loadOverdrafts()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [odFilter])

  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    setAdminError(null)
    setAdminMsg(null)
    try {
      await fn()
      setAdminMsg(ok)
      loadAudit()
    } catch (err) {
      setAdminError(err instanceof ApiError ? err.message : 'Operation failed')
    } finally {
      setBusy(false)
    }
  }

  const submitReversal = async (e: FormEvent) => {
    e.preventDefault()
    await run(
      () => api.adminReverse(txId.trim(), txDesc || 'Admin reversal', crypto.randomUUID()),
      'Reversal posted (or replayed).',
    )
    setTxId('')
    setTxDesc('')
  }

  const runBatch = async (fn: () => Promise<BatchRunDto>) => {
    setBatchBusy(true)
    setAdminError(null)
    try {
      const result = await fn()
      setBatchResult(result)
      loadRuns()
      if (result.status === 'Succeeded') loadTrialBalance()
      if (result.status === 'Partial' || result.status === 'Failed')
        setAdminError(`Batch finished with ${result.itemsFailed} failed item(s) — see run details.`)
    } catch (err) {
      setAdminError(err instanceof ApiError ? err.message : 'Batch failed to start')
    } finally {
      setBatchBusy(false)
    }
  }

  const [y, m] = batchDate.split('-').map(Number)

  const approveKyc = (userId: string) =>
    run(() => api.adminApproveKyc(userId), 'Customer verified.').then(loadKycQueue)
  const rejectKyc = (userId: string) => {
    const reason = rejectReason.trim() || 'Rejected by compliance.'
    return run(() => api.adminRejectKyc(userId, reason), 'Customer rejected.')
      .then(() => { setRejectReason(''); loadKycQueue() })
  }
  const reviewAlert = (id: string) =>
    run(() => api.adminReviewAlert(id, alertNote || undefined), 'Alert marked reviewed.')
      .then(() => { setAlertNote(''); loadAlerts() })
  const dismissAlert = (id: string) =>
    run(() => api.adminDismissAlert(id, alertNote || undefined), 'Alert dismissed.')
      .then(() => { setAlertNote(''); loadAlerts() })

  return (
    <div className="admin-grid">
      <section className="card">
        <h2 className="section-title">Account lifecycle</h2>
        {adminError && <div className="alert">{adminError}</div>}
        {adminMsg && <div className="alert alert-ok">{adminMsg}</div>}
        <label className="field">
          <span>Account ID (UUID)</span>
          <input value={accountId} onChange={(e) => setAccountId(e.target.value)}
            placeholder="00000000-0000-0000-0000-000000000000" className="mono small" required />
        </label>
        <label className="field">
          <span>Reason <small>(optional, audited)</small></span>
          <input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />
        </label>
        <div className="btn-row">
          <button className="btn btn-warn" disabled={busy || !accountId}
            onClick={() => run(() => api.adminFreeze(accountId.trim(), reason || undefined), 'Account frozen.')}>
            Freeze
          </button>
          <button className="btn btn-primary" disabled={busy || !accountId}
            onClick={() => run(() => api.adminUnfreeze(accountId.trim(), reason || undefined), 'Account reactivated.')}>
            Unfreeze
          </button>
          <button className="btn btn-danger" disabled={busy || !accountId}
            onClick={() => run(() => api.adminClose(accountId.trim(), reason || undefined), 'Account closed.')}>
            Close (zero balance)
          </button>
        </div>
      </section>

      <section className="card">
        <h2 className="section-title">Reverse a transaction</h2>
        <p className="muted">Admin-only. Posts mirror legs; the original is marked Reversed. Overdrafts are allowed and flagged in audit.</p>
        <form onSubmit={submitReversal}>
          <label className="field">
            <span>Transaction ID (UUID)</span>
            <input value={txId} onChange={(e) => setTxId(e.target.value)} required className="mono small" />
          </label>
          <label className="field">
            <span>Description</span>
            <input value={txDesc} onChange={(e) => setTxDesc(e.target.value)} maxLength={500} placeholder="Reason for reversal" />
          </label>
          <button className="btn btn-danger btn-block" disabled={busy || !txId}>
            {busy ? 'Working…' : 'Reverse transaction'}
          </button>
        </form>
      </section>

      <section className="card loan-portfolio-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>Loan portfolio</h2>
          <div className="btn-row" style={{ alignItems: 'center' }}>
            <select value={loanFilter} onChange={(e) => setLoanFilter(e.target.value as typeof loanFilter)}>
              <option value="Active">Active</option>
              <option value="Approved">Approved</option>
              <option value="Completed">Completed</option>
              <option value="Rejected">Rejected</option>
              <option value="Cancelled">Cancelled</option>
              <option value="">All</option>
            </select>
            <span className="muted">{loanTotal} total</span>
          </div>
        </div>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Reference</th><th>Customer</th><th>Product</th><th className="num">Principal</th><th className="num">Paid</th><th className="num">Overdue</th><th className="num">Outstanding</th></tr>
            </thead>
            <tbody>
              {loanRows.map((l) => (
                <tr key={l.id}>
                  <td className="mono small">{l.reference}</td>
                  <td className="small">{l.customer}</td>
                  <td>{l.product}</td>
                  <td className="num mono">{l.principal.toLocaleString('en-US', { style: 'currency', currency: 'USD' })}</td>
                  <td className="num mono">{l.installmentsPaid}/{l.installmentsTotal}</td>
                  <td className={`num mono ${l.overdueInstallments > 0 ? 'neg' : ''}`}>{l.overdueInstallments}</td>
                  <td className="num mono">{l.outstanding.toLocaleString('en-US', { style: 'currency', currency: 'USD' })}</td>
                </tr>
              ))}
              {loanRows.length === 0 && <tr><td colSpan={7} className="muted center">No loans in this view</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card loan-portfolio-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>Overdraft facilities</h2>
          <div className="btn-row" style={{ alignItems: 'center' }}>
            <select value={odFilter} onChange={(e) => setOdFilter(e.target.value as typeof odFilter)}>
              <option value="Active">Active</option>
              <option value="Closed">Closed</option>
              <option value="Rejected">Rejected</option>
              <option value="">All</option>
            </select>
            <span className="muted">{odTotal} total</span>
          </div>
        </div>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Reference</th><th>Customer</th><th>Account</th><th>Product</th><th className="num">Limit</th><th className="num">Used</th><th className="num">Available</th><th>Pledge</th><th>Status</th></tr>
            </thead>
            <tbody>
              {odRows.map((f) => (
                <tr key={f.id}>
                  <td className="mono small">{f.reference}</td>
                  <td className="small">{f.customer}</td>
                  <td className="mono small">{f.accountNumber}</td>
                  <td><span className="mono">{f.productCode}</span></td>
                  <td className="num mono">{f.limit.toFixed(2)}</td>
                  <td className="num mono">{f.utilized.toFixed(2)}</td>
                  <td className="num mono">{f.available.toFixed(2)}</td>
                  <td className="mono small">{f.collateralAccountNumber ?? '—'}</td>
                  <td><span className={`pill ${f.status === 'Active' ? 'pill-active' : f.status === 'Rejected' ? 'pill-rejected' : 'pill-closed'}`}>{f.status}</span></td>
                </tr>
              ))}
              {odRows.length === 0 && <tr><td colSpan={9} className="muted center">No facilities in this view</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card kyc-queue-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>KYC review queue</h2>
          <span className="muted">{kycTotal} pending</span>
        </div>
        <label className="field">
          <span>Rejection reason <small>(used by Reject)</small></span>
          <input value={rejectReason} onChange={(e) => setRejectReason(e.target.value)}
            maxLength={500} placeholder="e.g. Document unreadable / name mismatch" />
        </label>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Customer</th><th>ID</th><th>Nationality</th><th>Risk</th><th className="num">Docs</th><th className="num">Actions</th></tr>
            </thead>
            <tbody>
              {kycQueue.map((p) => (
                <Fragment key={p.userId}>
                  <tr>
                    <td>{p.fullLegalName}<br /><span className="muted small">{p.email}</span></td>
                    <td className="mono small">{p.idType} {p.idNumber}</td>
                    <td>{p.nationality}</td>
                    <td><span className={`pill ${p.riskRating === 'High' ? 'pill-rejected' : p.riskRating === 'Medium' ? 'pill-frozen' : 'pill-active'}`}>{p.riskRating}</span></td>
                    <td className="num">
                      <button className="link" onClick={() => toggleDocs(p.userId)}>{p.documentCount} ▾</button>
                    </td>
                    <td className="num">
                      <button className="btn btn-primary btn-small" disabled={busy} onClick={() => approveKyc(p.userId)}>Approve</button>
                      <button className="btn btn-danger btn-small" disabled={busy} onClick={() => rejectKyc(p.userId)} style={{ marginLeft: 6 }}>Reject</button>
                    </td>
                  </tr>
                  {expandedUserId === p.userId && (
                    <tr>
                      <td colSpan={6}>
                        {expandedDocs.length === 0
                          ? <span className="muted">No documents uploaded</span>
                          : expandedDocs.map((d) => (
                              <div key={d.id} className="doc-row">
                                <span className="small">{d.docType} · {d.fileName} · {(d.sizeBytes / 1024).toFixed(1)} KB</span>
                                <button className="link" onClick={() => api.kycDownloadDocument(d.id, d.fileName)}>Download</button>
                              </div>
                            ))}
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
              {kycQueue.length === 0 && <tr><td colSpan={6} className="muted center">Queue is empty — nothing to review</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card aml-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>AML alerts</h2>
          <div className="btn-row" style={{ alignItems: 'center' }}>
            <select value={alertFilter} onChange={(e) => setAlertFilter(e.target.value as typeof alertFilter)}>
              <option value="Open">Open</option>
              <option value="Reviewed">Reviewed</option>
              <option value="Dismissed">Dismissed</option>
              <option value="">All</option>
            </select>
            <span className="muted">{alertsTotal} total</span>
          </div>
        </div>
        <label className="field">
          <span>Review note <small>(applied to the next Review/Dismiss click)</small></span>
          <input value={alertNote} onChange={(e) => setAlertNote(e.target.value)} maxLength={1000}
            placeholder="e.g. Customer confirmed source of funds" />
        </label>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>When</th><th>Type</th><th>Severity</th><th>User</th><th>Details</th>
                {alertFilter === 'Open' && <th className="num">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {alerts.map((a) => (
                <tr key={a.id}>
                  <td className="mono small">{new Date(a.createdAt).toISOString().slice(0, 16).replace('T', ' ')}</td>
                  <td className="mono">{a.alertType}</td>
                  <td><span className={`pill ${a.severity === 'High' ? 'pill-rejected' : a.severity === 'Medium' ? 'pill-frozen' : 'pill-active'}`}>{a.severity}</span></td>
                  <td className="small">{a.userEmail}</td>
                  <td className="small aml-details">{a.details}</td>
                  {alertFilter === 'Open' && (
                    <td className="num">
                      <button className="btn btn-ghost btn-small" onClick={() => reviewAlert(a.id)}>Review</button>
                      <button className="btn btn-ghost btn-small" onClick={() => dismissAlert(a.id)} style={{ marginLeft: 6 }}>Dismiss</button>
                    </td>
                  )}
                </tr>
              ))}
              {alerts.length === 0 && <tr><td colSpan={alertFilter === 'Open' ? 6 : 5} className="muted center">No alerts</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card batch-card">
        <h2 className="section-title">Day-end batch</h2>
        <p className="muted">Accrues daily interest (USD-normalized GL), capitalizes on month-end, charges monthly fees on the 1st. Each job runs at most once per day per job type.</p>
        <div className="filters">
          <label className="field">
            <span>Batch date</span>
            <input type="date" value={batchDate} onChange={(e) => setBatchDate(e.target.value)} />
          </label>
        </div>
        <div className="btn-row">
          <button className="btn btn-primary" disabled={batchBusy}
            onClick={() => runBatch(() => api.adminRunDayEnd(batchDate))}>
            {batchBusy ? 'Running…' : 'Run day-end'}
          </button>
          <button className="btn btn-ghost" disabled={batchBusy}
            onClick={() => runBatch(() => api.adminCapitalizeInterest(batchDate))}>
            Capitalize interest
          </button>
          <button className="btn btn-ghost" disabled={batchBusy}
            onClick={() => runBatch(() => api.adminChargeFees(y, m))}>
            Charge fees
          </button>
        </div>
        {batchResult && (
          <div className={`batch-result ${batchResult.status === 'Succeeded' ? 'ok' : 'warn'}`}>
            <strong>{batchResult.jobType}</strong> · {runDate(batchResult.runDate)} · {batchResult.status}
            {batchResult.replayed && ' (replayed — no changes)'}<br />
            processed {batchResult.itemsProcessed} · failed {batchResult.itemsFailed}
            {batchResult.summary && (
              <>
                <br />
                <span className="mono small">{JSON.stringify(batchResult.summary)}</span>
              </>
            )}
            {batchResult.errors && (
              <>
                <br />
                <code className="small">{JSON.stringify(batchResult.errors)}</code>
              </>
            )}
          </div>
        )}
        {runs.length > 0 && (
          <div className="table-wrap" style={{ marginTop: 12 }}>
            <table className="table">
              <thead>
                <tr><th>Job</th><th>Date</th><th>Status</th><th className="num">OK</th><th className="num">Fail</th></tr>
              </thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.id}>
                    <td>{r.jobType}</td>
                    <td className="mono small">{runDate(r.runDate)}</td>
                    <td className={r.status === 'Succeeded' ? 'pos' : 'neg'}>{r.status}</td>
                    <td className="num mono">{r.itemsProcessed}</td>
                    <td className="num mono">{r.itemsFailed}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="card trial-balance-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>Trial balance (USD)</h2>
          <button className="btn btn-ghost" onClick={loadTrialBalance} disabled={tbBusy}>
            {tbBusy ? 'Loading…' : 'Refresh'}
          </button>
        </div>
        {trialBalance && (
          <>
            <div className={`batch-result ${trialBalance.balanced ? 'ok' : 'warn'}`} style={{ marginBottom: 12 }}>
              {trialBalance.balanced ? '✔ Ledger is balanced' : '✖ LEDGER DOES NOT BALANCE — investigate immediately'}
            </div>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr><th>Code</th><th>Account</th><th>Type</th><th className="num">Debit</th><th className="num">Credit</th></tr>
                </thead>
                <tbody>
                  {trialBalance.lines.map((l) => (
                    <tr key={l.code}>
                      <td className="mono">{l.code}</td>
                      <td>{l.name}</td>
                      <td>{l.type}</td>
                      <td className="num mono">{l.debit.toFixed(4)}</td>
                      <td className="num mono">{l.credit.toFixed(4)}</td>
                    </tr>
                  ))}
                  <tr>
                    <td colSpan={3}><strong>Totals</strong></td>
                    <td className="num mono"><strong>{trialBalance.totalDebits.toFixed(4)}</strong></td>
                    <td className="num mono"><strong>{trialBalance.totalCredits.toFixed(4)}</strong></td>
                  </tr>
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>

      <section className="card audit-card">
        <div className="dashboard-head">
          <h2 className="section-title" style={{ margin: 0 }}>Recent audit log</h2>
          <span className="muted">{auditTotal} total</span>
        </div>
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>When (UTC)</th><th>Action</th><th>Entity</th><th>IP</th></tr>
            </thead>
            <tbody>
              {audit.map((a) => (
                <tr key={a.id}>
                  <td className="mono small">{new Date(a.createdAt).toISOString().replace('T', ' ').slice(0, 19)}</td>
                  <td className="mono">{a.action}</td>
                  <td className="mono small">{a.entityName}{a.entityId ? ` · ${a.entityId.slice(0, 8)}…` : ''}</td>
                  <td className="mono small">{a.ipAddress ?? '—'}</td>
                </tr>
              ))}
              {audit.length === 0 && <tr><td colSpan={4} className="muted center">No audit entries</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}
