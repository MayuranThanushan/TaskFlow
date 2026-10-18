import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import App from './App'

describe('TaskFlow shell', () => {
  it('provides a main landmark and tells visitors the product is not available yet', () => {
    render(<App />)
    const main = screen.getByRole('main')
    expect(within(main).getByRole('heading', { level: 1 })).toBeVisible()
    expect(within(main).getByText(/TaskFlow is being built/i)).toBeVisible()
  })

  it('provides a keyboard-accessible home link', async () => {
    const user = userEvent.setup()
    render(<App />)
    const home = screen.getByRole('link', { name: 'TaskFlow home' })
    await user.tab()
    expect(home).toHaveFocus()
    expect(home).toHaveAttribute('href', '/')
  })
})
