(() => {
    for (const input of document.querySelectorAll('input[data-val-contacttelephone]')) {
        input.inputMode = 'tel';
        const check = () => input.setCustomValidity(input.value && !/^[+() .0-9-]+$/.test(input.value)
            ? input.dataset.valContacttelephone : '');
        input.addEventListener('input', check);
        check();
    }
})();
