const { defineConfig, devices } = require('@playwright/test');
module.exports = defineConfig({
  testDir: './tests/browser', timeout: 30000, fullyParallel: false, workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:5197', trace: 'retain-on-failure', screenshot: 'only-on-failure',
    launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] } },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } }
  ],
  webServer: {
    command: 'dotnet run --no-build --no-launch-profile --project src/Jarvis.Api --urls http://127.0.0.1:5197',
    url: 'http://127.0.0.1:5197/health', reuseExistingServer: false, timeout: 60000,
    env: { ASPNETCORE_ENVIRONMENT: 'Testing', Jarvis__ApiKey: 'browser-test-only-key', Jarvis__StorageProvider: 'InMemory', OpenAI__ApiKey: '', OpenAI__Model: '' }
  }
});
