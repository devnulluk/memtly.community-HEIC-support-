'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const adminMode = location.pathname.toLowerCase().replace(/\/$/, '') === '/wedding/admin';
  document.body.classList.toggle('admin-view', adminMode);
  const queue = [];
  let state, running = false, page = 0, pages = 0, collectionOpen = adminMode, poll;
  const make = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };
  const size = bytes => bytes >= 1073741824 ? `${(bytes / 1073741824).toFixed(2)} GiB` : bytes < 1048576 ? `${Math.ceil(bytes / 1024)} KiB` : `${(bytes / 1048576).toFixed(1)} MiB`;
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  function notify(message) { $('notice').textContent = message; $('notice').hidden = !message; }
  async function api(path, body) {
    const response = await fetch('/Wedding/' + path, {
      method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-Wedding-CSRF': state.csrf },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!(response.headers.get('content-type') || '').includes('application/json'))
      throw new Error('Please reopen the homepage, or sign in again for the couple’s collection.');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Please try again.');
    return data;
  }
  function settingsUI() {
    $('closed-message').hidden = state.uploadsOpen;
    $('files').disabled = !state.uploadsOpen;
    $('guest-name').disabled = !state.uploadsOpen;
    $('toggle-uploads').textContent = state.uploadsOpen ? 'Close uploads' : 'Reopen uploads';
    $('toggle-gallery').textContent = state.galleryOpen ? 'Close shared gallery' : 'Reopen shared gallery';
    $('settings-status').textContent = `Uploads: ${state.uploadsOpen ? 'open' : 'closed'} · Gallery: ${state.galleryOpen ? 'open' : 'closed'}`;
  }
  function tab(uploads) {
    collectionOpen = uploads;
    $('upload-panel').hidden = uploads;
    $('collection-panel').hidden = !uploads;
    $('upload-tab').setAttribute('aria-pressed', String(!uploads));
    $('uploads-tab').setAttribute('aria-pressed', String(uploads));
    if (uploads) { page = 0; load(); }
  }
  async function initialise() {
    const invitation = new URLSearchParams(location.hash.slice(1)).get('invite');
    if (location.hash) history.replaceState(null, '', location.pathname + location.search);
    try {
      state = await api('Session');
      // Old invitation cards still work; public mode needs no fragment at all.
      if (invitation && !state.publicAccess) { await api('Enter', { token: invitation }); state = await api('Session'); }
      $('admin-link').hidden = !state.admin;
      $('file-limit').textContent = state.maxFileBytes > 0 ? `Up to ${size(state.maxFileBytes)} per file.` : 'Original files welcome.';
      $('sharing-note').hidden = !state.publicAccess;
      $('name-note').textContent = state.publicAccess ? 'Shown with your uploads; leave blank to stay anonymous.' : 'Only the couple sees your name.';
      settingsUI();
      if (adminMode) {
        $('privacy').hidden = true;
        $('heading').textContent = 'Our collection';
        $('collection-title').textContent = 'Our wedding collection';
        $('collection-kicker').textContent = 'MARK & KEZIAH';
        $('collection-note').textContent = state.publicAccess ? 'Shared previews; private originals. Hide items without deleting them.' : 'All uploads and their original files.';
        $('admin-tools').hidden = !state.admin;
        $('collection-panel').hidden = !state.admin;
        $('admin-denied').hidden = state.admin;
        if (state.admin) await load();
      } else {
        $('guest').hidden = !state.invited;
        $('locked').hidden = state.invited;
        if (!state.publicAccess) {
          $('uploads-tab').replaceChildren(document.createTextNode('Your uploads '), $('count'));
          $('view-uploads').textContent = 'View your uploads';
          $('collection-title').textContent = 'Your uploads';
          $('collection-note').textContent = 'Visible to this browser and the couple.';
        }
        if (state.invited) {
          try { $('guest-name').value = localStorage.getItem('wedding-optional-name') || ''; } catch (_) {}
          await load();
        }
      }
    } catch (error) { notify(error.message); }
  }
  $('guest-name').addEventListener('change', () => { try { localStorage.setItem('wedding-optional-name', $('guest-name').value); } catch (_) {} });
  function addFiles(files) {
    if (!state?.invited || !state.uploadsOpen) { notify('Uploads are currently closed.'); return; }
    $('thank-you').hidden = true;
    for (const file of files) {
      const li = make('li');
      const status = make('span', 'Waiting', 'status');
      const progress = document.createElement('progress'); progress.max = 100; progress.value = 0;
      progress.setAttribute('aria-label', `Upload progress for ${file.name}`);
      li.append(make('span', `${file.name} · ${size(file.size)}`), status, progress); $('queue').append(li);
      const item = { file, id: crypto.randomUUID().replace(/-/g, ''), status, progress, state: 'queued', name: $('guest-name').value.trim() };
      if (!/\.(jpe?g|png|webp|heic|heif|mp4|mov)$/i.test(file.name) || !file.size || (state.maxFileBytes > 0 && file.size > state.maxFileBytes)) {
        item.state = 'invalid'; status.textContent = 'Not sent: unsupported, empty or over the size limit.'; status.classList.add('failed');
      }
      queue.push(item);
    }
    $('queue-wrap').hidden = !queue.length;
    runQueue();
  }
  $('files').addEventListener('change', e => { addFiles(e.target.files); e.target.value = ''; });
  const zone = $('drop-zone');
  ['dragenter', 'dragover'].forEach(event => zone.addEventListener(event, e => { e.preventDefault(); zone.classList.add('drag'); }));
  ['dragleave', 'drop'].forEach(event => zone.addEventListener(event, e => { e.preventDefault(); zone.classList.remove('drag'); }));
  zone.addEventListener('drop', e => addFiles(e.dataTransfer.files));
  function upload(item) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/Wedding/Upload');
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.setRequestHeader('X-Wedding-CSRF', state.csrf);
      xhr.setRequestHeader('X-File-Name', encodeURIComponent(item.file.name));
      xhr.setRequestHeader('X-Guest-Name', encodeURIComponent(item.name));
      xhr.setRequestHeader('X-Share-Name', String(!!state.publicAccess));
      xhr.setRequestHeader('X-Upload-ID', item.id);
      xhr.setRequestHeader('X-File-Size', String(item.file.size));
      xhr.timeout = 0;
      xhr.upload.onprogress = e => { if (e.lengthComputable) { item.progress.value = Math.round(e.loaded / e.total * 100); item.status.textContent = item.progress.value === 100 ? 'Saving…' : `Uploading · ${item.progress.value}%`; } };
      xhr.onload = () => {
        let data; try { data = JSON.parse(xhr.responseText); } catch (_) {}
        if (xhr.status >= 200 && xhr.status < 300 && data?.success) { resolve(data); return; }
        const error = new Error(data?.error || 'Not confirmed. Please keep your original and retry.');
        error.status = xhr.status;
        error.retryAfter = Math.min(120, Math.max(1, Number(xhr.getResponseHeader('Retry-After')) || 5));
        reject(error);
      };
      xhr.onerror = xhr.onabort = () => reject(new Error('Connection interrupted. Please retry when connected.'));
      // The browser File is sent unchanged: no canvas or client-side re-encoding.
      xhr.send(item.file);
    });
  }
  async function runQueue() {
    if (running) return;
    running = true;
    try {
      let item;
      while ((item = queue.find(q => q.state === 'queued'))) {
        item.state = 'uploading'; item.status.classList.remove('failed');
        for (let attempt = 0; attempt < 9; attempt++) {
          try { await upload(item); item.state = 'saved'; item.progress.value = 100; item.status.textContent = 'Saved ♡'; break; }
          catch (error) {
            if (error.status === 429 && attempt < 8) {
              item.status.textContent = `Busy — retrying in ${error.retryAfter} seconds…`;
              await pause(error.retryAfter * 1000); continue;
            }
            item.state = 'failed'; item.status.textContent = 'Not sent: ' + error.message; item.status.classList.add('failed'); break;
          }
        }
      }
    } finally {
      running = false;
      $('retry-failed').hidden = !queue.some(q => q.state === 'failed');
      $('thank-you').hidden = !queue.some(q => q.state === 'saved');
      $('thank-you').querySelector('p').textContent = queue.some(q => q.state === 'failed' || q.state === 'invalid')
        ? 'Some files still need attention above.' : 'Your memories are safely with us.';
      await load();
    }
  }
  $('retry-failed').addEventListener('click', () => { queue.filter(q => q.state === 'failed').forEach(q => q.state = 'queued'); runQueue(); });
  window.addEventListener('beforeunload', e => { if (running) { e.preventDefault(); e.returnValue = ''; } });
  $('upload-tab').addEventListener('click', () => tab(false));
  $('uploads-tab').addEventListener('click', () => tab(true));
  $('view-uploads').addEventListener('click', () => { tab(true); $('collection-panel').scrollIntoView({ block: 'start' }); });
  $('refresh').addEventListener('click', load);
  $('previous').addEventListener('click', () => { if (page > 0) { page--; load(); } });
  $('next').addEventListener('click', () => { if (page + 1 < pages) { page++; load(); } });
  async function toggleSetting(key) {
    if (!confirm(`${state[key] ? 'Close' : 'Reopen'} ${key === 'uploadsOpen' ? 'new uploads' : 'the shared guest gallery'}?`)) return;
    try {
      const result = await api('PublicSettings', { uploadsOpen: state.uploadsOpen, galleryOpen: state.galleryOpen, [key]: !state[key] });
      Object.assign(state, result); settingsUI();
    } catch (error) { notify(error.message); }
  }
  $('toggle-uploads').addEventListener('click', () => toggleSetting('uploadsOpen'));
  $('toggle-gallery').addEventListener('click', () => toggleSetting('galleryOpen'));
  function openItem(item) {
    const media = document.createElement(item.video ? 'video' : 'img');
    media.src = `/Wedding/${item.video ? 'Video' : 'Preview'}/${item.id}`;
    if (item.video) { media.controls = true; media.playsInline = true; media.preload = 'metadata'; }
    else media.alt = item.filename;
    $('viewer-content').replaceChildren(media);
    $('viewer-caption').textContent = item.video && state.publicAccess ? 'Preview · up to two minutes. Full original kept for the couple.' : item.filename;
    $('viewer').showModal();
  }
  $('close-viewer').addEventListener('click', () => $('viewer').close());
  $('viewer').addEventListener('close', () => $('viewer-content').replaceChildren());
  function card(item) {
    const article = make('article', undefined, 'card');
    const button = make('button', undefined, 'thumbnail'); button.type = 'button';
    button.setAttribute('aria-label', `View ${item.filename}`);
    if (item.video && (item.preview === 'ready' || (!state.publicAccess && item.preview === 'video'))) {
      button.append(make('span', '▷ Play video', 'placeholder')); button.addEventListener('click', () => openItem(item));
    } else if (!item.video && item.preview === 'ready') {
      const img = document.createElement('img'); img.src = '/Wedding/Preview/' + item.id; img.alt = item.filename; img.loading = 'lazy';
      img.onerror = () => button.replaceChildren(make('span', 'Preview unavailable. Original saved.', 'placeholder'));
      button.append(img); button.addEventListener('click', () => openItem(item));
    } else {
      button.disabled = true;
      button.append(make('span', item.preview === 'unavailable' ? 'No preview. Original saved.' : 'Preparing preview…', 'placeholder'));
    }
    article.append(button, make('p', item.filename, 'filename'));
    if (item.name) article.append(make('p', item.name, 'small'));
    if (adminMode) {
      article.append(make('p', `${size(item.bytes)} · ${new Date(item.uploadedUtc).toLocaleString('en-GB')}`, 'small'));
      const download = make('a', 'Download original', 'button'); download.href = '/Wedding/Original/' + item.id; article.append(download);
      const action = (text, path, body) => {
        const b = make('button', text, 'secondary'); b.type = 'button';
        b.addEventListener('click', async () => { try { await api(path + '/' + item.id, body); await load(); } catch (error) { notify(error.message); } }); article.append(b);
      };
      action(item.reviewed ? '✓ Reviewed · undo' : 'Mark reviewed', 'Review', { reviewed: !item.reviewed });
      if (state.publicAccess) action(item.hidden ? 'Hidden · show in gallery' : 'Hide from gallery', 'Visibility', { hidden: !item.hidden });
      if (item.preview === 'unavailable') action('Try preview again', 'Retry', {});
    }
    return article;
  }
  async function load() {
    clearTimeout(poll);
    if (!state || (!state.invited && !adminMode)) return;
    try {
      const data = await api((adminMode ? 'AdminItems' : 'Items') + '?page=' + page);
      pages = data.pages;
      $('count').textContent = data.total ? `(${data.total})` : '';
      $('total').textContent = `${data.total} ${data.total === 1 ? 'memory' : 'memories'}`;
      $('gallery').replaceChildren(...data.items.map(card));
      if (!data.total) $('gallery').append(make('p', !adminMode && state.publicAccess && !state.galleryOpen ? 'The gallery is currently closed.' : 'Be the first to share a memory.', 'empty'));
      $('pagination').hidden = pages < 2;
      $('page-number').textContent = `Page ${page + 1} of ${pages}`;
      $('previous').disabled = page === 0; $('next').disabled = page + 1 >= pages;
      if (collectionOpen && !document.hidden) poll = setTimeout(load, data.items.some(i => i.preview === 'pending' || i.preview === 'processing') ? 5000 : 30000);
    } catch (error) { $('gallery').replaceChildren(); notify(error.message); }
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && collectionOpen) load(); });
  initialise();
})();
