import { createContext, useContext } from 'react'
import type { Credentials, Profile, Registration } from '../../api/types'

export type Session =
  | { status: 'checking' | 'anonymous' | 'error'; user: null; message?: string }
  | { status: 'authenticated'; user: Profile; message?: string }

export interface AuthState {
  session: Session
  updateProfile: (displayName: string) => Promise<boolean>
  refresh: () => Promise<void>
  signIn: (values: Credentials) => Promise<void>
  register: (values: Registration) => Promise<void>
  signOut: () => Promise<void>
}

export const AuthContext = createContext<AuthState | null>(null)
export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('Authentication provider is missing.')
  return context
}
