import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'

export default function Login({ onAuthed, onSwitch }: { onAuthed: () => void; onSwitch: () => void }) {
  const [email, setEmail] = useState('demo@bank.com')
  const [password, setPassword] = useState('Demo123!')
  const [totpCode, setTotpCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.login(email, password, totpCode || undefined)
      onAuthed()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unable to sign in. Is the API up?')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="auth-layout">
      <form className="card auth-card" onSubmit={submit}>
        <h1 className="auth-title">Sign in</h1>
        <p className="auth-sub">Access your Meridian accounts</p>

        {error && <div className="alert">{error}</div>}

        <label className="field">
          <span>Email</span>
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus />
        </label>
        <label className="field">
          <span>Password</span>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required autoComplete="current-password" />
        </label>
        <label className="field">
          <span>2FA code <small>(only if enabled)</small></span>
          <input value={totpCode} onChange={(e) => setTotpCode(e.target.value)}
            inputMode="numeric" pattern="\d{6}" maxLength={6} placeholder="123456"
            autoComplete="one-time-code" className="mono" />
        </label>

        <button className="btn btn-primary btn-block" disabled={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <p className="auth-foot">
          No account? <button type="button" className="link" onClick={onSwitch}>Create one</button>
        </p>
        <p className="demo-hint">Demo: demo@bank.com / Demo123!</p>
      </form>
    </div>
  )
}
