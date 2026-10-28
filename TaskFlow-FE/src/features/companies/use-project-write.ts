import { useEffect, useState } from 'react'
import type { ApiClient } from '../../api/client'
import { useAuth } from '../auth/auth-context'
import { useCompany } from './company-context'
export function useProjectAccess(client: ApiClient, projectId: string, ownerId?: string) {
  const { lifecycleEnabled } = useCompany()
  const { session } = useAuth()
  const identity = session.user?.id ?? ''
  const key = identity + ':' + client.resourceKey + ':' + projectId
  const [access, setAccess] = useState({ key: '', write: false })
  useEffect(() => {
    if (!lifecycleEnabled || !projectId) return
    const controller = new AbortController(); let active = true
    void client.request<{ items: { userId: string; role: string }[] }>({ url: '/projects/' + encodeURIComponent(projectId) + '/members', signal: controller.signal })
      .then(result => { if (active) setAccess({ key, write: result.items.some(m => m.userId === identity && ['Owner', 'Editor', 'Member'].includes(m.role)) }) })
      .catch(() => { if (active) setAccess({ key, write: false }) })
    return () => { active = false; controller.abort() }
  }, [client, identity, key, lifecycleEnabled, projectId])
  return { canWrite: !lifecycleEnabled || ownerId === identity || access.key === key && access.write, loading: lifecycleEnabled && ownerId !== identity && access.key !== key }
}

export function useProjectWrite(client: ApiClient, projectId: string, ownerId?: string) { return useProjectAccess(client, projectId, ownerId).canWrite }
