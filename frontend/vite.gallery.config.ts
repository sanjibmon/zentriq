import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// CI only: the screenshot gallery (src/dev/gallery.tsx), built to dist-gallery/; never part of the product.
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist-gallery', rollupOptions: { input: 'gallery.html' }, chunkSizeWarningLimit: 2000 },
});
