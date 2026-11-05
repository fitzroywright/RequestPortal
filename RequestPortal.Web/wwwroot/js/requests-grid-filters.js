(() => {
    const form = document.querySelector('[data-requests-grid-filters]');
    if (!form) return;
    const filters = ['requests-applicant-filter', 'requests-organization-filter', 'requests-status-filter']
        .map(id => document.getElementById(id)).filter(Boolean);
    let pending;
    const apply = () => {
        window.clearTimeout(pending);
        pending = undefined;
        form.requestSubmit();
    };
    filters.forEach(filter => {
        if (filter.tagName === 'SELECT') {
            filter.addEventListener('change', apply);
        } else {
            filter.addEventListener('input', () => {
                window.clearTimeout(pending);
                pending = window.setTimeout(apply, 450);
            });
            filter.addEventListener('search', apply);
            filter.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.isComposing) {
                    event.preventDefault();
                    apply();
                }
            });
        }
    });
})();
