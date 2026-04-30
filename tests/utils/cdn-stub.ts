import { Page } from '@playwright/test';

export async function stubCdnScripts(page: Page) {
  const emptyJs = '/* stubbed by Playwright */';
  await page.route('https://cdn.tailwindcss.com', r => r.fulfill({ status: 200, contentType: 'application/javascript', body: emptyJs }));
  await page.route('https://cdnjs.cloudflare.com/ajax/libs/animejs/3.2.2/anime.min.js', r => r.fulfill({ status: 200, contentType: 'application/javascript', body: emptyJs }));
  await page.route('https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.5/signalr.min.js', r => r.fulfill({ status: 200, contentType: 'application/javascript', body: emptyJs }));
  await page.route('https://fonts.googleapis.com/**', r => r.fulfill({ status: 204 }));
  await page.route('https://fonts.gstatic.com/**', r => r.fulfill({ status: 204 }));
}
