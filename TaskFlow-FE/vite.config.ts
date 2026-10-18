import react from '@vitejs/plugin-react'
import { readFileSync } from 'node:fs'
import { Agent } from 'node:https'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'TASKFLOW_')
  const target = env.TASKFLOW_API_TARGET || 'http://localhost:5140'
  return {
    plugins: [react()],
    server: {
      https: env.TASKFLOW_HTTPS_PFX
        ? { pfx: readFileSync(env.TASKFLOW_HTTPS_PFX), passphrase: env.TASKFLOW_HTTPS_PASSWORD }
        : undefined,
      port: 5173,
      strictPort: true,
      proxy: {
        '/api': {
          target,
          agent: target.startsWith('https:') && env.TASKFLOW_API_CA
            ? new Agent({ ca: readFileSync(env.TASKFLOW_API_CA) })
            : undefined,
          changeOrigin: true,
        },
      },
    },
  }
})
