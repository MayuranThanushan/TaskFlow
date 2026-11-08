import { useEffect, useState } from 'react'
import { asApiError } from '../api/client'
import type { ApiClient, ApiError } from '../api/client'
import { useAuth } from '../features/auth/auth-context'

type Resource<T> = { key: string; status: 'loading' } | { key: string; status: 'ready'; data: T }
  | { key: string; status: 'error'; error: ApiError }

export function useApiResource<T>(client: ApiClient, path: string) {
  const { session } = useAuth()
  const identity = session.user?.id ?? ''
  const key = identity + ':' + client.resourceKey + ':' + path
  const [attempt, setAttempt] = useState(0)
  const [resource, setResource] = useState<Resource<T>>({ key, status: 'loading' })
  useEffect(() => {
    const controller = new AbortController()
    let alive = true
    queueMicrotask(async () => {
      if (!alive) return
      setResource({ key, status: 'loading' })
      try {
        const data = await client.request<T>({ url: path, signal: controller.signal })
        if (alive) setResource({ key, status: 'ready', data })
      } catch (error) {
        if (alive) setResource({ key, status: 'error', error: asApiError(error) })
      }
    })
    return () => { alive = false; controller.abort() }
  }, [client, path, key, attempt])
  const result: Resource<T> = resource.key === key ? resource : { key, status: 'loading' }
  return { result, retry: () => setAttempt(value => value + 1) }
}
