import type { SavedView } from './SavedViews'
export function viewLink(view: SavedView) {
  const path = view.surface === 'work' ? '/work' : '/projects/' + view.projectId + '/' + (view.surface === 'board' ? 'board' : 'tasks')
  const query = new URLSearchParams(view.filters)
  if (view.surface === 'work' && view.projectId) query.set('projectId', view.projectId)
  return path + (query.size ? '?' + query.toString() : '')
}
