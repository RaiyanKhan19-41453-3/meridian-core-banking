import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'

export default function Security({
  twoFactorEnabled,
  on2faChanged,
}: {
  twoFactorEnabled: boolean
  on2faChanged: () => void
}) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [passwordMsg, setPasswordMsg] = useState<string | null>(null)
  const [passwordError, setPasswordError] = useState<string | null>(null)
  const [changing, setChanging] = useState(false)

  const [setup, setSetup] = useState<{ secretBase32: string; otpAuthUri: string } | null>(null)
  const [totpCode, setTotpCode] = useState('')
  const [factorMsg, setFactorMsg] = useState<string | null>(null)
  const [factorError, setFactorError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const changePassword = async (e: FormEvent) => {
    e.preventDefault()
    setChanging(true)
    setPasswordError(null)
    setPasswordMsg(null)
    try {
      await api.changePassword(currentPassword, newPassword)
      setPasswordMsg('Password changed. All other sessions were signed out — please sign in again.')
      setCurrentPassword('')
      setNewPassword('')
    } catch (err) {
      setPasswordError(err instanceof ApiError ? err.message : 'Failed to change password')
    } finally {
      setChanging(false)
    }
  }

  const startSetup = async () => {
    setBusy(true)
    setFactorError(null)
    setFactorMsg(null)
    try {
      setSetup(await api.setup2fa())
    } catch (err) {
      setFactorError(err instanceof ApiError ? err.message : 'Setup failed')
    } finally {
      setBusy(false)
    }
  }

  const enable = async () => {
    setBusy(true)
    setFactorError(null)
    try {
      await api.enable2fa(totpCode)
      setFactorMsg('Two-factor authentication is now enabled.')
      setSetup(null)
      setTotpCode('')
      on2faChanged()
    } catch (err) {
      setFactorError(err instanceof ApiError ? err.message : 'Invalid code')
    } finally {
      setBusy(false)
    }
  }

  const disable = async () => {
    setBusy(true)
    setFactorError(null)
    try {
      await api.disable2fa(totpCode)
      setFactorMsg('Two-factor authentication disabled. All sessions were signed out.')
      setTotpCode('')
      on2faChanged()
    } catch (err) {
      setFactorError(err instanceof ApiError ? err.message : 'Invalid code')
    } finally {
      setBusy(false)
    }
  }

  const signOutEverywhere = async () => {
    setBusy(true)
    try {
      await api.logoutAll()
    } finally {
      // Local tokens are dead now either way
      localStorage.removeItem('meridian.access')
      localStorage.removeItem('meridian.refresh')
      window.dispatchEvent(new Event('meridian:unauthorized'))
    }
  }

  return (
    <div className="security-grid">
      <section className="card">
        <h2 className="section-title">Password</h2>
        {passwordError && <div className="alert">{passwordError}</div>}
        {passwordMsg && <div className="alert alert-ok">{passwordMsg}</div>}
        <form onSubmit={changePassword}>
          <label className="field">
            <span>Current password</span>
            <input type="password" value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} required autoComplete="current-password" />
          </label>
          <label className="field">
            <span>New password <small>(8+ chars, 1 digit, 1 letter)</small></span>
            <input type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} minLength={8} required autoComplete="new-password" />
          </label>
          <button className="btn btn-primary btn-block" disabled={changing}>
            {changing ? 'Updating…' : 'Change password'}
          </button>
        </form>
      </section>

      <section className="card">
        <h2 className="section-title">Two-factor authentication (TOTP)</h2>
        {factorError && <div className="alert">{factorError}</div>}
        {factorMsg && <div className="alert alert-ok">{factorMsg}</div>}

        {twoFactorEnabled ? (
          <>
            <p className="muted">Two-factor authentication is <strong className="ok-text">enabled</strong>. Codes are required at sign-in.</p>
            <label className="field">
              <span>Current 6-digit code to disable</span>
              <input value={totpCode} onChange={(e) => setTotpCode(e.target.value)}
                inputMode="numeric" pattern="\d{6}" maxLength={6} placeholder="123456" className="mono" />
            </label>
            <button className="btn btn-danger btn-block" onClick={disable} disabled={busy || totpCode.length !== 6}>
              {busy ? 'Working…' : 'Disable 2FA'}
            </button>
          </>
        ) : !setup ? (
          <>
            <p className="muted">Add a time-based one-time code from an authenticator app (Google Authenticator, Aegis, 1Password…).</p>
            <button className="btn btn-primary btn-block" onClick={startSetup} disabled={busy}>
              {busy ? 'Generating…' : 'Set up 2FA'}
            </button>
          </>
        ) : (
          <>
            <p className="muted">Scan/enter this secret in your authenticator, then confirm with the 6-digit code:</p>
            <div className="secret-box mono">{setup.secretBase32}</div>
            <details className="otpauth">
              <summary>otpauth URI (advanced)</summary>
              <code>{setup.otpAuthUri}</code>
            </details>
            <label className="field">
              <span>Confirmation code</span>
              <input value={totpCode} onChange={(e) => setTotpCode(e.target.value)}
                inputMode="numeric" pattern="\d{6}" maxLength={6} placeholder="123456" autoFocus className="mono" />
            </label>
            <button className="btn btn-primary btn-block" onClick={enable} disabled={busy || totpCode.length !== 6}>
              {busy ? 'Enabling…' : 'Enable 2FA'}
            </button>
          </>
        )}
      </section>

      <section className="card">
        <h2 className="section-title">Sessions</h2>
        <p className="muted">Sign out on every device and revoke all refresh tokens issued to you.</p>
        <button className="btn btn-ghost btn-block" onClick={signOutEverywhere} disabled={busy}>
          Sign out everywhere
        </button>
      </section>
    </div>
  )
}
