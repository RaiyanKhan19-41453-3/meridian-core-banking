import { useEffect, useState, type FormEvent, type ChangeEvent } from 'react'
import { api, ApiError, type KycProfilePayload, type KycProfileInput, type KycDocumentMeta } from '../api'

const INCOME_BANDS = ['<25k', '25k-50k', '50k-100k', '100k+']
const EMPLOYMENT = ['Employed', 'SelfEmployed', 'BusinessOwner', 'Student', 'Retired', 'Unemployed']

const sizeFmt = (bytes: number) =>
  bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(2)} MB`

export default function Profile({ onStatusChanged }: { onStatusChanged: () => void }) {
  const [loading, setLoading] = useState(true)
  const [profile, setProfile] = useState<KycProfilePayload | null>(null)
  const [form, setForm] = useState<KycProfilePayload | null>(null)
  const [documents, setDocuments] = useState<KycDocumentMeta[]>([])
  const [error, setError] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [docType, setDocType] = useState('NationalId')
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)

  const verified = profile?.kycStatus === 'Verified'

  const reload = async () => {
    const [p, d] = await Promise.all([api.kycProfile(), api.kycDocuments()])
    setProfile(p)
    setForm({ ...p })
    setDocuments(d)
  }

  useEffect(() => {
    setLoading(true)
    reload().catch(() => setError('Failed to load your KYC profile')).finally(() => setLoading(false))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const save = async (e: FormEvent) => {
    e.preventDefault()
    if (!form) return
    setBusy(true)
    setError(null)
    setMsg(null)
    try {
      const updated = await api.kycUpdateProfile({
        fullLegalName: form.fullLegalName,
        dateOfBirth: form.dateOfBirth.slice(0, 10),
        nationality: form.nationality,
        phone: form.phone,
        addressLine: form.addressLine,
        city: form.city,
        country: form.country,
        idType: form.idType as 'NationalId' | 'Passport',
        idNumber: form.idNumber,
        employmentStatus: form.employmentStatus as KycProfileInput['employmentStatus'],
        annualIncomeBand: form.annualIncomeBand,
      })
      setProfile(updated)
      setForm({ ...updated })
      setMsg('Profile saved. It will be reviewed by our compliance team.')
      onStatusChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save profile')
    } finally {
      setBusy(false)
    }
  }

  const pickFile = (e: ChangeEvent<HTMLInputElement>) => setFile(e.target.files?.[0] ?? null)

  const upload = async () => {
    if (!file) return
    setUploading(true)
    setError(null)
    try {
      await api.kycUploadDocument(docType, file)
      setFile(null)
      const input = document.getElementById('kyc-file') as HTMLInputElement | null
      if (input) input.value = ''
      setDocuments(await api.kycDocuments())
      setMsg('Document uploaded.')
      onStatusChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Upload failed')
    } finally {
      setUploading(false)
    }
  }

  const download = async (doc: KycDocumentMeta) => {
    try {
      await api.kycDownloadDocument(doc.id, doc.fileName)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Download failed')
    }
  }

  const remove = async (doc: KycDocumentMeta) => {
    try {
      await api.kycDeleteDocument(doc.id)
      setDocuments(documents.filter((d) => d.id !== doc.id))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Delete failed')
    }
  }

  if (loading) return <p className="muted">Loading…</p>
  if (!profile || !form) return <div className="alert">{error ?? 'Profile unavailable'}</div>

  const statusClass = profile.kycStatus === 'Verified' ? 'pill-active'
    : profile.kycStatus === 'Rejected' ? 'pill-rejected' : 'pill-frozen'

  return (
    <div className="kyc-grid">
      <section className="card kyc-status-card">
        <div className="dashboard-head">
          <h1 className="section-title" style={{ margin: 0 }}>Identity verification</h1>
          <span className={`pill ${statusClass}`}>{profile.kycStatus}</span>
        </div>
        {profile.kycStatus === 'Verified' && (
          <p className="muted">✔ Verified on {profile.verifiedAt ? new Date(profile.verifiedAt).toLocaleDateString() : '—'}. Your profile is locked.</p>
        )}
        {profile.kycStatus === 'Pending' && (
          <p className="muted">Our compliance team is reviewing your dossier. The bank can't process transactions until it's approved — deposit, withdrawal, transfer and new accounts are locked.</p>
        )}
        {profile.kycStatus === 'Rejected' && (
          <div className="alert">
            <strong>Verification rejected.</strong><br />{profile.rejectionReason ?? 'No reason provided. Update your details and documents, then save to resubmit.'}
          </div>
        )}
        {profile.riskRating === 'High' && (
          <div className="alert alert-warn">Your profile is flagged for enhanced review.</div>
        )}
        {error && <div className="alert">{error}</div>}
        {msg && <div className="alert alert-ok">{msg}</div>}

        <form onSubmit={save}>
          <div className="kyc-form-grid">
            <label className="field"><span>Full legal name</span>
              <input value={form.fullLegalName} disabled={verified} required
                onChange={(e) => setForm({ ...form, fullLegalName: e.target.value })} />
            </label>
            <label className="field"><span>Date of birth</span>
              <input type="date" value={form.dateOfBirth.slice(0, 10)} disabled={verified} required
                onChange={(e) => setForm({ ...form, dateOfBirth: e.target.value })} />
            </label>
            <label className="field"><span>Nationality</span>
              <input value={form.nationality} disabled={verified} required
                onChange={(e) => setForm({ ...form, nationality: e.target.value })} />
            </label>
            <label className="field"><span>Phone</span>
              <input value={form.phone} disabled={verified} required autoComplete="tel"
                onChange={(e) => setForm({ ...form, phone: e.target.value })} />
            </label>
            <label className="field field-wide"><span>Address</span>
              <input value={form.addressLine} disabled={verified} required
                onChange={(e) => setForm({ ...form, addressLine: e.target.value })} />
            </label>
            <label className="field"><span>City</span>
              <input value={form.city} disabled={verified} required
                onChange={(e) => setForm({ ...form, city: e.target.value })} />
            </label>
            <label className="field"><span>Country</span>
              <input value={form.country} disabled={verified} required
                onChange={(e) => setForm({ ...form, country: e.target.value })} />
            </label>
            <label className="field"><span>ID type</span>
              <select value={form.idType} disabled={verified}
                onChange={(e) => setForm({ ...form, idType: e.target.value })}>
                <option value="NationalId">National ID</option>
                <option value="Passport">Passport</option>
              </select>
            </label>
            <label className="field"><span>ID number</span>
              <input value={form.idNumber} disabled={verified} required autoComplete="off"
                onChange={(e) => setForm({ ...form, idNumber: e.target.value })} />
            </label>
            <label className="field"><span>Employment</span>
              <select value={form.employmentStatus} disabled={verified}
                onChange={(e) => setForm({ ...form, employmentStatus: e.target.value })}>
                {EMPLOYMENT.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </label>
            <label className="field"><span>Annual income</span>
              <select value={form.annualIncomeBand} disabled={verified}
                onChange={(e) => setForm({ ...form, annualIncomeBand: e.target.value })}>
                {INCOME_BANDS.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </label>
          </div>

          {!verified && (
            <button className="btn btn-primary btn-block" disabled={busy}>
              {busy ? 'Saving…' : profile.kycStatus === 'Rejected' ? 'Save & resubmit' : 'Save profile'}
            </button>
          )}
        </form>
      </section>

      <section className="card kyc-docs-card">
        <h2 className="section-title">Documents</h2>
        <p className="muted">PDF, JPEG or PNG · max 2 MB · stored encrypted at rest (bytea) and never public.</p>

        <div className="upload-row">
          <select value={docType} onChange={(e) => setDocType(e.target.value)} disabled={verified}>
            <option value="NationalId">National ID</option>
            <option value="Passport">Passport</option>
            <option value="ProofOfAddress">Proof of address</option>
            <option value="Selfie">Selfie</option>
          </select>
          <input id="kyc-file" type="file" accept=".pdf,.jpg,.jpeg,.png" onChange={pickFile} disabled={verified || uploading} />
          <button className="btn btn-primary" onClick={upload} disabled={!file || uploading || verified}>
            {uploading ? 'Uploading…' : 'Upload'}
          </button>
        </div>

        <div className="table-wrap" style={{ marginTop: 12 }}>
          <table className="table">
            <thead>
              <tr><th>Type</th><th>File</th><th>Size</th><th className="num">Actions</th></tr>
            </thead>
            <tbody>
              {documents.map((d) => (
                <tr key={d.id}>
                  <td>{d.docType}</td>
                  <td className="small">{d.fileName}</td>
                  <td className="small">{sizeFmt(d.sizeBytes)}</td>
                  <td className="num">
                    <button className="link" onClick={() => download(d)}>Download</button>
                    {!verified && <button className="link link-danger" onClick={() => remove(d)} style={{ marginLeft: 12 }}>Delete</button>}
                  </td>
                </tr>
              ))}
              {documents.length === 0 && <tr><td colSpan={4} className="muted center">No documents yet</td></tr>}
            </tbody>
          </table>
        </div>
        <p className="demo-hint">Document access is logged. Only you and compliance staff can view these files.</p>
      </section>
    </div>
  )
}
