import { test } from '@playwright/test';
import { DashboardPage } from '../../pages/dashboard.page';
import { IncidentDetailPage } from '../../pages/incident-detail.page';
import { mockIncidentsSuccess, mockIncidentDetail, mockRollbackForbidden } from '../../fixtures/routes/api-routes';
import { injectDummyAuth } from '../../utils/auth';

test.describe('Incidente: detalle, logs y rollback', () => {
  test.beforeEach(async ({ page }) => {
    await mockIncidentsSuccess(page);
  });

  test('admin ve logs y starter no puede rollback (403)', async ({ page }) => {
    const dp = new DashboardPage(page);
    const idAdmin = '22222222-2222-2222-2222-222222222222';
    await mockIncidentDetail(page, idAdmin, 'incident.detail.admin.json');

    await injectDummyAuth(page);
    await page.goto('/dashboard/index.html', { waitUntil: 'domcontentloaded' });
    const detail = new IncidentDetailPage(page);
    await detail.openIncident(idAdmin);
    await detail.assertAdminLogsVisible(true);

    // Starter sin rollback
    const idStarter = '11111111-1111-1111-1111-111111111111';
    await mockIncidentDetail(page, idStarter, 'incident.detail.starter.json');
    await mockRollbackForbidden(page, idStarter);
    await detail.openIncident(idStarter);
    await detail.rollbackButtonState('disabled');
  });
});
