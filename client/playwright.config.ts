import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 90000,
  expect: { timeout: 10000 },
  workers: 1,
  retries: 0,
  use: {
    actionTimeout: 15000,
    baseURL: process.env.ARGUS_URL ?? 'http://127.0.0.1:5077',
    viewport: { width: 1440, height: 960 },
    // Exercise the native 2D fallback in CI, where hardware WebGL is unavailable.
    launchOptions: { args: ['--disable-webgl'] },
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
});
