import type { components } from './generated'

export type Profile = components['schemas']['ProfileDto']
export type Credentials = components['schemas']['LoginRequest']
export type Registration = components['schemas']['RegisterRequest']
export type CsrfToken = components['schemas']['CsrfDto']
export type ProfileUpdate = components['schemas']['ProfileUpdateRequest']
export type Problem = components['schemas']['ApiProblem']
export type FieldErrors = NonNullable<Problem['errors']>

export type AccountCapabilities = components['schemas']['AccountCapabilitiesDto']
export type AccountSecurity = components['schemas']['AccountSecurityDto']
export type AccountToken = components['schemas']['AccountTokenRequest']
export type AccountReset = components['schemas']['AccountResetRequest']
