(() => {
    const directory = document.querySelector('[data-staff-directory-filters]');
    if (directory) {
        const search = directory.querySelector('input[name="Search"]');
        let pendingSearch = null;
        const submitFilters = () => {
            if (pendingSearch !== null) {
                clearTimeout(pendingSearch);
                pendingSearch = null;
            }
            directory.requestSubmit();
        };
        directory.querySelectorAll('select[name="Status"],select[name="Role"]').forEach(select => {
            select.addEventListener('change', submitFilters);
        });
        if (search) {
            // Apply text search after the operator pauses typing; clearing it applies too.
            search.addEventListener('input', () => {
                if (pendingSearch !== null) clearTimeout(pendingSearch);
                pendingSearch = window.setTimeout(submitFilters, 400);
            });
            search.addEventListener('search', () => {
                // Also handles the native search field's clear control.
                if (pendingSearch !== null) clearTimeout(pendingSearch);
                pendingSearch = window.setTimeout(submitFilters, 0);
            });
            search.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.isComposing) {
                    event.preventDefault();
                    submitFilters();
                }
            });
        }
        const viewport = directory.querySelector('.staff-directory-scroll');
        if (viewport) {
            // Measure its actual screen position instead of assuming a fixed shell height.
            // Only the directory gets a scrollable height; other administration pages are unaffected.
            const sizeDirectory = () => {
                const available = Math.floor(window.innerHeight - viewport.getBoundingClientRect().top - 20);
                viewport.style.height = Math.max(160, available) + 'px';
            };
            sizeDirectory();
            window.addEventListener('resize', sizeDirectory, { passive: true });
        }
    }
    const form = document.querySelector('[data-staff-access]');
    if (!form) return;
    form.addEventListener('submit', event => {
        form.elements.Confirmed.value = 'false';
        const message = event.submitter?.dataset.confirm;
        if (!message) return;
        if (!window.confirm(message)) {
            event.preventDefault();
            return;
        }
        form.elements.Confirmed.value = 'true';
    });
})();
