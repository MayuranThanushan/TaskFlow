import { createContext, useCallback, useContext, useSyncExternalStore } from 'react'
export type InvitationLink = Readonly<{ workspaceId: string; invitationId: string; token: string; generation: number }>
const id = (s: string | null): s is string => !!s && /^[1-9][0-9]{0,18}$/.test(s) && BigInt(s) <= 9223372036854775807n
export function parseInvitation(hash: string): InvitationLink | null {
  const values = new URLSearchParams(hash.slice(1)), keys = ['workspaceId', 'invitationId', 'token']
  if ([...values.keys()].some(k => !keys.includes(k)) || keys.some(k => values.getAll(k).length !== 1)) return null
  const workspaceId = values.get('workspaceId'), invitationId = values.get('invitationId'), token = values.get('token')
  return id(workspaceId) && id(invitationId) && token && /^[A-Za-z0-9_-]{43}$/.test(token) ? Object.freeze({ workspaceId, invitationId, token, generation: 0 }) : null
}
export function invitationVault(initial: InvitationLink | null) {
  let value = initial, generation = 0
  const listeners = new Set<() => void>()
  return { snapshot: () => value, subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener) } }, replace(next: InvitationLink | null) { value = next ? Object.freeze({ ...next, generation: ++generation }) : null; listeners.forEach(l => l()) } }
}
const empty = invitationVault(null)
export const InvitationContext = createContext<ReturnType<typeof invitationVault> | null>(null)
export function useInvitation() {
  const store = useContext(InvitationContext) ?? empty
  const clear = useCallback(() => store.replace(null), [store])
  return { pending: useSyncExternalStore(store.subscribe, store.snapshot), clear }
}
