import type { To } from 'react-router-dom'
export function companyPath(to: To, id?: string): To {
  const scope = (path?: string) => id && path && /^\/(projects|tasks|dashboard|activity)(?:[/?#]|$)/.test(path) ? '/companies/' + id + path : path
  return typeof to === 'string' ? scope(to)! : { ...to, pathname: scope(to.pathname) }
}
