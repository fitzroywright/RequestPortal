// Compiled Razor fixture pages and real application assets. No production access.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = process.env.REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT;
if (!root) throw new Error('Set REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT to the Razor fixture output.');
const assets = path.resolve(__dirname, '../../RequestPortal.Web/wwwroot');
const requestId = '33333333-3333-4333-8333-333333333333';
(async () => {
    const browser = await chromium.launch({ headless: true, ...(process.env.CHROME_EXECUTABLE_PATH ? { executablePath: process.env.CHROME_EXECUTABLE_PATH } : {}) });
    const context = await browser.newContext({ locale: 'en-US', permissions: ['clipboard-read', 'clipboard-write'] });
    try {
        const page = await context.newPage(), errors = [], measurements = [];
        page.on('pageerror', error => errors.push(error.message));
        let failDiagnostics = false;
        await page.route('**/*', route => {
            const req = route.request(), url = new URL(req.url());
            if (url.searchParams.get('handler') === 'Diagnostics') return route.fulfill(failDiagnostics
                ? { status: 503, body: 'Unavailable' }
                : { contentType: 'text/html', body: fs.readFileSync(path.join(root, 'integration-detail.html')) });
            if (req.resourceType() === 'document') {
                const name = url.searchParams.has('many') ? 'integration-many' : url.searchParams.has('collapsed') ? 'integration-collapsed' : url.searchParams.has('reviewer') ? 'integrations-reviewer' : 'integration-visual';
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(root, name + '.html')) });
            }
            if (url.hostname === 'cdnjs.cloudflare.com' || url.pathname.endsWith('/messages.js')) return route.fulfill({ contentType: 'text/javascript', body: '' });
            if (req.resourceType() === 'image') return route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR5kAAAAASUVORK5CYII=','base64') });
            const file = path.join(assets, url.pathname);
            if (fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ contentType: url.pathname.endsWith('.css') ? 'text/css' : 'text/javascript', body: fs.readFileSync(file) });
            return route.fulfill({ status: 204, body: '' });
        });
        const noOverflow = async (label) => {
            const result = await page.evaluate(() => ({ viewport: [innerWidth, innerHeight], document: [document.documentElement.scrollWidth, document.documentElement.scrollHeight], rows: document.querySelector('.integration-rows').clientHeight, layout: Object.fromEntries(['.site-header', '.integration-heading', '.integration-tile', '.integration-filters', '.integration-grid', '.integration-diagnostics', '.integration-data-row'].map(selector => { const r = document.querySelector(selector).getBoundingClientRect(); return [selector, { x: r.x, y: r.y, width: r.width, height: r.height }]; })) }));
            assert(result.document[0] <= result.viewport[0], `Document horizontal overflow: ${label} ${JSON.stringify(result)}`);
            assert(result.document[1] <= result.viewport[1], `Document vertical overflow: ${label} ${JSON.stringify(result)}`);
            assert(result.rows >= 80, `Grid row region unusable: ${label}`);
            for (const selector of ['.site-header', '.integration-heading', '.integration-monitor > .integration-summary', '.integration-filters', '.integration-grid-header']) {
                const box = await page.locator(selector).boundingBox();
                assert(box && box.y >= 0 && box.x >= 0 && box.y + box.height <= result.viewport[1] + 1 && box.x + box.width <= result.viewport[0] + 1, `Clipped controls: ${label} ${selector}`);
            }
            assert.equal(await page.locator('.integration-diagnostics').evaluateAll(panels => panels.some(panel => [...panel.querySelectorAll('*')].some(el => /auto|scroll/.test(getComputedStyle(el).overflowY) && el.scrollHeight > el.clientHeight))), false, 'Nested diagnostics scrolling');
            return result;
        };
        for (const [width, height] of [[1672,941], [1648,928], [1440,900], [1920,1080], [1024,768], [768,900], [390,844]]) {
            await page.setViewportSize({ width, height });
            await page.goto('https://requestportal.test/Staff/Integrations');
            await page.waitForFunction(() => !!document.querySelector('.integration-monitor'));
            assert.equal(await page.locator('.integration-summary > .integration-tile').count(), 5);
            assert.equal(await page.getByRole('link', { name: /export.*csv|export complete attempt history/i }).count(), 0);
            await page.evaluate(() => new Promise(requestAnimationFrame));
            assert.equal(await page.locator('.integration-rows').evaluate(el => el.scrollTop), 0, 'An already visible selection must not hide preceding rows');
            const initial = await noOverflow(`${width}x${height} expanded`);
            const overlaps = await page.locator('.integration-row-actions').evaluateAll(cells => cells.flatMap(cell => {
                const buttons = [...cell.querySelectorAll('form>button,a.button,details>summary')].map(el => ({ text: el.textContent, rect: el.getBoundingClientRect(), font: getComputedStyle(el).fontSize, padding: getComputedStyle(el).paddingInline }));
                return buttons.flatMap((a,index) => buttons.slice(index+1).filter(b => Math.min(a.rect.bottom,b.rect.bottom) > Math.max(a.rect.top,b.rect.top)+1 && Math.min(a.rect.right,b.rect.right) > Math.max(a.rect.left,b.rect.left)+1).map(b => ({ a: a.text, b: b.text, ax: a.rect.x, aw: a.rect.width, font: a.font, padding: a.padding, bx: b.rect.x, bw: b.rect.width })));
            }));
            assert.deepEqual(overlaps, [], `Row action controls must not overlap at ${width}x${height}`);
            await page.screenshot({ path: path.join(root, `integrations-mockup-${width}x${height}.png`), fullPage: true });
            const record = page.locator(`[data-record="${requestId}"]`), expand = record.locator('.integration-expand');
            await record.getByRole('button', { name: 'View Full Diagnostics', exact: true }).click();
            assert(await record.getByRole('heading', { name: 'Attempt history', exact: true }).isVisible());
            await record.getByRole('button', { name: 'Copy technical reference', exact: true }).click();
            assert.equal(await page.evaluate(() => navigator.clipboard.readText()), '22222222-2222-4222-8222-222222222222');
            await noOverflow(`${width} full diagnostics`);
            await record.getByRole('button', { name: 'Hide Full Diagnostics', exact: true }).click();
            await expand.click();
            assert.equal(await expand.getAttribute('aria-expanded'), 'false');
            await record.getByRole('link', { name: 'View', exact: true }).click();
            assert.equal(await expand.getAttribute('aria-expanded'), 'true');
            await noOverflow(`${width} reopened`);

            measurements.push({ width, height, ...initial });
            await page.goto('https://requestportal.test/Staff/Integrations?many=true');
            assert.equal(await page.locator('.integration-record').count(), 100);
            await noOverflow(`${width} 100 records`);
            const positions = await page.locator('.integration-rows').evaluate(el => { const before = el.scrollTop; el.scrollTop = el.scrollHeight; return [before, el.scrollTop, el.scrollHeight, el.clientHeight]; });
            assert(positions[1] > positions[0] && positions[2] > positions[3], 'Only grid rows must scroll');
            await noOverflow(`${width} after scrolling`);
            const actions = page.locator('.integration-more-menu').last();
            await actions.locator('summary').click();
            await actions.getByRole('button', { name: 'System diagnostics', exact: true }).click();
            assert(await page.getByRole('heading', { name: 'Return-event outbox', exact: true }).isVisible());
            await noOverflow(`${width} system diagnostics`);
            await page.screenshot({ path: path.join(root, `integrations-system-${width}x${height}.png`), fullPage: true });
        }
        await page.setViewportSize({ width: 1648, height: 928 });
        await page.goto('https://requestportal.test/Staff/Integrations?collapsed=true');
        // A collapsed, unloaded row fetches the protected partial; failed fetches remain recoverable.
        const unloaded = page.locator(`[data-record="${requestId}"]`);
        failDiagnostics = true;
        await unloaded.locator('.integration-expand').click();
        await unloaded.locator('.integration-diagnostic-content').getByText('Diagnostics could not be loaded. Check your connection and staff session, then select View to try again.', { exact: true }).waitFor();
        failDiagnostics = false;
        await unloaded.getByRole('link', { name: 'View', exact: true }).click();
        await unloaded.locator('.integration-diagnostics').waitFor();
        assert.equal(await unloaded.locator('.integration-expand').getAttribute('aria-expanded'), 'true');
        // Real GET form submission preserves search, status, both dates and sorting.
        await page.getByRole('textbox', { name: 'Search', exact: true }).fill('John');
        await page.locator('#Status').selectOption('Failed');
        await page.locator('#From').fill('2026-01-01');
        await page.locator('#Through').fill('2026-12-31');
        await page.locator('#Sort').selectOption('activity');
        await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
        await page.waitForURL(url => url.searchParams.get('Search') === 'John');
        for (const [key, value] of Object.entries({ Search: 'John', Status: 'Failed', From: '2026-01-01', Through: '2026-12-31', Sort: 'activity' })) assert.equal(new URL(page.url()).searchParams.get(key), value);
        await page.getByRole('link', { name: 'Clear', exact: true }).click();
        await page.waitForURL('**/Staff/Integrations');
        assert.equal(new URL(page.url()).search, '');
        await page.goto('https://requestportal.test/Staff/Integrations?reviewer=true');
        assert.equal(await page.getByRole('button', { name: /^Retry|^Recheck/ }).count(), 0);
        assert.deepEqual(errors, []);
        fs.writeFileSync(path.join(root, 'integrations-viewport-results.json'), JSON.stringify(measurements, null, 2));
        console.log(`Passed ${measurements.length} viewports: fixed shell and grid header; no document overflow; 100 scrolling records; no nested diagnostic scrolling; expand/collapse, View, copy, fetch failure/recovery, filters, clear and reviewer permissions.`);
    } finally { await context.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
