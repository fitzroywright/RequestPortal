(() => {
    'use strict';
    const root = document.querySelector('[data-registered-users]');
    if (!root) return;
    const source = document.getElementById('ru-dashboard-data');
    if (!source) return;
    const data = JSON.parse(source.textContent);
    const form = root.querySelector('.ru-period');
    const period = root.querySelector('#ru-period');
    const custom = root.querySelector('.ru-custom');
    const update = () => { custom.hidden = period.value !== 'custom'; };
    period.addEventListener('change', () => {
        update();
        if (period.value !== 'custom') form.requestSubmit();
    });
    update();
    const charts = [];
    function showChart(id, type, labels, values, colors, horizontal = false) {
        const canvas = document.getElementById(id);
        if (!canvas || !window.Chart) { if (canvas) canvas.replaceWith(document.createTextNode('Chart unavailable. See data summary below.')); return; }
        const hasData = values.some(n => n > 0);
        if (!hasData) { canvas.replaceWith(document.createTextNode('No data for the selected period.')); return; }
        charts.push(new Chart(canvas, {
            type,
            data: { labels, datasets: [{ data: values, borderWidth: type === 'doughnut' ? 2 : 0,
                borderColor: '#fff', backgroundColor: colors, fill: type === 'line', tension: .35, pointRadius: 4, borderRadius: type === 'bar' ? 4 : 0 }] },
            options: { responsive: true, maintainAspectRatio: false, animation: false,
                cutout: type === 'doughnut' ? '70%' : undefined,
                plugins: { legend: { display: false }, tooltip: { enabled: true } },
                scales: type === 'doughnut' ? undefined : {
                    x: { grid: { display: false }, ticks: { color: '#52627a', maxRotation: 0 } },
                    y: { beginAtZero: true, ticks: { precision: 0, color: '#52627a' }, grid: { color: '#e6edf4' } }
                } }
        }));
    }
    showChart('ru-types', 'doughnut', ['Individual accounts', 'Organization representatives'],
        [data.individuals, data.representatives], ['#2b83fa', '#a67bea']);
    showChart('ru-status', 'doughnut', data.accountStates.map(x => x.name),
        data.accountStates.map(x => x.count), ['#25b383', '#fac34d', '#dd5c69']);
    const trend = data.trend;
    // Line chart takes a single color; the status and type charts have fixed semantic colors.
    const trendCanvas = document.getElementById('ru-trend');
    if (trendCanvas && window.Chart && trend.length > 0 && trend.some(x => x.count > 0)) {
        charts.push(new Chart(trendCanvas, {
            type: 'line',
            data: { labels: trend.map(x => x.label),
                datasets: [{ label: 'Registrations', data: trend.map(x => x.count),
                    borderColor: '#2a82ef', backgroundColor: '#2a82ef22',
                    fill: true, tension: .3, pointRadius: 4, pointBackgroundColor: '#fff', pointBorderWidth: 2 }] },
            options: { responsive: true, maintainAspectRatio: false, animation: false,
                plugins: { legend: { display: false } },
                scales: { x: { grid: { display: false }, ticks: { maxRotation: 0 } },
                    y: { beginAtZero: true, ticks: { precision: 0 }, grid: { color: '#e4ecf3' } } } }
        }));
    } else if (trendCanvas) {
        trendCanvas.replaceWith(document.createTextNode('No registrations during the selected period.'));
    }

    const parishCounts = new Map(data.parishes.map(x => [x.name, x.count]));
    const fills = ['#eff9ed', '#d7f0d7', '#b4e4bd', '#72c991', '#2c9d62'];
    const fill = n => fills[n >= 12 ? 4 : n >= 6 ? 3 : n >= 3 ? 2 : n >= 1 ? 1 : 0];
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 12000);
    let map;
    let observer;
    fetch(root.dataset.geographyUrl, { credentials: 'same-origin', signal: controller.signal })
        .then(response => { if (!response.ok) throw new Error('Map unavailable'); return response.json(); })
        .then(geo => {
            if (!window.L || geo.type !== 'FeatureCollection' || geo.features.length !== 14 ||
                geo.features.some(f => !parishCounts.has(f.properties.name))) throw new Error('Invalid parish map');
            const node = document.getElementById('registered-users-parish-map');
            node.replaceChildren();
            map = L.map(node, { zoomControl: false, scrollWheelZoom: false, dragging: false,
                doubleClickZoom: false, touchZoom: false, boxZoom: false, keyboard: false, zoomSnap: .1,
                attributionControl: true });
            map.attributionControl.setPrefix(false);
            map.attributionControl.addAttribution('Parishes: <a href="https://www.geoboundaries.org/">geoBoundaries</a> · CC BY-SA 2.0');
            const geometry = L.geoJSON(geo, {
                style: feature => ({ weight: 1.3, color: '#fff', fillOpacity: 1, fillColor: fill(parishCounts.get(feature.properties.name)) }),
                onEachFeature(feature, layer) {
                    const name = feature.properties.name;
                    const count = parishCounts.get(name);
                    const label = document.createElement('span');
                    label.textContent = name + ': ' + count + ' registered users';
                    layer.bindTooltip(label, { sticky: true });
                    layer.on('mouseover', () => layer.setStyle({ weight: 2, color: '#266d56' }));
                    layer.on('mouseout', () => layer.setStyle({ weight: 1.3, color: '#fff' }));
                }
            }).addTo(map);
            const labels = L.layerGroup().addTo(map);
            const fit = () => {
                if (!map) return;
                map.invalidateSize({ pan: false });
                map.fitBounds(geometry.getBounds(), { padding: [10, 8], animate: false });
                labels.clearLayers();
                geometry.eachLayer(layer => {
                    const name = layer.feature.properties.name;
                    const el = document.createElement('div');
                    el.append(document.createTextNode(name));
                    const count = document.createElement('strong');
                    count.textContent = parishCounts.get(name);
                    el.append(count);
                    const center = name === 'Kingston' ? L.latLng(17.97, -76.79) : layer.getBounds().getCenter();
                    L.marker(center, { interactive: false, icon: L.divIcon({ html: el, className: 'ru-map-label',
                        iconSize: [75, 26], iconAnchor: [37, 13] }) }).addTo(labels);
                });
            };
            const control = L.control({ position: 'bottomleft' });
            control.onAdd = () => {
                const box = L.DomUtil.create('div', 'ru-map-legend');
                [['12+', 4], ['6–11', 3], ['3–5', 2], ['1–2', 1], ['0', 0]].forEach(([label, ix]) => {
                    const row = document.createElement('div');
                    const swatch = document.createElement('i');
                    swatch.style.backgroundColor = fills[ix];
                    row.append(swatch, document.createTextNode(label));
                    box.append(row);
                });
                return box;
            };
            control.addTo(map);
            fit();
            observer = new ResizeObserver(fit);
            observer.observe(node);
        })
        .catch(() => {
            root.querySelector('.ru-map-error').textContent =
                'Map unavailable. Parish totals remain available in the accessible reporting table below.';
        })
        .finally(() => clearTimeout(timer));
    window.addEventListener('pagehide', () => {
        controller.abort(); clearTimeout(timer);
        observer?.disconnect(); charts.forEach(x => x.destroy()); map?.remove();
    }, { once: true });
})();
