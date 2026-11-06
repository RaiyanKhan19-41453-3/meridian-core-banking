import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'

export default function Register({ onAuthed, onSwitch }: { onAuthed: () => void; onSwitch: () => void }) {
  const [fullName, setFullName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.register(email, password, fullName)
      onAuthed()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unable to register. Is the API up?')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="auth-layout">
      <form className="card auth-card" onSubmit={submit}>
        <h1 className="auth-title">Create account</h1>
        <p className="auth-sub">Open your Meridian banking profile</p>

        {error && <div className="alert">{error}</div>}

        <label className="field">
          <span>Full name</span>
          <input value={fullName} onChange={(e) => setFullName(e.target.value)} required autoFocus />
        </label>
        <label className="field">
          <span>Email</span>
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </label>
        <label className="field">
          <span>Password <small>(min 8 chars)</small></span>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} minLength={8} required />
        </label>

        <button className="btn btn-primary btn-block" disabled={busy}>
          {busy ? 'Creating…' : 'Create account'}
        </button>

        <p className="auth-foot">
          Already registered? <button type="button" className="link" onClick={onSwitch}>Sign in</button>
        </p>
      </form>
    </div>
  )
}
