import axios, { AxiosHeaders } from 'axios'
import type { AxiosAdapter, AxiosInstance, AxiosRequestConfig, RawAxiosHeaders } from 'axios'
import type { Credentials, FieldErrors, Profile, Registration } from './types'

export class ApiError extends Error {
  readonly status?: number
  readonly fields: FieldErrors

  constructor(message: string, status?: number, fields: FieldErrors = {}) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.fields = fields
  }
}

export function asApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error
  if (!axios.isAxiosError(error)) return new ApiError('Something went wrong. Please try again.')
  const status = error.response?.status
  const body: unknown = error.response?.data
  const fields: FieldErrors = {}
  if (body && typeof body === 'object' && 'errors' in body && body.errors && typeof body.errors === 'object') {
    for (const [key, value] of Object.entries(body.errors)) {
      if (Array.isArray(value) && value.every(item => typeof item === 'string')) fields[key] = value
    }
  }
  const message = status === 401 ? 'Email or password is incorrect, or the account is temporarily locked.'
    : status === 409 ? 'An account with this email already exists. Sign in to continue.'
      : status === 429 ? 'Too many attempts. Please wait a minute before trying again.'
        : status === 403 ? 'You do not have permission to do that.'
          : status === 400 ? 'Please check the details below and try again.'
            : status ? 'TaskFlow could not complete the request. Please try again.'
              : 'We could not reach TaskFlow. Check your connection and try again.'
  return new ApiError(message, status, fields)
}

export class ApiClient {
  private readonly transport: AxiosInstance
  private token?: string
  private pendingToken?: Promise<string>
  private tokenVersion = 0
  private identityVersion = 0
  private readonly unauthorized = new Set<() => void>()

  constructor(transport: AxiosInstance) { this.transport = transport }

  onUnauthorized(listener: () => void) {
    this.unauthorized.add(listener)
    return () => { this.unauthorized.delete(listener) }
  }

  forgetSession() {
    this.identityVersion++
    this.tokenVersion++
    this.token = undefined
    this.pendingToken = undefined
  }

  private csrf(): Promise<string> {
    if (this.token) return Promise.resolve(this.token)
    if (this.pendingToken) return this.pendingToken
    const version = this.tokenVersion
    const pending = this.transport.get<{ requestToken: string }>('/auth/csrf').then(response => {
      if (version === this.tokenVersion) this.token = response.data.requestToken
      return response.data.requestToken
    }).catch(error => { throw asApiError(error) }).finally(() => {
      if (this.pendingToken === pending) this.pendingToken = undefined
    })
    this.pendingToken = pending
    return pending
  }

  async request<T>(config: AxiosRequestConfig, notifyUnauthorized = true): Promise<T> {
    // Relative API paths only: never attach cookies/CSRF tokens to a caller-supplied origin.
    if (!config.url?.startsWith('/') || config.url.startsWith('//') || config.url.includes('\\'))
      throw new ApiError('Invalid API request.')
    const identity = this.identityVersion
    const unsafe = !['get', 'head', 'options'].includes((config.method ?? 'get').toLowerCase())
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const headers = AxiosHeaders.from(config.headers as RawAxiosHeaders | undefined)
        if (unsafe) headers.set('X-CSRF-TOKEN', await this.csrf())
        return (await this.transport.request<T>({ ...config, baseURL: "/api/v1", withCredentials: true, headers })).data
      } catch (error) {
        const failure = asApiError(error)
        // Retry only an explicit CSRF rejection, never a network/server failure or an arbitrary write.
        if (attempt === 0 && unsafe && failure.status === 400 && failure.fields.csrf) {
          this.tokenVersion++
          this.token = undefined
          this.pendingToken = undefined
          continue
        }
        if (failure.status === 401 && notifyUnauthorized && identity === this.identityVersion)
          this.unauthorized.forEach(listener => listener())
        throw failure
      }
    }
    throw new ApiError('Please try the request again.')
  }

  updateProfile(displayName: string) { return this.request<Profile>({ url: '/users/me', method: 'patch', data: { displayName } }) }

  currentUser() { return this.request<Profile>({ url: '/auth/me' }, false) }
  register(values: Registration) { return this.request<Profile>({ url: '/auth/register', method: 'post', data: values }, false) }

  async login(values: Credentials) {
    const user = await this.request<Profile>({ url: '/auth/login', method: 'post', data: values }, false)
    this.forgetSession()
    void this.csrf().catch(() => { /* The next write will retry token acquisition. */ })
    return user
  }

  async logout() {
    try { await this.request<void>({ url: '/auth/logout', method: 'post' }, false) }
    catch (error) { if (asApiError(error).status !== 401) throw error }
    this.forgetSession()
    void this.csrf().catch(() => { /* The next write will retry token acquisition. */ })
  }
}

export function createApiClient(adapter?: AxiosAdapter) {
  return new ApiClient(axios.create({
    baseURL: '/api/v1', withCredentials: true, timeout: 15_000,
    headers: { Accept: 'application/json' }, ...(adapter ? { adapter } : {}),
  }))
}

export const api = createApiClient()
