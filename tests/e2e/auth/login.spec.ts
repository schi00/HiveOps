import { test, expect } from '@playwright/test';
import { LoginPage } from '../../pages/login.page';
import { stubCdnScripts } from '../../utils/cdn-stub';

test.describe('Auth: login y persistencia', () => {
  test('login con credenciales reales y persistencia de token', async ({ page }) => {
    const lp = new LoginPage(page);
    await stubCdnScripts(page);
    await page.goto('/dashboard/index.html', { waitUntil: 'domcontentloaded' });
    await lp.loginWithEnv();
    await lp.assertTokenPersisted();
    // Refresh y verificar sesión viva
    await page.reload();
    const token = await page.evaluate(() => localStorage.getItem('token'));
    expect(token).toBeTruthy();
  });
});
