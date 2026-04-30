import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['html'], ['list']] : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:5198',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    ignoreHTTPSErrors: true,
    viewport: { width: 1366, height: 800 },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'webkit',
      use: { ...devices['Desktop Safari'] },
    }
  ],
});
