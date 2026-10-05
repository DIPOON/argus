import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5077',
      '/health': 'http://127.0.0.1:5077',
      '/ws': { target: 'ws://127.0.0.1:5077', ws: true },
    },
  },
  build: { outDir: '../server/Argus.Server/wwwroot', emptyOutDir: true },
});
