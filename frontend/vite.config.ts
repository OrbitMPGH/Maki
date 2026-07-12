import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Honor an externally assigned port (e.g. the preview harness); default 5173.
    port: Number(process.env.PORT) || 5173,
    proxy: {
      '/api': 'http://localhost:8990',
      '/initialize.json': 'http://localhost:8990',
      '/signalr': {
        target: 'http://localhost:8990',
        ws: true,
      },
    },
  },
})
