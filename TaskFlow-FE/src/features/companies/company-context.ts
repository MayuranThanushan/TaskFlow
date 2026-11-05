import { createContext, useContext } from 'react'
export const CompanyContext = createContext<{ lifecycleEnabled: boolean; role: string; invitationsEnabled?: boolean }>({ lifecycleEnabled: false, role: '' })
export const useCompany = () => useContext(CompanyContext)
