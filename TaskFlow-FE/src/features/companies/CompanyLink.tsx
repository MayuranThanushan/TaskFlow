import { Link as RouterLink, useParams } from 'react-router-dom'
import type { ComponentProps } from 'react'
import { companyPath } from './company-path'
export function Link({ to, ...props }: ComponentProps<typeof RouterLink>) {
  const { workspaceId } = useParams()
  return <RouterLink {...props} to={companyPath(to, workspaceId)} />
}
