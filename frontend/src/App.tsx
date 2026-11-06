import { useEffect, useState } from 'react'
import { api, getAccessToken, type MePayload } from './api'
import Login from './components/Login'
import Register from './components/Register'
import Dashboard from './components/Dashboard'
import Transfer from './components/Transfer'
import History from './components/History'
import Security from './components/Security'
import Admin from './components/Admin'
import Profile from './components/Profile'
import Loans from './components/Loans'

type View = 'login' | 'register' | 'app'
type Tab = 'overview' | 'transfer' | 'loans' | 'history' | 'profile' | 'security' | 'admin'

export default function App() {
  const [view, setView] = useState<View>(() => (getAccessToken() ? 'app' : 'login'))
  const [tab, setTab] = useState<Tab>('overview')
  const [me, setMe] = useState<MePayload | null>(null)

  useEffect(() => {
    const onUnauthorized = () => {
      setMe(null)
      setView('login')
    }
    window.addEventListener('meridian:unauthorized', onUnauthorized)
    return () => window.removeEventListener('meridian:unauthorized', onUnauthorized)
  }, [])

  useEffect(() => {
    if (view === 'app') {
      api.me().then(setMe).catch(() => setView('login'))
    }
  }, [view])

  const handleAuthed = () => setView('app')
  const handleLogout = async () => {
    await api.logout()
    setMe(null)
    setView('login')
  }

  const isAdmin = me?.role === 'Admin'

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">M</span>
          <span className="brand-name">Meridian<span className="brand-sub">Core Banking</span></span>
        </div>
        {view === 'app' && (
          <div className="topbar-right">
            {me && <span className="topbar-user">{me.fullName}{isAdmin ? ' · admin' : ''}</span>}
            <button className="btn btn-ghost" onClick={handleLogout}>Sign out</button>
          </div>
        )}
      </header>

      {view === 'app' && (
        <nav className="tabbar" aria-label="Primary">
          <button className={tab === 'overview' ? 'tab active' : 'tab'} onClick={() => setTab('overview')}>Overview</button>
          <button className={tab === 'transfer' ? 'tab active' : 'tab'} onClick={() => setTab('transfer')}>Move money</button>
          <button className={tab === 'loans' ? 'tab active' : 'tab'} onClick={() => setTab('loans')}>Loans</button>
          <button className={tab === 'history' ? 'tab active' : 'tab'} onClick={() => setTab('history')}>History</button>
          <button className={tab === 'profile' ? 'tab active' : 'tab'} onClick={() => setTab('profile')}>KYC</button>
          <button className={tab === 'security' ? 'tab active' : 'tab'} onClick={() => setTab('security')}>Security</button>
          {isAdmin && <button className={tab === 'admin' ? 'tab active tab-admin' : 'tab tab-admin'} onClick={() => setTab('admin')}>Admin</button>}
        </nav>
      )}

      {view === 'app' && me && me.kycStatus !== 'Verified' && (
        <div className="kyc-banner" role="alert">
          <span>
            <strong>KYC verification required.</strong> Until our compliance team approves your identity, the bank can't process transactions.
          </span>
          <button className="btn btn-primary" onClick={() => setTab('profile')}>Complete KYC</button>
        </div>
      )}

      <main className="main">
        {view === 'login' && <Login onAuthed={handleAuthed} onSwitch={() => setView('register')} />}
        {view === 'register' && <Register onAuthed={handleAuthed} onSwitch={() => setView('login')} />}
        {view === 'app' && tab === 'overview' && <Dashboard />}
        {view === 'app' && tab === 'transfer' && <Transfer />}
        {view === 'app' && tab === 'loans' && <Loans />}
        {view === 'app' && tab === 'history' && <History />}
        {view === 'app' && tab === 'profile' && me && (
          <Profile onStatusChanged={() => api.me().then(setMe).catch(() => { /* non-fatal */ })} />
        )}
        {view === 'app' && tab === 'security' && me && (
          <Security twoFactorEnabled={me.twoFactorEnabled} on2faChanged={() => api.me().then(setMe)} />
        )}
        {view === 'app' && tab === 'admin' && isAdmin && <Admin />}
      </main>

      <footer className="footer">Meridian Core Banking · demo environment — do not use real credentials</footer>
    </div>
  )
}
