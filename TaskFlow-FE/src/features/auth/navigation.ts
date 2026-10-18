export function returnPath(value: unknown) {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) return '/dashboard'
  const url = new URL(value, window.location.origin)
  if (url.origin !== window.location.origin || ['/login', '/register'].includes(url.pathname)) return '/dashboard'
  return url.pathname + url.search + url.hash
}
