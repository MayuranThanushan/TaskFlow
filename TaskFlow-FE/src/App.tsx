import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, Route, Routes, useLocation } from 'react-router-dom'
import { api } from './api/client'
import type { ApiClient } from './api/client'
import { ProjectsPage, NewProjectPage, ProjectDetailPage, EditProjectPage } from './features/projects/ProjectPages'
import { AuthProvider } from './features/auth/AuthProvider'
import { useAuth } from './features/auth/auth-context'
import { GuestRoute, ProtectedRoute } from './features/auth/RouteGates'
import { AuthPage } from './features/auth/AuthPage'
import { TasksPage, NewTaskPage, TaskDetailPage, EditTaskPage } from './features/tasks/TaskPages'
import './App.css'

function AppLayout() {
  const { session, signOut, refresh } = useAuth()
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const lock = useRef(false)
  const location = useLocation()
  const previousPath = useRef(location.pathname)
  useEffect(() => {
    const titles: Record<string, string> = { '/login': 'Sign in', '/register': 'Create account', '/dashboard': 'Overview', '/projects': 'Projects', '/projects/new': 'Create project', '/': 'A clearer way to work' }
    document.title = `${titles[location.pathname] ?? (location.pathname.startsWith('/tasks/') ? 'Task' : location.pathname.endsWith('/tasks') ? 'Tasks' : location.pathname.endsWith('/tasks/new') ? 'Create task' : location.pathname.startsWith('/projects/') ? 'Project' : 'Page not found')} | TaskFlow`
    if (previousPath.current !== location.pathname) document.getElementById('main-content')?.focus()
    previousPath.current = location.pathname
  }, [location.pathname])

  const logout = async () => {
    if (lock.current) return
    lock.current = true
    setPending(true)
    setError('')
    try { await signOut() }
    catch { setError('We could not sign you out. Please try again.') }
    finally { lock.current = false; setPending(false) }
  }

  return <div className="app-shell">
    <a className="skip-link" href="#main-content">Skip to content</a>
    <header className="app-header">
      <Link className="brand" to={session.status === 'authenticated' ? '/dashboard' : '/'} aria-label="TaskFlow home"><span className="brand-mark" aria-hidden="true">T</span>TaskFlow</Link>
      <nav aria-label="Main navigation">
        {session.status === 'authenticated' ? <>
          <NavLink to="/dashboard">Overview</NavLink>
          <NavLink to="/projects">Projects</NavLink>
          <span className="user-name">{session.user.displayName}</span>
          <button className="button subtle" disabled={pending} onClick={() => void logout()}>{pending ? 'Signing out...' : 'Sign out'}</button>
        </> : session.status === 'anonymous' ? <>
          <NavLink to="/login">Sign in</NavLink>
          <Link className="button small primary" to="/register">Create account</Link>
        </> : null}
      </nav>
    </header>
    {error && session.status === 'authenticated' && <p className="error-summary" role="alert">{error}</p>}
    {session.status === 'authenticated' && session.message && <div className="connection-banner" role="status">{session.message}{' '}
      <button className="text-button" onClick={() => void refresh()}>Try again</button>
    </div>}
    <main id="main-content" tabIndex={-1}><Outlet /></main>
    <footer className="app-footer"><span>TaskFlow</span><span>A clearer way to work.</span></footer>
  </div>
}

function HomePage() {
  const { session } = useAuth()
  return <section className="welcome">
    <p className="eyebrow">Your work. A little more organized.</p>
    <h1>Keep your<br />work moving.</h1>
    <p className="intro">Make space for what matters. Bring your projects, tasks, and team into one place.</p>
    <div className="home-actions"><Link className="button primary" to={session.status === 'authenticated' ? '/dashboard' : '/register'}>{session.status === 'authenticated' ? 'Open your workspace' : 'Create your account'}</Link>
      {session.status !== 'authenticated' && <Link className="text-link" to="/login">Already here? Sign in <span aria-hidden="true">&rarr;</span></Link>}</div>
    <div className="home-note"><span className="note-dot" />A fresh start for your next idea.</div>
  </section>
}

function DashboardPage() {
  const { session } = useAuth()
  return <section className="dashboard">
    <p className="eyebrow">Your workspace</p>
    <h1>Welcome back{session.user ? `, ${session.user.displayName}` : ''}.</h1>
    <p className="intro">A clear view of what comes next.</p><p className="muted">Your account ID: {session.user?.id}</p>
    <section className="empty-workspace" aria-labelledby="workspace-title">
      <span className="empty-icon" aria-hidden="true">+</span>
      <h2 id="workspace-title">Room for your next idea.</h2>
      <p>Give your next idea a place to grow.</p><Link className="button primary" to="/projects">Browse projects</Link>
    </section>
  </section>
}

function NotFoundPage() {
  return <section className="session-panel"><p className="eyebrow">404</p><h1>That page is not here.</h1>
    <p>The link may have changed. Let us get you back on track.</p><Link className="button primary" to="/">Back to TaskFlow</Link></section>
}

export default function App({ client = api }: { client?: ApiClient }) {
  return <AuthProvider client={client}><Routes><Route element={<AppLayout />}>
    <Route index element={<HomePage />} />
    <Route element={<GuestRoute />}>
      <Route path="login" element={<AuthPage key="login" mode="login" />} />
      <Route path="register" element={<AuthPage key="register" mode="register" />} />
    </Route>
    <Route element={<ProtectedRoute />}>
      <Route path="dashboard" element={<DashboardPage />} />
      <Route path="projects" element={<ProjectsPage client={client} />} />
      <Route path="projects/new" element={<NewProjectPage client={client} />} />
      <Route path="projects/:projectId/tasks" element={<TasksPage client={client} />} />
      <Route path="projects/:projectId/tasks/new" element={<NewTaskPage client={client} />} />
      <Route path="tasks/:taskId/edit" element={<EditTaskPage client={client} />} />
      <Route path="tasks/:taskId" element={<TaskDetailPage client={client} />} />
      <Route path="projects/:projectId/edit" element={<EditProjectPage client={client} />} />
      <Route path="projects/:projectId" element={<ProjectDetailPage client={client} />} />
    </Route>
    <Route path="*" element={<NotFoundPage />} />
  </Route></Routes></AuthProvider>
}
