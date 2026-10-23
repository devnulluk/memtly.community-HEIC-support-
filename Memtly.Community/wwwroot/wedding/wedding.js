'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const adminMode = location.pathname.toLowerCase().replace(/\/$/, '') === '/wedding/admin';
  const queue = [];
  let state, running = false, page = 0, pages = 0, collectionOpen = adminMode, poll;
  const make = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };
  const size = bytes => bytes >= 1073741824 ? `${(bytes / 1073741824).toFixed(2)} GiB` : bytes < 1048576 ? `${Math.ceil(bytes / 1024)} KiB` : `${(bytes / 1048576).toFixed(1)} MiB`;
  function notify(message) { $('notice').textContent = message; $('notice').hidden = !message; }
  async function api(path, body) {
    const response = await fetch('/Wedding/' + path, {
      method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-Wedding-CSRF': state.csrf },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const type = response.headers.get('content-type') || '';
    if (!type.includes('application/json')) throw new Error('Please reopen the private link, or sign in again if you are using the couple’s collection.');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'That request could not be completed. Please try again.');
    return data;
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
      if (invitation) {
        await api('Enter', { token: invitation });
        state = await api('Session');
      }
      $('admin-link').hidden = !state.admin;
      $('file-limit').textContent = state.maxFileBytes > 0
        ? `Up to ${size(state.maxFileBytes)} per file.`
        : 'Full-quality videos welcome. No fixed file-size cap, subject to available storage.';
      if (adminMode) {
        $('privacy').hidden = true;
        $('heading').replaceChildren(make('span', 'Every memory,'), document.createElement('br'), make('em', 'safely kept.'));
        $('collection-title').textContent = 'Our wedding collection';
        $('collection-kicker').textContent = 'JUST FOR MARK & KEZIAH';
        $('collection-note').textContent = 'All guests’ uploads, optional names and untouched originals. Only Memtly administrators can open this collection.';
        $('admin-tools').hidden = !state.admin;
        $('collection-panel').hidden = !state.admin;
        $('admin-denied').hidden = state.admin;
        if (state.admin) await load();
      } else {
        $('guest').hidden = !state.invited;
        $('locked').hidden = state.invited;
        if (state.invited) {
          try { $('guest-name').value = localStorage.getItem('wedding-optional-name') || ''; } catch (_) {}
          await load();
        }
      }
    } catch (error) { notify(error.message); if (!adminMode) $('locked').hidden = false; }
  }
  $('guest-name').addEventListener('change', () => { try { localStorage.setItem('wedding-optional-name', $('guest-name').value); } catch (_) {} });
  function addFiles(files) {
    $('thank-you').hidden = true;
    for (const file of files) {
      const li = make('li');
      const title = make('span', `${file.name} · ${size(file.size)}`);
      const status = make('span', 'Waiting to upload', 'status');
      const progress = document.createElement('progress'); progress.max = 100; progress.value = 0; progress.setAttribute('aria-label', `Upload progress for ${file.name}`);
      li.append(title, status, progress); $('queue').append(li);
      const item = { file, id: crypto.randomUUID().replace(/-/g, ''), status, progress, state: 'queued', name: $('guest-name').value.trim() };
      if (!/\.(jpe?g|png|webp|heic|heif|mp4|mov)$/i.test(file.name) || !file.size || (state.maxFileBytes > 0 && file.size > state.maxFileBytes)) {
        item.state = 'invalid'; status.textContent = 'Not sent: unsupported file type, empty file, or above the configured size limit.'; status.classList.add('failed');
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
  zone.addEventListener('drop', e => { if (state?.invited) addFiles(e.dataTransfer.files); });
  function upload(item) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/Wedding/Upload');
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.setRequestHeader('X-Wedding-CSRF', state.csrf);
      xhr.setRequestHeader('X-File-Name', encodeURIComponent(item.file.name));
      xhr.setRequestHeader('X-Guest-Name', encodeURIComponent(item.name));
      xhr.setRequestHeader('X-Upload-ID', item.id);
      xhr.setRequestHeader('X-File-Size', String(item.file.size));
      // No wall-clock cutoff for large videos. The server retains idle protections.
      xhr.timeout = 0;
      xhr.upload.onprogress = e => { if (e.lengthComputable) { item.progress.value = Math.round(e.loaded / e.total * 100); item.status.textContent = item.progress.value === 100 ? 'Checking and saving…' : `Uploading · ${item.progress.value}%`; } };
      xhr.onload = () => {
        let data; try { data = JSON.parse(xhr.responseText); } catch (_) {}
        if (xhr.status >= 200 && xhr.status < 300 && data?.success) resolve(data);
        else reject(new Error(data?.error || 'Not confirmed. Your file is still on your device; please retry.'));
      };
      xhr.onerror = xhr.ontimeout = xhr.onabort = () => reject(new Error('Connection interrupted. Please retry this file when you’re connected.'));
      xhr.send(item.file);
    });
  }
  async function runQueue() {
    if (running) return;
    running = true;
    try {
      let item;
      while ((item = queue.find(q => q.state === 'queued'))) {
        item.state = 'uploading'; item.status.classList.remove('failed'); item.status.textContent = 'Uploading…';
        try { await upload(item); item.state = 'saved'; item.progress.value = 100; item.status.textContent = 'Saved safely ♡'; }
        catch (error) { item.state = 'failed'; item.status.textContent = 'Not sent: ' + error.message; item.status.classList.add('failed'); }
      }
    } finally {
      running = false;
      $('retry-failed').hidden = !queue.some(q => q.state === 'failed');
      $('thank-you').hidden = !queue.some(q => q.state === 'saved');
      if (queue.some(q => q.state === 'failed' || q.state === 'invalid'))
        $('thank-you').querySelector('p').textContent = 'Thank you for the memories received. Some files above still need your attention.';
      else $('thank-you').querySelector('p').textContent = 'Thank you for sharing a little of our day with us.';
      await load();
    }
  }
  $('retry-failed').addEventListener('click', () => { queue.filter(q => q.state === 'failed').forEach(q => q.state = 'queued'); runQueue(); });
  window.addEventListener('beforeunload', e => { if (running) { e.preventDefault(); e.returnValue = ''; } });
  $('upload-tab').addEventListener('click', () => tab(false));
  $('uploads-tab').addEventListener('click', () => tab(true));
  $('view-uploads').addEventListener('click', () => { tab(true); $('collection-panel').scrollIntoView({ behavior: 'auto', block: 'start' }); });
  $('refresh').addEventListener('click', load);
  $('previous').addEventListener('click', () => { if (page > 0) { page--; load(); } });
  $('next').addEventListener('click', () => { if (page + 1 < pages) { page++; load(); } });
  function openItem(item) {
    const media = document.createElement(item.video ? 'video' : 'img');
    media.src = `/Wedding/${item.video ? 'Video' : 'Preview'}/${item.id}`;
    if (item.video) { media.controls = true; media.playsInline = true; media.preload = 'metadata'; }
    else media.alt = item.filename;
    $('viewer-content').replaceChildren(media);
    $('viewer-caption').textContent = item.video ? `${item.filename} · Some MOV/HEVC videos cannot play in this browser; the original is still safely stored.` : item.filename;
    $('viewer').showModal();
  }
  $('close-viewer').addEventListener('click', () => $('viewer').close());
  $('viewer').addEventListener('close', () => $('viewer-content').replaceChildren());
  function card(item) {
    const article = make('article', undefined, 'card');
    const button = make('button', undefined, 'thumbnail'); button.type = 'button';
    button.setAttribute('aria-label', `View ${item.filename}`);
    if (item.preview === 'ready') {
      const img = document.createElement('img'); img.src = '/Wedding/Preview/' + item.id; img.alt = item.filename; img.loading = 'lazy';
      img.onerror = () => button.replaceChildren(make('span', 'Preview unavailable. Your original is safe.', 'placeholder'));
      button.append(img); button.addEventListener('click', () => openItem(item));
    } else if (item.video) { button.append(make('span', '▷ Play your video', 'placeholder')); button.addEventListener('click', () => openItem(item)); }
    else { button.disabled = true; button.append(make('span', item.preview === 'unavailable' ? 'Original safely stored. A preview isn’t available for this file.' : 'Original safely stored. Preparing a preview…', 'placeholder')); }
    article.append(button, make('p', item.filename, 'filename'), make('p', size(item.bytes), 'small'));
    if (adminMode) {
      article.append(make('p', item.name || 'Name not supplied', 'small'));
      article.append(make('p', new Date(item.uploadedUtc).toLocaleString('en-GB'), 'small'));
      const download = make('a', 'Download original', 'button'); download.href = '/Wedding/Original/' + item.id; article.append(download);
      const review = make('button', item.reviewed ? '✓ Reviewed · undo' : 'Mark reviewed', 'secondary'); review.type = 'button';
      review.addEventListener('click', async () => { try { await api('Review/' + item.id, { reviewed: !item.reviewed }); await load(); } catch (error) { notify(error.message); } }); article.append(review);
      if (item.preview === 'unavailable') {
        const retry = make('button', 'Try preview again', 'secondary'); retry.type = 'button'; retry.addEventListener('click', async () => { try { await api('Retry/' + item.id, {}); await load(); } catch (error) { notify(error.message); } }); article.append(retry);
      }
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
      $('total').textContent = `${data.total} ${data.total === 1 ? 'memory' : 'memories'} safely received`;
      $('gallery').replaceChildren(...data.items.map(card));
      if (!data.total) $('gallery').append(make('p', adminMode ? 'The postbox is ready for its first memory.' : 'Your memories will appear here once you’ve sent them.', 'empty'));
      $('pagination').hidden = pages < 2;
      $('page-number').textContent = `Page ${page + 1} of ${pages}`;
      $('previous').disabled = page === 0; $('next').disabled = page + 1 >= pages;
      if (collectionOpen && !document.hidden && data.items.some(i => i.preview === 'pending' || i.preview === 'processing')) poll = setTimeout(load, 5000);
    } catch (error) { $('gallery').replaceChildren(); notify(error.message); }
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && collectionOpen) load(); });
  initialise();
})();
