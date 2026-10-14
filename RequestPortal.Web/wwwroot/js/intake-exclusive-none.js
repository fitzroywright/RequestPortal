(() => {
    const groups = [
        ['Input.AssistancePrograms', 'None'],
        ['Input.VictimCircumstances', 'None of the above'],
        ['ProfileInput.AssistancePrograms', 'None'],
        ['ProfileInput.VictimCircumstances', 'None of the above']
    ];
    for (const [name, noneValue] of groups) {
        const boxes = Array.from(document.querySelectorAll('input[type="checkbox"][name="' + name + '"]'));
        if (!boxes.length) continue;
        const updateValidity = () => {
            boxes[0].setCustomValidity(boxes.some(box => box.checked)
                ? '' : 'Select at least one option, or select ' + noneValue + '.');
        };
        boxes.forEach(box => box.addEventListener('change', () => {
            if (box.checked) {
                boxes.forEach(other => {
                    if (other !== box && (box.value === noneValue || other.value === noneValue))
                        other.checked = false;
                });
            }
            updateValidity();
        }));
        updateValidity();
    }
})();
