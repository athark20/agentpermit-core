import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The app is fully config-driven via VITE_* env vars (see .env.example); nothing is hard-coded,
// so the same build runs against dev and prod by swapping environment values.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173 },
  build: { outDir: 'dist' },
})
