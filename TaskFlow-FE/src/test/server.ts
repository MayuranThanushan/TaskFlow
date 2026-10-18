import { AxiosError, AxiosHeaders } from 'axios'
import type { AxiosAdapter, InternalAxiosRequestConfig } from 'axios'
export const profile = { id: 'user-1', displayName: 'Alex', email: 'alex@example.com' }
export function reply(config: InternalAxiosRequestConfig, status: number, data: unknown = {}) {
  const response = { config, status, data, statusText: String(status), headers: new AxiosHeaders() }
  if (status >= 400) throw new AxiosError('Request failed', undefined, config, undefined, response)
  return response
}
export function server(initial = false) {
  const state = { authenticated: initial, meStatus: 0, loginStatus: 200, logoutStatus: 204, csrfFailures: 0,
    loginWait: null as Promise<void> | null, meWait: null as Promise<void> | null, calls: [] as InternalAxiosRequestConfig[], tokens: 0 }
  const adapter: AxiosAdapter = async config => {
    state.calls.push(config)
    if (config.url === '/auth/csrf') return reply(config, 200, { requestToken: `token-${++state.tokens}` })
    if (config.url === '/auth/me') { await state.meWait; return reply(config, state.meStatus || (state.authenticated ? 200 : 401), profile) }
    if (config.url === '/dashboard') return reply(config, state.authenticated ? 200 : 401, { totalProjects: 0, totalTasks: 0, tasksByStatus: { toDo: 0, inProgress: 0, done: 0 }, assignedToMe: 0, overdueTasks: 0 })
    if (state.csrfFailures-- > 0) return reply(config, 400, { errors: { csrf: ['Invalid token'] } })
    if (config.url === '/auth/login') {
      await state.loginWait
      if (state.loginStatus === 200) state.authenticated = true
      return reply(config, state.loginStatus, profile)
    }
    if (config.url === '/auth/register') return reply(config, 201, profile)
    if (config.url === '/auth/logout') {
      if (state.logoutStatus === 204) state.authenticated = false
      return reply(config, state.logoutStatus)
    }
    return reply(config, 401)
  }
  return { state, adapter }
}
