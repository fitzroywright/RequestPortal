(() => {
    const monitor = document.querySelector('.integration-monitor');
    if (!monitor) return;
    const announcement = monitor.querySelector('.integration-announcement');
    const announce = message => { announcement.textContent = message; };
    async function toggle(record, forceOpen = false) {
        const trigger = record.querySelector('.integration-expand');
        const panel = record.querySelector('.integration-diagnostic-row');
        if (trigger.getAttribute('aria-busy') === 'true') return;
        if (!panel.hidden && !forceOpen) {
            panel.hidden = true;
            record.classList.remove('is-expanded');
            trigger.setAttribute('aria-expanded', 'false');
            return;
        }
        const content = panel.querySelector('.integration-diagnostic-content');
        if (!content.querySelector('.integration-diagnostics')) {
            trigger.setAttribute('aria-busy', 'true');
            announce('Loading integration diagnostics.');
            try {
                const response = await fetch(trigger.dataset.diagnosticsUrl, { headers: { 'Accept': 'text/html' }, credentials: 'same-origin', redirect: 'error' });
                if (!response.ok) throw new Error('Diagnostics could not be loaded.');
                const html = await response.text();
                const parsed = new DOMParser().parseFromString(html, 'text/html');
                if (!parsed.querySelector('.integration-diagnostics')) throw new Error('Diagnostics are unavailable.');
                content.replaceChildren(...parsed.body.childNodes);
                announce('Diagnostics loaded.');
            } catch {
                content.textContent = 'Diagnostics could not be loaded. Check your connection and staff session, then select View to try again.';
                announce(content.textContent);
            } finally { trigger.removeAttribute('aria-busy'); }
        }
        panel.hidden = false;
        record.classList.add('is-expanded');
        trigger.setAttribute('aria-expanded', 'true');
    }
    monitor.addEventListener('click', async event => {
        const expand = event.target.closest('.integration-expand, [data-view-diagnostics]');
        if (expand) {
            if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            await toggle(expand.closest('.integration-record'), expand.hasAttribute('data-view-diagnostics'));
            return;
        }
        const copy = event.target.closest('[data-copy-correlation]');
        if (copy) {
            try { await navigator.clipboard.writeText(copy.dataset.copyCorrelation); announce('Correlation ID copied.'); copy.title = 'Copied'; }
            catch { announce('Could not copy the correlation ID. Select and copy the displayed ID manually.'); }
            return;
        }
        const full = event.target.closest('[data-full-diagnostics]');
        if (full) {
            const detail = full.closest('.integration-diagnostics').querySelector('.integration-full-diagnostics');
            detail.hidden = !detail.hidden;
            full.setAttribute('aria-expanded', String(!detail.hidden));
            full.textContent = detail.hidden ? 'View Full Diagnostics' : 'Hide Full Diagnostics';
            if (!detail.hidden) {
                const evidence = detail.querySelector('[data-integration-exception]');
                if (evidence && !evidence.dataset.loaded) {
                    evidence.textContent = 'Loading protected exception evidence…';
                    try {
                        const response = await fetch(evidence.dataset.exceptionUrl, {
                            headers: { 'Accept': 'text/plain' }, credentials: 'same-origin', cache: 'no-store', redirect: 'error'
                        });
                        if (!response.ok) throw new Error('Exception evidence unavailable or access denied.');
                        evidence.textContent = (await response.text()).slice(0, 4000);
                        evidence.dataset.loaded = 'true';
                    } catch {
                        evidence.textContent = 'Unable to load exception evidence. Check your security-management permission and staff session.';
                    }
                }
            }
            return;
        }
        const system = event.target.closest('[data-system-toggle]');
        if (system) {
            const panel = monitor.querySelector('#integration-operations');
            panel.hidden = !panel.hidden;
            monitor.querySelectorAll('[data-system-toggle]').forEach(button => button.setAttribute('aria-expanded', String(!panel.hidden)));
            monitor.querySelectorAll('.integration-more-menu[open]').forEach(menu => menu.open = false);
            if (!panel.hidden) panel.scrollIntoView({ block: 'start' });
        }
    });
    monitor.addEventListener('toggle', event => {
        if (event.target.matches('.integration-more-menu[open]')) {
            monitor.querySelectorAll('.integration-more-menu[open]').forEach(menu => { if (menu !== event.target) menu.open = false; });
            event.target.querySelector('.integration-more-options').scrollIntoView({ block: 'nearest' });
        }
    }, true);
    document.addEventListener('click', event => {
        monitor.querySelectorAll('.integration-more-menu[open]').forEach(menu => { if (!menu.contains(event.target)) menu.open = false; });
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') monitor.querySelectorAll('.integration-more-menu[open]').forEach(menu => { menu.open = false; menu.querySelector('summary').focus(); });
    });
    // A server-rendered selection must scroll its own row region, never the shell.
    const selected = monitor.querySelector('.integration-record.is-expanded');
    if (selected) requestAnimationFrame(() => {
        const rows = monitor.querySelector('.integration-rows');
        const recordBounds = selected.querySelector('.integration-data-row').getBoundingClientRect();
        const rowBounds = rows.getBoundingClientRect();
        if (recordBounds.top < rowBounds.top || recordBounds.top >= rowBounds.bottom) {
            rows.scrollTop += recordBounds.top - rowBounds.top;
        }
    });
})();
