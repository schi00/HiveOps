import { test, expect } from '@playwright/test';
import { DashboardPage } from '../../pages/dashboard.page';
import { IncidentsListPage } from '../../pages/incidents-list.page';
import { mockIncidentsSuccess, mockIncidentsError500 } from '../../fixtures/routes/api-routes';
import { injectDummyAuth } from '../../utils/auth';
import { stubCdnScripts } from '../../utils/cdn-stub';

test.describe('Incidentes: listado y filtros', () => {
  test.beforeEach(async ({ page }) => {
    // Mock de datos (excepto auth)
    await mockIncidentsSuccess(page);
  });

  test('muestra lista con badges de estado y filtros', async ({ page }) => {
    const dp = new DashboardPage(page);
    const lp = new IncidentsListPage(page);
    await stubCdnScripts(page);
    await injectDummyAuth(page);
    await page.goto('/dashboard/index.html', { waitUntil: 'domcontentloaded' });
    await dp.openTab('Conversaciones');
    await lp.waitLoaded();
    await lp.assertBadgeForStatusPresent('DEPLOYING');
    await lp.assertBadgeForStatusPresent('SUCCESS');
    await lp.assertBadgeForStatusPresent('FAILED');
    await lp.filterByStatus('Open');
    await lp.filterBySeverity('High');
  });

  test('muestra error 500 al fallar backend', async ({ page }) => {
    await page.unroute('**/api/support/incidents**');
    await mockIncidentsError500(page);
    await stubCdnScripts(page);
    await injectDummyAuth(page);
    await page.goto('/dashboard/index.html', { waitUntil: 'domcontentloaded' });
    await expect(page.getByText(/error|server/i)).toBeVisible({ timeout: 5000 });
  });
});
