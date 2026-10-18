import { useCallback, useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { api, asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { Credentials, Registration } from '../../api/types'
import { AuthContext } from './auth-context'
import type { Session } from './auth-context'

export function AuthProvider({ children, client = api }: { children: ReactNode; client?: ApiClient }) {
  const [session, setSession] = useState<Session>({ status: 'checking', user: null })
  const current = useRef(session)
  const operation = useRef(0)
  const alive = useRef(false)
  const refreshing = useRef<number | null>(null)
  const refreshId = useRef(0)
  const commit = useCallback((value: Session) => { current.current = value; setSession(value) }, [])

  const expire = useCallback(() => {
    operation.current++
    client.forgetSession()
    if (alive.current) commit({ status: 'anonymous', user: null, message: 'Your session has expired. Sign in again.' })
  }, [client, commit])

  const refresh = useCallback(async () => {
    if (refreshing.current !== null) return
    const requestId = ++refreshId.current
    refreshing.current = requestId
    const version = operation.current
    if (current.current.status === 'error') commit({ status: 'checking', user: null })
    try {
      const user = await client.currentUser()
      if (alive.current && version === operation.current) {
        if (current.current.status === 'authenticated' && current.current.user.id !== user.id) client.forgetSession()
        commit({ status: 'authenticated', user })
      }
    } catch (error) {
      if (!alive.current || version !== operation.current) return
      if (asApiError(error).status === 401) {
        if (current.current.status === 'authenticated') expire()
        else { client.forgetSession(); commit({ status: 'anonymous', user: null }) }
      } else if (current.current.status === 'authenticated') {
        commit({ ...current.current, message: 'We could not check your session. Please try again.' })
      } else commit({ status: 'error', user: null, message: 'We could not check your session. Please try again.' })
    } finally { if (refreshing.current === requestId) refreshing.current = null }
  }, [client, commit, expire])

  useEffect(() => {
    alive.current = true
    const unsubscribe = client.onUnauthorized(expire)
    queueMicrotask(() => { if (alive.current) void refresh() })
    const recheck = () => {
      if (document.visibilityState === 'visible' && current.current.status === 'authenticated') void refresh()
    }
    window.addEventListener('focus', recheck)
    document.addEventListener('visibilitychange', recheck)
    const interval = window.setInterval(recheck, 60_000)
    return () => {
      alive.current = false
      // Invalidate responses belonging to this effect lifetime.
      operation.current += 1
      refreshing.current = null
      unsubscribe()
      window.removeEventListener('focus', recheck)
      document.removeEventListener('visibilitychange', recheck)
      window.clearInterval(interval)
    }
  }, [client, expire, refresh])

  const signIn = useCallback(async (values: Credentials) => {
    const version = ++operation.current
    const user = await client.login(values)
    if (alive.current && version === operation.current) {
      operation.current++
      commit({ status: 'authenticated', user })
    }
  }, [client, commit])

  const signOut = useCallback(async () => {
    const version = ++operation.current
    await client.logout()
    if (alive.current && version === operation.current) {
      operation.current++
      commit({ status: 'anonymous', user: null })
    }
  }, [client, commit])

  const register = useCallback(async (values: Registration) => { await client.register(values) }, [client])
  return <AuthContext.Provider value={{ session, refresh, signIn, signOut, register }}>{children}</AuthContext.Provider>
}
