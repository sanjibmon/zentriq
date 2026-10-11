import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Production: built into dist/ and served by the Zentriq API (same origin, strict CSP: no inline scripts,
// no external fonts/CDNs). Development: `npm run dev` proxies /api to a local API.
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', assetsDir: 'assets', sourcemap: false, chunkSizeWarningLimit: 800 },
  server: {
    proxy: {
      '/api': { target: process.env.ZENTRIQ_API ?? 'https://localhost', changeOrigin: true, secure: false },
      '/health': { target: process.env.ZENTRIQ_API ?? 'https://localhost', changeOrigin: true, secure: false },
    },
  },
});
