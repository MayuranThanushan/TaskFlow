import { useEffect, useLayoutEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { invitationVault, parseInvitation, InvitationContext } from './invitation-context'
export function InvitationProvider({ children }: { children: ReactNode }) {
  const location = useLocation(), navigate = useNavigate()
  const [store] = useState(() => invitationVault(location.pathname === '/invitations/accept' ? parseInvitation(location.hash) : null))
  useLayoutEffect(() => {
    if (location.pathname !== '/invitations/accept' || !location.hash && !location.search) return
    store.replace(location.hash ? parseInvitation(location.hash) : null)
  }, [location.pathname, location.hash, location.search, store])
  useEffect(() => {
    if (location.pathname === '/invitations/accept' && (location.hash || location.search)) void navigate('/invitations/accept', { replace: true })
  }, [location.pathname, location.hash, location.search, navigate])
  return <InvitationContext value={store}>{children}</InvitationContext>
}
