(() => {
    'use strict';
    const root = document.querySelector('[data-dashboard]');
    if (!root) return;
    const data = JSON.parse(document.getElementById('management-dashboard-data').textContent);
    const palette = ['#edf8e9', '#c5e6b5', '#83c8a5', '#40a99b', '#197d87', '#173955'];
    const select = root.querySelector('#management-parish');
    const selection = root.querySelector('[data-parish-selection]');
    const message = root.querySelector('[data-map-message]');
    const loading = root.querySelector('[data-map-loading]');
    const retry = root.querySelector('[data-map-retry]');
    const container = root.querySelector('.mg-map');
    const parishMetrics = new Map(data.parishes.map(item => [item.name, item]));
    const mapped = data.parishes.filter(item => item.name !== 'Unknown / not provided');
    const maximum = Math.max(0, ...mapped.map(item => item.total));
    const scripts = new Map();
    const controllers = new Set();
    const charts = [];
    let map, boundaries, resizeObserver, mapPending = false, disposed = false;
    const totalsText = item => `${item.name}: ${item.total} total requests; ${item.outstanding} outstanding; ${item.completed} completed`;
    const metrics = name => parishMetrics.get(name) || { name, total: 0, outstanding: 0, completed: 0 };
    const allText = `All parishes · ${data.parishes.reduce((total, item) => total + item.total, 0)} requests`;
    const color = count => palette[maximum === 0 ? 0 : Math.round(count / maximum * (palette.length - 1))];
    const loadScript = url => {
        if (!scripts.has(url)) scripts.set(url, new Promise((resolve, reject) => {
            const script = document.createElement('script');
            const failed = () => { clearTimeout(timer); scripts.delete(url); script.remove(); reject(new Error('Resource unavailable')); };
            const timer = setTimeout(failed, 12000);
            script.src = url; script.onload = () => { clearTimeout(timer); resolve(); };
            script.onerror = failed;
            document.head.append(script);
        }));
        return scripts.get(url);
    };
    const requestUrl = name => {
        const url = new URL(root.dataset.requestsUrl, location.origin);
        if (name) url.searchParams.set('parish', name);
        return url.href;
    };
    const style = feature => ({ color: '#527c7a', weight: .8, fillColor: color(metrics(feature.properties.name).total), fillOpacity: .72 });
    const updateSelection = () => {
        selection.textContent = select.value ? totalsText(metrics(select.value)) : allText;
        const details = root.querySelector('[data-parish-details]');
        details.hidden = !select.value;
        if (select.value) { const url = new URL(root.dataset.parishUrl, location.origin); url.searchParams.set('parish', select.value); details.href = url.href; }
        if (boundaries) boundaries.eachLayer(layer => {
            const selected = layer.feature.properties.name === select.value;
            layer.setStyle({ ...style(layer.feature), color: selected ? '#163653' : '#527c7a', weight: selected ? 3 : .8 });
            if (selected) layer.bringToFront();
        });
    };
    select.addEventListener('change', updateSelection);
    root.querySelector('[data-map-clear]').addEventListener('click', () => { select.value = ''; updateSelection(); });
    root.querySelector('[data-map-reset]').addEventListener('click', () => { if (map && boundaries) map.fitBounds(boundaries.getBounds(), { padding: [18, 18], animate: false }); });
    root.querySelector('.mg-parish-filter').addEventListener('submit', event => {
        event.preventDefault(); location.assign(requestUrl(select.value));
    });
    const setMessage = text => { message.textContent = text; message.hidden = !text; };
    async function initializeMap() {
        if (mapPending || map || disposed) return;
        mapPending = true; retry.hidden = true; loading.hidden = false; container.setAttribute('aria-busy', 'true'); setMessage('');
        const controller = new AbortController(); controllers.add(controller);
        const timeout = setTimeout(() => controller.abort(), 12000);
        try {
            const [, response] = await Promise.all([
                loadScript(root.dataset.leafletUrl),
                fetch(root.dataset.geographyUrl, { signal: controller.signal, credentials: 'same-origin' })
            ]);
            if (!response.ok) throw new Error('Boundary data unavailable');
            const geography = await response.json();
            const expected = new Set(mapped.map(item => item.name));
            const names = new Set(geography.features?.map(feature => feature.properties?.name));
            if (geography.type !== 'FeatureCollection' || geography.features.length !== 14 || names.size !== 14 || [...expected].some(name => !names.has(name))) throw new Error('Invalid parish geography');
            if (disposed) return;
            map = L.map(container, { scrollWheelZoom: false, zoomSnap: .25, zoomDelta: .5, minZoom: 6, maxZoom: 16, zoomControl: true, attributionControl: true });
            map.attributionControl.setPrefix(false);
            L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19, referrerPolicy: 'strict-origin-when-cross-origin',
                attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap contributors</a>'
            }).on('tileerror', () => setMessage('Street tiles are unavailable. Parish boundaries and the parish filter remain available.')).addTo(map);
            map.attributionControl.addAttribution('Parishes: <a href="https://www.geoboundaries.org/" target="_blank" rel="noopener">geoBoundaries</a> · <a href="https://creativecommons.org/licenses/by-sa/2.0/" target="_blank" rel="noopener">CC BY-SA 2.0</a>');
            boundaries = L.geoJSON(geography, {
                style,
                onEachFeature(feature, layer) {
                    const item = metrics(feature.properties.name);
                    const tooltip = document.createElement('div');
                    const heading = document.createElement('strong'); heading.textContent = item.name; tooltip.append(heading, document.createElement('br'));
                    tooltip.append(`${item.total} total requests`, document.createElement('br'), `${item.outstanding} outstanding`, document.createElement('br'), `${item.completed} completed`);
                    layer.bindTooltip(tooltip, { sticky: true, direction: 'auto', className: 'mg-parish-tooltip' });
                    layer.on('mouseover', () => layer.setStyle({ weight: 2, color: '#173955' }));
                    layer.on('mouseout', updateSelection);
                    layer.on('click', () => { select.value = item.name; updateSelection(); location.assign(requestUrl(item.name)); });
                    layer.on('add', () => {
                        const path = layer.getElement();
                        path.setAttribute('tabindex', '0'); path.setAttribute('role', 'link'); path.setAttribute('aria-label', totalsText(item) + '. View requests.');
                        L.DomEvent.on(path, 'focus', () => layer.openTooltip());
                        L.DomEvent.on(path, 'blur', () => layer.closeTooltip());
                        L.DomEvent.on(path, 'keydown', event => {
                            if (event.key === 'Enter' || event.key === ' ') { L.DomEvent.preventDefault(event); location.assign(requestUrl(item.name)); }
                        });
                    });
                }
            }).addTo(map);
            const parishLabels = L.layerGroup().addTo(map);
            const updateLabels = () => {
                parishLabels.clearLayers();
                if (container.clientWidth < 600 || map.getZoom() < 8.5) return;
                const occupied = [];
                boundaries.eachLayer(layer => {
                    const name = layer.feature.properties.name;
                    // Place labels on the main land polygon, rather than the first offshore cay in a MultiPolygon.
                    const geometry = layer.feature.geometry;
                    let center = layer.getCenter();
                    if (geometry.type === 'MultiPolygon') {
                        const area = ring => Math.abs(ring.slice(1).reduce((sum, point, i) => sum + ring[i][0] * point[1] - point[0] * ring[i][1], 0));
                        const main = geometry.coordinates.map(polygon => polygon[0]).reduce((largest, ring) => area(ring) > area(largest) ? ring : largest);
                        center = L.latLngBounds(main.map(([longitude, latitude]) => [latitude, longitude])).getCenter();
                    }
                    const point = map.latLngToContainerPoint(center);
                    const bounds = layer.getBounds();
                    const east = map.latLngToContainerPoint(bounds.getNorthEast());
                    const west = map.latLngToContainerPoint(bounds.getSouthWest());
                    const width = name.length * 5.3 + 8;
                    const box = { left: point.x - width / 2, right: point.x + width / 2, top: point.y - 9, bottom: point.y + 9 };
                    if (Math.abs(east.x - west.x) < width || Math.abs(east.y - west.y) < 22 ||
                        box.left < 10 || box.right > container.clientWidth - 10 || box.top < 10 || box.bottom > container.clientHeight - 10 ||
                        occupied.some(other => box.left < other.right && box.right > other.left && box.top < other.bottom && box.bottom > other.top)) return;
                    occupied.push(box);
                    const text = document.createElement('span'); text.textContent = name;
                    L.tooltip({ permanent: true, direction: 'center', className: 'mg-parish-label', opacity: 1, interactive: false })
                        .setLatLng(center).setContent(text).addTo(parishLabels);
                });
            };
            map.fitBounds(boundaries.getBounds(), { padding: [18, 18], animate: false });
            // Polygon centers become available after Leaflet's initial load has added the layers.
            map.on('zoomend moveend', updateLabels);
            const legend = L.control({ position: 'topright' });
            legend.onAdd = () => {
                const box = L.DomUtil.create('div', 'mg-map-legend');
                const title = document.createElement('strong'); title.textContent = 'Requests received'; box.append(title);
                const colors = document.createElement('div'); colors.className = 'mg-map-colors'; colors.setAttribute('aria-hidden', 'true');
                palette.forEach(value => { const swatch = document.createElement('i'); swatch.style.backgroundColor = value; colors.append(swatch); });
                const labels = document.createElement('div'); labels.className = 'mg-map-legend-labels';
                const low = document.createElement('span'); low.textContent = '0'; const high = document.createElement('span'); high.textContent = String(maximum);
                labels.append(low, high); box.append(colors, labels); box.title = maximum ? 'Colors scale from zero to the highest parish request count.' : 'No mapped requests recorded.';
                L.DomEvent.disableClickPropagation(box); return box;
            };
            legend.addTo(map); updateSelection(); updateLabels();
            resizeObserver = new ResizeObserver(() => {
                if (!map || disposed) return;
                map.invalidateSize({ pan: false });
                // Keep the complete country in view when the responsive card changes size.
                map.fitBounds(boundaries.getBounds(), { padding: [18, 18], animate: false });
            });
            resizeObserver.observe(container);
        } catch (error) {
            if (disposed) return;
            if (map) { map.remove(); map = null; boundaries = null; }
            setMessage('The Jamaica map could not be loaded. Use the parish filter to view requests, or retry the map.'); retry.hidden = false;
        } finally {
            clearTimeout(timeout); controllers.delete(controller); mapPending = false;
            loading.hidden = true; container.setAttribute('aria-busy', 'false');
        }
    }
    retry.addEventListener('click', initializeMap);
    async function initializeChart(element) {
        if (element.dataset.initialized || disposed) return;
        element.dataset.initialized = 'true';
        try {
            await loadScript(root.dataset.chartUrl);
            if (disposed) return;
            Chart.defaults.font.family = getComputedStyle(root).fontFamily;
            const kind = element.dataset.chartKind;
            const rows = kind === 'delivery' ? data.delivery : data.requests;
            const series = kind === 'delivery'
                ? [['delivered', 'Delivered', '#278b5c'], ['partial', 'Partially Delivered', '#c28b2c'], ['failed', 'Failed', '#c44f53']]
                : [['received', 'Received', '#287bc1'], ['completed', 'Completed', '#278b5c']];
            charts.push(new Chart(element.querySelector('canvas'), {
                type: 'line',
                data: { labels: rows.map(row => row.label), datasets: series.map(([key, label, borderColor]) => ({
                    label, data: rows.map(row => row[key]), borderColor, backgroundColor: borderColor,
                    borderWidth: 2, pointRadius: 2, pointHoverRadius: 4, tension: 0, fill: false
                })) },
                options: { responsive: true, maintainAspectRatio: false, animation: false,
                    interaction: { intersect: false, mode: 'index' },
                    plugins: { legend: { position: 'bottom', labels: { boxWidth: 12, boxHeight: 2, font: { size: 10 }, padding: 10 } } },
                    scales: { x: { grid: { display: false }, ticks: { maxTicksLimit: 8, maxRotation: 0, font: { size: 9 } } },
                        y: { beginAtZero: true, suggestedMax: rows.every(row => series.every(([key]) => row[key] === 0)) ? 1 : undefined,
                            ticks: { precision: 0, font: { size: 9 } }, grid: { color: '#edf1f5' } } }
                }
            }));
        } catch (error) {
            const note = document.createElement('p'); note.className = 'mg-chart-error'; note.textContent = 'Chart unavailable. View the reporting data below.';
            element.after(note); element.closest('section').querySelector('details').open = true;
        }
    }
    const observer = new IntersectionObserver(entries => entries.forEach(entry => {
        if (!entry.isIntersecting) return;
        observer.unobserve(entry.target);
        if (entry.target === container) initializeMap(); else initializeChart(entry.target);
    }), { rootMargin: '150px' });
    observer.observe(container); root.querySelectorAll('[data-chart-kind]').forEach(element => observer.observe(element));
    // An actual unload releases Leaflet, Chart.js, requests and observers. Keep BFCache pages intact.
    window.addEventListener('pagehide', event => {
        if (event.persisted) return;
        disposed = true; observer.disconnect(); resizeObserver?.disconnect(); controllers.forEach(controller => controller.abort());
        charts.forEach(chart => chart.destroy()); map?.remove(); map = null;
    });
})();
