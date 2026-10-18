import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from './auth-context'
import { returnPath } from './navigation'

export function SessionCheck() {
  const { session, refresh } = useAuth()
  return <section className="session-panel" aria-live="polite">
    <h1>{session.status === 'error' ? 'Let’s try that again.' : 'Getting your workspace ready…'}</h1>
    {session.status === 'error' ? <>
      <p role="alert">{session.message}</p>
      <button className="button primary" onClick={() => void refresh()}>Try again</button>
    </> : <p role="status">Checking your session…</p>}
  </section>
}

export function ProtectedRoute() {
  const { session } = useAuth()
  const location = useLocation()
  if (session.status === 'checking' || session.status === 'error') return <SessionCheck />
  if (session.status !== 'authenticated') return <Navigate to="/login" replace state={{
    returnTo: location.pathname + location.search + location.hash, notice: session.message,
  }} />
  return <Outlet />
}

export function GuestRoute() {
  const { session } = useAuth()
  const location = useLocation()
  if (session.status === 'checking' || session.status === 'error') return <SessionCheck />
  return session.status === 'authenticated' ? <Navigate to={returnPath(location.state?.returnTo)} replace /> : <Outlet />
}
