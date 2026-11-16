import { lazy, Suspense } from 'react'
import type { ApiClient } from '../../api/client'
import { LazyPanelBoundary } from '../../components/LazyPanelBoundary'
import { useNavigate } from '../companies/navigation'
const WorkPage = lazy(() => import('./WorkPage').then(module => ({ default: module.WorkPage })))
export function WorkEntry({ client }: { client: ApiClient }) {
  const navigate = useNavigate()
  return <LazyPanelBoundary key={client.resourceKey} headingLevel={1} title="My work could not load." message="Reload the current app or continue in your project list." reloadLabel="Reload My work" fallbackLabel="Browse projects" onFallback={() => void navigate('/projects')}><Suspense fallback={<section><h1>My work</h1><p role="status">Opening My work...</p></section>}><WorkPage client={client} /></Suspense></LazyPanelBoundary>
}
