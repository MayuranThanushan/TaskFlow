export interface Profile {
  id: string
  displayName: string
  email: string
}

export interface Credentials { email: string; password: string }
export interface Registration extends Credentials { displayName: string }
export type FieldErrors = Record<string, string[]>
