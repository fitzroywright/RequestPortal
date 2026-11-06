(() => {
    const input = document.querySelector('[name="Input.MonthlyIncome"]');
    if (!input) return;
    const valid = /^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$/;
    const check = () => input.setCustomValidity(input.value && !valid.test(input.value)
        ? 'Enter weekly income in JMD without leading zeros, with at most two decimal places.' : '');
    input.addEventListener('keydown', event => {
        if (!event.ctrlKey && !event.metaKey && ['e', 'E', '+', '-'].includes(event.key)) event.preventDefault();
    });
    input.addEventListener('input', check);
    check();
})();
