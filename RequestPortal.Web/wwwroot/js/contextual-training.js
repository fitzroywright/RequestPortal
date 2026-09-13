(() => {
  const configUrl = '/training/training-videos.json';
  const normalize = value => (value || '/').replace(/\/$/, '') || '/';

  function close(dialog) {
    const frame = dialog.querySelector('iframe');
    if (frame) frame.src = '';
    dialog.close();
  }

  function createUi(item) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'training-help-button';
    button.setAttribute('aria-label', `Training: ${item.title}`);
    button.innerHTML = '<span aria-hidden="true">▶</span><span>Training</span>';

    const dialog = document.createElement('dialog');
    dialog.className = 'training-dialog';
    dialog.innerHTML = `
      <div class="training-dialog-card">
        <div class="training-dialog-heading">
          <div><span class="training-kicker">CONTEXTUAL TRAINING</span><h2></h2></div>
          <button type="button" class="training-close" aria-label="Close training">×</button>
        </div>
        <p class="training-description"></p>
        <div class="training-player"></div>
        <div class="training-meta"></div>
      </div>`;
    dialog.querySelector('h2').textContent = item.title;
    dialog.querySelector('.training-description').textContent = item.description || '';
    dialog.querySelector('.training-meta').textContent = item.duration ? `Expected length: ${item.duration}` : '';

    const player = dialog.querySelector('.training-player');
    if (item.videoUrl) {
      const frame = document.createElement('iframe');
      frame.title = item.title;
      frame.loading = 'lazy';
      frame.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share';
      frame.allowFullscreen = true;
      frame.dataset.src = item.videoUrl;
      player.appendChild(frame);
    } else {
      player.classList.add('training-coming-soon');
      player.innerHTML = '<strong>Training video coming soon</strong><span>This guide is ready for a video to be assigned without changing the application.</span>';
    }

    button.addEventListener('click', () => {
      const frame = dialog.querySelector('iframe');
      if (frame) frame.src = frame.dataset.src;
      dialog.showModal();
    });
    dialog.querySelector('.training-close').addEventListener('click', () => close(dialog));
    dialog.addEventListener('click', event => { if (event.target === dialog) close(dialog); });

    document.body.append(button, dialog);
  }

  fetch(configUrl, { cache: 'no-store' })
    .then(response => response.ok ? response.json() : [])
    .then(items => {
      const path = normalize(window.location.pathname).toLowerCase();
      const item = items
        .filter(x => x.active !== false && x.pathPrefix)
        .sort((a, b) => b.pathPrefix.length - a.pathPrefix.length)
        .find(x => path.startsWith(normalize(x.pathPrefix).toLowerCase()));
      if (item) createUi(item);
    })
    .catch(() => { /* Training must never interfere with the portal. */ });
})();
