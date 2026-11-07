(() => {
    'use strict';
    const root = document.querySelector('[data-quick-intake-dashboard]');
    if (!root) return;
    const data = JSON.parse(document.getElementById('quick-intake-dashboard-data').textContent);
    const palette = ['#2e82ff', '#22b95d', '#9951ff', '#ffd24e', '#20b4bb', '#ef7c9c', '#cbd1db'];
    const charts = [];
    let map, observer;
    const period = document.getElementById('qi-period');
    const dateForm = root.querySelector('.qi-date-filter');
    dateForm.dataset.custom = String(period.value === 'custom');
    period.addEventListener('change', () => {
        const custom = period.value === 'custom';
        dateForm.dataset.custom = String(custom);
        root.querySelector('.qi-custom-dates').hidden = !custom;
        if (!custom) dateForm.requestSubmit();
    });
    const empty = (container, text) => {
        container.replaceChildren();
        const state = document.createElement('p'); state.className = 'qi-unavailable'; state.textContent = text;
        container.append(state);
    };
    const valuesPlugin = { id: 'qiValues', afterDatasetsDraw(chart) {
        if (chart.config.type !== 'bar') return;
        const { ctx } = chart; const horizontal = chart.options.indexAxis === 'y';
        ctx.save(); ctx.font = 'bold 12px Inter, sans-serif'; ctx.fillStyle = '#09132f';
        chart.getDatasetMeta(0).data.forEach((bar, index) => {
            const value = chart.data.datasets[0].data[index];
            ctx.textAlign = horizontal ? 'left' : 'center';
            ctx.fillText(String(value), horizontal ? bar.x + 7 : bar.x, horizontal ? bar.y + 4 : bar.y - 6);
        }); ctx.restore();
    }};
    function legend(key, items, colors) {
        const list = root.querySelector(`[data-legend="${key}"]`);
        if (!list) return;
        items.forEach((item, index) => {
            if (!item.count) return;
            const li = document.createElement('li'), swatch = document.createElement('i'), name = document.createElement('span'), count = document.createElement('strong'), percent = document.createElement('small');
            swatch.style.backgroundColor = colors[index % colors.length]; name.textContent = item.name;
            count.textContent = item.count; percent.textContent = `${item.percent}%`;
            li.append(swatch, name, count, percent); list.append(li);
        });
    }
    function chart(id, dimension, type, horizontal = false, legendKey = null, colors = palette) {
        const canvas = document.getElementById(id);
        const container = root.querySelector(`[data-dimension="${dimension}"]`) || canvas.closest('.qi-doughnut-layout');
        const group = dimension === 'types' ? { items: data.requestTypes } : data[dimension];
        if (group.suppressed || group.unavailable) { empty(container, group.unavailable || 'Distribution suppressed for privacy.'); return; }
        if (!data.total) { empty(container, 'No Quick Intakes in this date range.'); return; }
        if (!window.Chart) { empty(container, 'Chart unavailable. Use the accessible data table below.'); return; }
        const items = group.items.filter(item => item.count > 0 || item.name !== "Not provided / unknown");
        const instance = new Chart(canvas, {
            type,
            data: { labels: items.map(v => v.name), datasets: [{ data: items.map(v => v.count), backgroundColor: type === 'doughnut' ? colors : '#3788ff', borderColor: type === 'doughnut' ? '#fff' : '#3788ff', borderWidth: type === 'doughnut' ? 1 : 0, borderRadius: type === 'bar' ? 3 : 0, maxBarThickness: horizontal ? 15 : 48 }] },
            plugins: [valuesPlugin],
            options: { responsive: true, maintainAspectRatio: false, animation: false, cutout: type === 'doughnut' ? '67%' : undefined,
                indexAxis: horizontal ? 'y' : 'x', layout: { padding: type === 'bar' ? { top: 15, right: 25 } : 3 },
                plugins: { legend: { display: false }, tooltip: { callbacks: { label: ctx => `${dimension === 'income' ? 'Monthly income (JMD): ' : dimension === 'weeklyIncome' ? 'Weekly income (JMD): ' : ''}${items[ctx.dataIndex].count} (${items[ctx.dataIndex].percent}% of all Quick Intakes)` } } },
                scales: type === 'bar' ? {
                    x: { beginAtZero: true, display: !horizontal, grid: { display: false }, border: { display: false }, ticks: { color: '#09132f', font: { size: 12 }, maxRotation: 0 }, grace: horizontal ? '18%' : undefined },
                    y: { beginAtZero: true, grid: { display: !horizontal, color: '#e6eaf1' }, border: { display: false }, ticks: { color: '#09132f', precision: 0, font: { size: 12 }, autoSkip: false }, grace: horizontal ? undefined : '18%' }
                } : undefined }
        });
        charts.push(instance);
        if (legendKey) legend(legendKey, items, colors);
        return instance;
    }
    chart('qi-types', 'types', 'doughnut', false, 'types');
    chart('qi-children-under-18', 'childrenUnder18', 'bar', true);
    chart('qi-employment', 'employment', 'bar', true);
    let incomeChart = chart('qi-income', 'weeklyIncome', 'bar', true);
    document.getElementById('qi-income-period').addEventListener('change', event => {
        const weekly = event.target.value === 'weekly';
        const title = `${weekly ? 'Weekly' : 'Monthly'} Income (JMD)`;
        document.getElementById('qi-income-title').textContent = title;
        root.querySelectorAll('[data-income-table]').forEach(table => { table.hidden = table.dataset.incomeTable !== event.target.value; });
        incomeChart?.destroy();
        if (incomeChart) charts.splice(charts.indexOf(incomeChart), 1);
        const container = root.querySelector('[data-dimension="weeklyIncome"], [data-dimension="income"]');
        container.dataset.dimension = weekly ? 'weeklyIncome' : 'income';
        const canvas = document.createElement('canvas'); canvas.id = 'qi-income';
        canvas.setAttribute('role', 'img'); canvas.setAttribute('aria-label', title);
        container.replaceChildren(canvas);
        incomeChart = chart('qi-income', container.dataset.dimension, 'bar', true);
    });
    chart('qi-gender', 'gender', 'doughnut', false, 'gender', ['#2e82ff', '#9951ff', '#c6bbff', '#cbd1db']);
    chart('qi-ages', 'ages', 'bar');
    const metrics = new Map(data.parishes.map(p => [p.name, p.count]));
    // Relative color scale: a parish with the most requests always has the
    // darkest blue. Rank distinct positive counts so that large reporting
    // periods do not collapse every parish above a fixed threshold into one shade.
    const fills = ['#f2f8ff', '#dfedff', '#c5dfff', '#95c5f2', '#559ad8'];
    const distinctPositive = [...new Set([...metrics.values()].filter(n => n > 0))].sort((a, b) => a - b);
    const shadeIndex = count => {
        if (!count) return 0;
        const rank = distinctPositive.indexOf(count);
        return distinctPositive.length === 1 ? 4 : 1 + Math.floor(3 * rank / (distinctPositive.length - 1));
    };
    const color = count => fills[shadeIndex(count)];
    const legendBands = [4, 3, 2, 1, 0].map(index => {
        const counts = [...metrics.values()].filter(n => shadeIndex(n) === index);
        if (!counts.length) return null;
        const low = Math.min(...counts), high = Math.max(...counts);
        return { index, label: low === high ? String(low) : `${low}–${high}` };
    }).filter(Boolean);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 12000);
    fetch(root.dataset.geographyUrl, { credentials: 'same-origin', signal: controller.signal }).then(response => {
        if (!response.ok) throw new Error('Map unavailable'); return response.json();
    }).then(geography => {
        if (!window.L || geography.type !== 'FeatureCollection' || geography.features.length !== 14 || new Set(geography.features.map(f => f.properties.name)).size !== 14 || geography.features.some(f => !metrics.has(f.properties.name))) throw new Error('Invalid parish map');
        const container = document.getElementById('quick-intake-parish-map'); container.replaceChildren();
        map = L.map(container, { zoomControl: false, scrollWheelZoom: false, dragging: false, doubleClickZoom: false, touchZoom: false, boxZoom: false, keyboard: false, zoomSnap: .1, attributionControl: true });
        map.attributionControl.setPrefix(false);
        map.attributionControl.addAttribution('Parishes: <a href="https://www.geoboundaries.org/">geoBoundaries</a> · CC BY-SA 2.0');
        const boundaries = L.geoJSON(geography, { style: f => ({ color: '#fff', weight: 1.3, fillColor: color(metrics.get(f.properties.name)), fillOpacity: 1 }),
            onEachFeature(feature, layer) {
                const name = feature.properties.name, count = metrics.get(name);
                const tooltip = document.createElement('span'); tooltip.textContent = `${name}: ${count} Quick Intakes`;
                layer.bindTooltip(tooltip, { sticky: true });
                const open = () => { const url = new URL(root.dataset.recordsUrl, location.origin); url.searchParams.set('From', data.range.from); url.searchParams.set('Through', data.range.through); url.searchParams.set('Parish', name); location.assign(url); };
                layer.on('click', open);
                layer.on('mouseover', () => layer.setStyle({ weight: 2, color: '#173955' }));
                layer.on('mouseout', () => layer.setStyle({ weight: 1.3, color: '#fff' }));
                layer.on('add', () => { const path = layer.getElement(); if (path) { path.setAttribute('tabindex', '0'); path.setAttribute('role', 'link'); path.setAttribute('aria-label', `${name}: ${count} Quick Intakes. View records.`); path.addEventListener('keydown', e => { if (e.key === 'Enter') open(); }); } });
            }
        }).addTo(map);
        const labels = L.layerGroup().addTo(map);
        const fit = () => {
            map.invalidateSize({ pan: false }); map.fitBounds(boundaries.getBounds(), { padding: [10, 7], animate: false }); labels.clearLayers();
            boundaries.eachLayer(layer => {
                const name = layer.feature.properties.name, count = metrics.get(name);
                const element = document.createElement('div'); element.append(document.createTextNode(name)); const strong = document.createElement('strong'); strong.textContent = count; element.append(strong);
                const center = name === 'Kingston' ? L.latLng(17.97, -76.79) : layer.getBounds().getCenter();
                const offsets = { 'St. Andrew': [15, -5], 'St. Catherine': [-12, -5], 'Kingston': [3, 17], 'Manchester': [3, 8] };
                const [dx, dy] = offsets[name] || [0, 0];
                L.marker(center, { interactive: false, icon: L.divIcon({ html: element, className: 'qi-map-label', iconSize: [75, 26], iconAnchor: [37 - dx, 13 - dy] }) }).addTo(labels);
            });
        };
        const legendControl = L.control({ position: 'bottomleft' });
        legendControl.onAdd = () => {
            const box = L.DomUtil.create('div', 'qi-map-legend');
            const title = document.createElement('strong');
            title.textContent = 'Quick Intakes by parish';
            box.append(title);
            legendBands.forEach(({ index, label }) => {
                const row = document.createElement('span'), swatch = document.createElement('i');
                swatch.style.backgroundColor = fills[index];
                row.append(swatch, document.createTextNode(label));
                box.append(row);
            });
            return box;
        };
        legendControl.addTo(map); fit(); observer = new ResizeObserver(fit); observer.observe(container);
    }).catch(() => { root.querySelector('.qi-map-status').textContent = 'Map unavailable. All parish counts and filtered records remain accessible below.'; }).finally(() => clearTimeout(timer));
    window.addEventListener('pagehide', () => { controller.abort(); clearTimeout(timer); observer?.disconnect(); charts.forEach(c => c.destroy()); map?.remove(); }, { once: true });
})();
