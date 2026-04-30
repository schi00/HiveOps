import { Page } from '@playwright/test';

export async function ensureLoggedIn(page: Page) {
  // If token exists, skip UI login
  const hasToken = await page.evaluate(() => !!localStorage.getItem('token'));
  if (hasToken) return;
  // Otherwise, navigate to login and let test handle UI login
  await page.goto('/dashboard');
}

export async function injectDummyAuth(page: Page, role: 'Admin'|'SuperAdmin'|'Tenant' = 'Admin') {
  await page.addInitScript(([r]) => {
    window.localStorage.setItem('token', 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.dummy.payload');
    window.localStorage.setItem('user_role', r);
    window.localStorage.setItem('user', JSON.stringify({ username: 'e2e', role: r }));
  }, [role]);
}
