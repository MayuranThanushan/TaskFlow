import { lazy, Suspense } from 'react'
import type { ApiClient } from '../../api/client'
import { LazyPanelBoundary } from '../../components/LazyPanelBoundary'
import { Link, useNavigate, useParams } from '../companies/navigation'
const BoardPage = lazy(() => import('./BoardPage').then(module => ({ default: module.BoardPage })))
export function BoardEntry({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams(), navigate = useNavigate()
  const list = '/projects/' + encodeURIComponent(projectId) + '/tasks'
  return <LazyPanelBoundary key={client.resourceKey + ':' + projectId} headingLevel={1} title="The board view could not load." message="Reload this page to get the current version, or continue in the task list." reloadLabel="Reload board view" fallbackLabel="Open task list" onFallback={() => void navigate(list)}>
    <Suspense fallback={<section className="projects-page"><h1>Project board</h1><p role="status">Loading board view...</p><Link to={list}>Open task list</Link></section>}><BoardPage client={client} /></Suspense>
  </LazyPanelBoundary>
}
