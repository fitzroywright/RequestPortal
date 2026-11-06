// Real Razor output and assets; all accounts and requests come from isolated test fixtures.
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const assets = path.resolve(__dirname, '../../RequestPortal.Web/wwwroot');
const inputs = {
  '/Management/QuickIntakeDashboard': process.env.REQUESTPORTAL_QI_RENDER_OUTPUT,
  '/Management/RegisteredUsers': process.env.REQUESTPORTAL_RU_RENDER_OUTPUT
};
for (const root of Object.values(inputs)) assert(root, 'Set both dashboard render output directories.');
(async () => {
  const browser = await chromium.launch({headless: true,
    ...(process.env.CHROME_EXECUTABLE_PATH ? {executablePath: process.env.CHROME_EXECUTABLE_PATH} : {})});
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    let errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await context.route('**/*', route => {
      const url = new URL(route.request().url());
      if (route.request().resourceType() === 'document') {
        const root = inputs[url.pathname];
        return route.fulfill({contentType: 'text/html', body: root ? fs.readFileSync(path.join(root, 'index.html')) : '<h1>Navigation destination</h1>'});
      }
      if (url.pathname === '/api/messages/unread-count')
        return route.fulfill({contentType: 'application/json', body: '{"count":0,"unreadCount":0}'});
      if (url.hostname === 'cdnjs.cloudflare.com' || url.pathname.endsWith('/messages.js'))
        return route.fulfill({contentType: 'text/javascript', body: ''});
      const file = path.join(assets, url.pathname);
      if (fs.existsSync(file) && fs.statSync(file).isFile())
        return route.fulfill({body: fs.readFileSync(file), contentType: url.pathname.endsWith('.js') ? 'text/javascript' : url.pathname.endsWith('.css') ? 'text/css' : url.pathname.endsWith('.geojson') ? 'application/geo+json' : 'application/octet-stream'});
      return route.fulfill({status: 204, body: ''});
    });
    let cases = 0;
    for (const route of Object.keys(inputs)) for (const width of [1440, 820, 390]) {
      errors = [];
      await page.setViewportSize({width, height: 1000});
      await page.goto('https://fixture.test' + route);
      const quick = route.endsWith('QuickIntakeDashboard');
      const map = quick ? '#quick-intake-parish-map' : '#registered-users-parish-map';
      await page.waitForFunction(selector => document.querySelectorAll(selector + ' path.leaflet-interactive').length === 14, map);
      const chartIds = quick ? ['qi-types', 'qi-employment', 'qi-income', 'qi-gender', 'qi-ages'] : ['ru-types', 'ru-status', 'ru-trend'];
      for (const id of chartIds) {
        if (!await page.locator('#' + id).count()) continue;
        await page.waitForFunction(id => !!window.Chart?.getChart(id), id);
      }
      if (quick) {
        const fills = await page.locator(map + ' path.leaflet-interactive').evaluateAll(nodes => nodes.map(node => node.getAttribute('fill')));
        assert(fills.includes('#95c5f2'), JSON.stringify(fills));
        assert(!fills.includes('#173f8a'));
      } else {
        assert.deepEqual(await page.evaluate(() => Chart.getChart('ru-types').data.datasets[0].data), [2, 1]);
      }
      // The existing site stylesheet hides the main navigation below 901px.
      // Dashboard assets still run at every viewport; exercise the visible menus on desktop.
      if (width <= 900) { assert.deepEqual(errors, []); cases++; continue; }
      const reports = page.locator('.reports-menu');
      await reports.locator('summary').click();
      assert.deepEqual(await reports.locator('a').evaluateAll(links => links.map(link => {
        const copy = link.cloneNode(true); copy.querySelectorAll('[aria-hidden="true"]').forEach(node => node.remove()); return copy.textContent.trim();
      })), ['Quick Intakes', 'Registered Users', 'Management', 'Requests', 'Invite Reviewers']);
      const divider = reports.locator('[role="separator"]');
      assert.equal(await divider.count(), 1);
      assert.equal(await divider.evaluate(node => node.previousElementSibling.getAttribute('href')), '/Staff/Reports');
      assert.equal(await divider.evaluate(node => node.nextElementSibling.getAttribute('href')), '/Staff/Reviewers');
      assert.equal(await divider.evaluate(node => getComputedStyle(node).height), '1px');
      await page.keyboard.press('Escape'); assert.equal(await reports.getAttribute('open'), null);
      await reports.locator('summary').click(); await page.locator('h1').click();
      assert.equal(await reports.getAttribute('open'), null);
      const admin = page.locator('.staff-admin-menu').filter({has: page.locator('summary[aria-label="Administration menu"]')});
      await admin.locator('summary').click(); await page.keyboard.press('Escape');
      assert.equal(await admin.getAttribute('open'), null);
      assert.deepEqual(errors, []);
      cases++;
    }
    console.log(`${cases} Reports/dashboard browser cases passed: maps, charts, five ordered links, separator, outside click and Escape.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
