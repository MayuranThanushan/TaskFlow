import { createContext, useContext } from 'react'
export const CompanyContext = createContext({ lifecycleEnabled: false, role: '' })
export const useCompany = () => useContext(CompanyContext)
