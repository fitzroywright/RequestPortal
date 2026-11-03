(() => {
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
