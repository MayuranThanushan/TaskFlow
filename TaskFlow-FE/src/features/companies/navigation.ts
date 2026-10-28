import { useNavigate as useRouterNavigate, useParams } from 'react-router-dom'
import type { NavigateOptions, To } from 'react-router-dom'
import { companyPath } from './company-path'
export { Link } from './CompanyLink'
export { useLocation, useParams, useSearchParams } from 'react-router-dom'
export function useNavigate() {
  const navigate = useRouterNavigate()
  const { workspaceId } = useParams()
  return (to: To, options?: NavigateOptions) => navigate(companyPath(to, workspaceId), options)
}
