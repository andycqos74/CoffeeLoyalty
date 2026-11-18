// Admin -> Pages: edit the words and photos on the customer-facing pages (join page, loyalty card, privacy policy)
// with a live preview, upload or choose photos, and animate photos (presets or "describe it" via Claude).
// Server side: PageContent.cs, PageRenderer.cs and Animations.cs; uses api() and pinVal from admin/index.html.
window.PagesEditor = (() => {
  let root, data, draft = {}, page = 'join', previewTimer, previewSeq = 0, frame, saving = false;

  // Tiny DOM helper: el('div', { className: 'x', onclick }, [children or strings])
  function el(tag, props = {}, children = []) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
      else if (k === 'dataset') Object.assign(n.dataset, v);
      else if (k in n && k !== 'list') n[k] = v;
      else n.setAttribute(k, v === true ? '' : v);
    }
    for (const c of [].concat(children)) if (c != null && c !== false) n.append(c);
    return n;
  }

  function toast(msg, opts = {}) {
    let t = document.querySelector('.pg-toast');
    if (!t) { t = el('div', { className: 'pg-toast', role: 'status' }); document.body.append(t); }
    t.textContent = msg; t.classList.toggle('is-error', !!opts.error); t.hidden = false;
    clearTimeout(t._timer);
    if (!opts.sticky) t._timer = setTimeout(() => { t.hidden = true; }, opts.error ? 6000 : 3500);
  }
  const hideToast = () => { const t = document.querySelector('.pg-toast'); if (t) t.hidden = true; };

  const field = key => data.fields.find(f => f.key === key);
  const value = f => (f.key in draft ? (draft[f.key] ?? f.default) : f.value);
  const dirty = () => Object.keys(draft).length > 0;
  const isPhoto = src => /\.(jpe?g|png|webp)$/i.test(src || '');

  window.addEventListener('beforeunload', e => { if (data && dirty()) { e.preventDefault(); e.returnValue = ''; } });

  async function open(container) {
    root = container;
    if (!data) {
      root.replaceChildren(el('p', { className: 'pg-muted', textContent: 'Loading…' }));
      try { data = await api('/api/admin/pages'); } catch (e) { root.replaceChildren(el('p', { className: 'err', textContent: e.message })); return; }
    }
    render();
  }

  function setDraft(key, v) {
    const f = field(key);
    // Back to what's saved: no longer a change.
    if ((v === null && !f.edited) || (v !== null && v === f.value && f.edited) || (v !== null && !f.edited && v === f.default)) delete draft[key];
    else draft[key] = v;
    updateBar();
    schedulePreview();
  }

  // Which sections each preview page shows.
  const SECTIONS = { join: ['join', 'brand', 'privacy'], card: ['card', 'brand'] };

  function render() {
    const tabs = el('div', { className: 'pg-tabs', role: 'tablist' }, [['join', 'Join page'], ['card', 'Loyalty card']].map(([k, label]) =>
      el('button', { type: 'button', className: 'pg-tab', role: 'tab', ariaSelected: String(page === k), textContent: label,
        onclick: () => { page = k; render(); } })));
    const bar = el('div', { className: 'pg-bar' }, [
      el('span', { className: 'pg-status', id: 'pgStatus' }),
      el('button', { type: 'button', className: 'small', id: 'pgDiscard', textContent: 'Discard changes', onclick: discard }),
      el('button', { type: 'button', className: 'act', id: 'pgSave', textContent: 'Save changes', onclick: save }),
    ]);
    const form = el('div', { className: 'pg-form' });
    for (const p of SECTIONS[page]) {
      const label = data.pages.find(x => x.key === p)?.label ?? p;
      const fields = data.fields.filter(f => f.page === p);
      form.append(el('section', { className: 'panel pg-section' }, [
        el('h2', { textContent: label }),
        p === 'privacy' ? el('p', { className: 'pg-muted', textContent: 'Opens from the link at the foot of the join page.' }) : null,
        ...fields.map(fieldRow),
      ]));
    }
    frame = el('iframe', { className: 'pg-frame', title: 'Preview of the customer page' });
    frame.addEventListener('load', () => { try { frame.contentWindow.scrollTo(0, frame._scroll || 0); } catch (e) {} });
    const preview = el('div', { className: 'pg-preview' }, [
      el('div', { className: 'pg-phone' }, frame),
      el('p', { className: 'pg-muted' }, ['Preview with your unsaved changes. ',
        el('a', { href: page === 'join' ? '/join.html' : '#', target: '_blank', rel: 'noopener', textContent: page === 'join' ? 'Open the live page' : '',
          hidden: page !== 'join' })]),
    ]);
    root.replaceChildren(
      el('div', { className: 'pg-head' }, [
        el('div', {}, [el('h2', { textContent: 'Customer pages', style: 'margin:0' }),
          el('p', { className: 'pg-muted', textContent: `Theme: ${data.theme.label}. Changes reach customers when you save.` })]),
        tabs, bar]),
      el('div', { className: 'pg-body' }, [form, preview]));
    updateBar();
    refreshPreview();
  }

  function updateBar() {
    const n = Object.keys(draft).length;
    const s = document.getElementById('pgStatus');
    if (!s) return;
    s.textContent = n ? `${n} unsaved change${n === 1 ? '' : 's'}` : 'All changes saved';
    document.getElementById('pgSave').disabled = !n || saving;
    document.getElementById('pgDiscard').disabled = !n;
    for (const row of root.querySelectorAll('.pg-field')) {
      const f = field(row.dataset.key);
      const isDefault = f.key in draft ? draft[f.key] === null || draft[f.key] === f.default : !f.edited;
      row.querySelector('.pg-reset').hidden = isDefault;
      row.classList.toggle('is-changed', f.key in draft);
    }
  }

  function fieldRow(f) {
    const id = `pg-${f.key.replace(/\./g, '-')}`;
    const reset = el('button', { type: 'button', className: 'pg-reset small', textContent: 'Use theme default',
      onclick: () => { setDraft(f.key, null); render(); } });
    let input;
    if (f.type === 'image') input = imageInput(f);
    else if (f.type === 'text') {
      input = el('input', { id, type: 'text', value: value(f), maxLength: 500 });
      input.addEventListener('input', () => setDraft(f.key, input.value));
    } else {
      const rows = f.type === 'rich' ? 16 : f.type === 'headline' ? 4 : 3;
      input = el('textarea', { id, rows, value: value(f), className: f.type === 'rich' ? 'pg-rich' : '' });
      input.addEventListener('input', () => setDraft(f.key, input.value));
    }
    return el('div', { className: 'pg-field', dataset: { key: f.key } }, [
      el('div', { className: 'pg-label-row' }, [el('label', { htmlFor: id, className: 'pg-label', textContent: f.label }), reset]),
      input,
      f.help ? el('p', { className: 'pg-help', textContent: f.help }) : null,
    ]);
  }

  function imageInput(f) {
    const src = value(f);
    const file = el('input', { type: 'file', accept: 'image/jpeg,image/png,image/webp', hidden: true });
    file.addEventListener('change', async () => {
      if (!file.files[0]) return;
      try {
        toast('Uploading…', { sticky: true });
        const url = await upload(file.files[0], f.key === 'brand.logo' ? 1024 : 2000);
        hideToast(); setDraft(f.key, url); render();
      } catch (e) { toast(e.message, { error: true }); }
    });
    const buttons = [
      el('button', { type: 'button', className: 'small', textContent: 'Upload a photo…', onclick: () => file.click() }),
      el('button', { type: 'button', className: 'small', textContent: 'Choose from library…', onclick: () => chooseFromLibrary(f) }),
    ];
    if (isPhoto(src) && f.key !== 'brand.logo')
      buttons.push(el('button', { type: 'button', className: 'small pg-animate', textContent: '✦ Animate this photo…', onclick: () => animatePanel(src) }));
    if (src && f.key !== 'brand.logo')
      buttons.push(el('button', { type: 'button', className: 'small danger', textContent: 'Remove', onclick: () => { setDraft(f.key, ''); render(); } }));
    return el('div', { className: 'pg-image' }, [
      el('div', { className: 'pg-thumb' + (f.key === 'brand.logo' ? ' is-logo' : '') }, src ? el('img', { src, alt: '' }) : el('span', { textContent: 'No photo' })),
      el('div', { className: 'pg-image-side' }, [el('code', { className: 'pg-path', textContent: src || '—' }), el('div', { className: 'pg-buttons' }, buttons), file]),
    ]);
  }

  // Resizes in the browser (longest side maxSide) and uploads. PNGs stay PNG (logos with transparency).
  async function upload(fileObj, maxSide) {
    if (!/^image\/(jpeg|png|webp)$/.test(fileObj.type)) throw new Error('Choose a JPEG, PNG or WebP photo.');
    const url = URL.createObjectURL(fileObj);
    let blob;
    try {
      const img = await AnimAnalyse.loadImage(url);
      const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
      const c = document.createElement('canvas');
      c.width = Math.round(img.naturalWidth * scale); c.height = Math.round(img.naturalHeight * scale);
      const ctx = c.getContext('2d');
      const png = fileObj.type === 'image/png';
      if (!png) { ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, c.width, c.height); }
      ctx.drawImage(img, 0, 0, c.width, c.height);
      blob = await new Promise(ok => c.toBlob(ok, png ? 'image/png' : 'image/jpeg', 0.86));
    } finally { URL.revokeObjectURL(url); }
    if (!blob) throw new Error('That photo could not be read.');
    const res = await fetch('/api/admin/media', { method: 'POST', headers: { 'X-Pin': pinVal, 'Content-Type': blob.type }, body: blob });
    const d = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(d.error || 'Upload failed.');
    return d.url;
  }

  function modalShell(title, body, extraClass = '') {
    const close = () => { modal.remove(); document.removeEventListener('keydown', onKey); };
    const onKey = e => { if (e.key === 'Escape') close(); };
    const modal = el('div', { className: 'pg-modal', role: 'dialog', ariaModal: 'true', ariaLabel: title },
      el('div', { className: `pg-modal-box ${extraClass}` }, [
        el('div', { className: 'pg-modal-head' }, [el('h2', { textContent: title }), el('button', { type: 'button', className: 'small', textContent: 'Close', onclick: close })]),
        body]));
    modal.addEventListener('click', e => { if (e.target === modal) close(); });
    document.body.append(modal);
    document.addEventListener('keydown', onKey);
    return { modal, close };
  }

  async function chooseFromLibrary(f) {
    let items;
    try { items = await api('/api/admin/media'); } catch (e) { return toast(e.message, { error: true }); }
    let shell;
    const grid = el('div', { className: 'pg-library' }, items.length ? items.map(m => el('button', {
      type: 'button', className: 'pg-library-item', title: m.url,
      onclick: () => { shell.close(); setDraft(f.key, m.url); render(); },
    }, [el('img', { src: m.url, alt: '', loading: 'lazy' }), el('span', { textContent: m.source === 'theme' ? 'Theme' : 'Uploaded' })]))
      : [el('p', { className: 'pg-muted', textContent: 'No photos yet. Use “Upload a photo…”.' })]);
    shell = modalShell('Choose a photo', grid);
  }

  function schedulePreview() { clearTimeout(previewTimer); previewTimer = setTimeout(refreshPreview, 350); }

  async function refreshPreview() {
    if (!frame) return;
    const seq = ++previewSeq;
    try {
      const res = await fetch('/api/admin/pages/preview', { method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Pin': pinVal }, body: JSON.stringify({ page, values: draft }) });
      if (!res.ok || seq !== previewSeq) return;
      const html = await res.text();
      try { frame._scroll = frame.contentWindow.scrollY; } catch (e) {}
      frame.srcdoc = html;
    } catch (e) { /* offline: keep the last preview */ }
  }

  async function save() {
    if (!dirty() || saving) return;
    saving = true; updateBar();
    try {
      await api('/api/admin/pages', { method: 'PUT', body: JSON.stringify({ values: draft }) });
      data = await api('/api/admin/pages'); draft = {};
      toast('Saved. Customers see the changes now.');
      render();
    } catch (e) { toast(e.message, { error: true }); }
    saving = false; updateBar();
  }

  function discard() {
    if (!dirty() || !confirm('Discard your unsaved changes?')) return;
    draft = {}; render();
  }

  // ---------- Animate this photo (Animations.cs, /lib/animated-image.js) ----------
  // Pick a preset, switch effects on and off, adjust strength and speed, or describe the animation and let Claude
  // propose settings. The preview plays exactly what customers will see; nothing changes for them until "Save
  // animation". The animation belongs to the photo, wherever it's used.
  async function animatePanel(src) {
    let info;
    try {
      toast('Looking at the photo…', { sticky: true });
      info = await api('/api/admin/animations?src=' + encodeURIComponent(src));
      if (info.needsAnalysis) {
        const analysis = await AnimAnalyse.detectLights(src);
        info = await api('/api/admin/animations/analysis', { method: 'POST', body: JSON.stringify({ src, analysis }) });
      }
      hideToast();
    } catch (e) { return toast(e.message, { error: true }); }

    const clone = o => JSON.parse(JSON.stringify(o));
    let cfg = clone(info.config ?? info.presets[info.suggested].config);
    let presetKey = info.config ? null : info.suggested;
    let drawing = false;

    const player = el('animated-image', { className: 'pg-anim-player' }, el('img', { src, alt: '' }));
    player.setAttribute('src', info.analysisUrl);
    player.style.aspectRatio = `${info.width} / ${info.height}`;
    const box = el('div', { className: 'pg-anim-box' });
    const stage = el('div', { className: 'pg-anim-stage' }, [player, box]);

    const found = info.found.nodes || info.found.stars
      ? `Found ${info.found.nodes} lights and ${info.found.stars} small points of light in this photo.`
      : 'No lights found in this photo: drift and particles suit it best.';
    const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;

    const presetRow = el('div', { className: 'pg-chips' });
    const effectRow = el('div', { className: 'pg-chips' });
    const particleBox = el('div', { className: 'pg-anim-particles' });
    const slider = (label, min, max, step, get, set, fmt) => {
      const out = el('output');
      const input = el('input', { type: 'range', min, max, step });
      input.addEventListener('input', () => { set(Number(input.value)); presetKey = null; refresh(); });
      return { row: el('label', { className: 'pg-range' }, [el('span', { className: 'pg-label' }, [label, ' ', out]), input]),
        sync: () => { input.value = get(); out.textContent = fmt(get()); } };
    };
    const intensity = slider('Strength', 0, 1, 0.05, () => cfg.intensity, v => { cfg.intensity = v; }, v => `${Math.round(v * 100)}%`);
    const speed = slider('Speed', 0.25, 2, 0.05, () => cfg.speed, v => { cfg.speed = v; }, v => `${v.toFixed(2)}×`);
    const note = el('p', { className: 'pg-anim-note', hidden: true, role: 'status' });

    const has = type => cfg.effects.some(e => e.type === type);
    function toggle(fx) {
      if (has(fx.type)) cfg.effects = cfg.effects.filter(e => e.type !== fx.type);
      else cfg.effects.push(clone(fx.defaults));
      presetKey = null; refresh();
    }

    function drawParticles() {
      const p = cfg.effects.find(e => e.type === 'particles');
      if (!p) { particleBox.replaceChildren(); return; }
      const kind = el('select', { onchange: () => { p.kind = kind.value; refresh(); } },
        info.particleKinds.map(k => el('option', { value: k, textContent: k[0].toUpperCase() + k.slice(1), selected: p.kind === k })));
      const count = el('input', { type: 'range', min: 4, max: 60, step: 1, value: p.count ?? 24, oninput: () => { p.count = Number(count.value); refresh(false); } });
      const b = p.region?.bbox;
      particleBox.replaceChildren(
        el('label', { className: 'pg-range' }, [el('span', { className: 'pg-label', textContent: 'Particles' }), kind]),
        el('label', { className: 'pg-range' }, [el('span', { className: 'pg-label', textContent: 'How many' }), count]),
        el('p', { className: 'pg-help' }, [b ? `Area: ${b[2]} × ${b[3]} pixels, at ${b[0]}, ${b[1]}. ` : 'Area: the middle of the photo. ',
          el('button', { type: 'button', className: 'small', textContent: drawing ? 'Drag on the preview…' : 'Draw the area',
            onclick: () => { drawing = !drawing; stage.classList.toggle('is-drawing', drawing); drawParticles(); } })]),
      );
    }

    // Drag a rectangle on the preview to set where particles appear (image pixels; the preview has the photo's shape).
    let start;
    const toImage = e => { const r = player.getBoundingClientRect(); return [Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)), Math.max(0, Math.min(1, (e.clientY - r.top) / r.height))]; };
    stage.addEventListener('pointerdown', e => { if (!drawing) return; e.preventDefault(); start = toImage(e); stage.setPointerCapture(e.pointerId); });
    stage.addEventListener('pointermove', e => {
      if (!drawing || !start) return;
      const [x, y] = toImage(e);
      Object.assign(box.style, { display: 'block', left: `${Math.min(x, start[0]) * 100}%`, top: `${Math.min(y, start[1]) * 100}%`,
        width: `${Math.abs(x - start[0]) * 100}%`, height: `${Math.abs(y - start[1]) * 100}%` });
    });
    stage.addEventListener('pointerup', e => {
      if (!drawing || !start) return;
      const [x, y] = toImage(e);
      const p = cfg.effects.find(fx => fx.type === 'particles');
      if (p && Math.abs(x - start[0]) > 0.02 && Math.abs(y - start[1]) > 0.02) {
        p.region = { bbox: [Math.round(Math.min(x, start[0]) * info.width), Math.round(Math.min(y, start[1]) * info.height),
          Math.round(Math.abs(x - start[0]) * info.width), Math.round(Math.abs(y - start[1]) * info.height)] };
      }
      start = null; drawing = false; stage.classList.remove('is-drawing'); box.style.display = 'none';
      refresh();
    });

    function refresh(redrawParticles = true) {
      player.setAttribute('config', JSON.stringify(cfg));
      presetRow.replaceChildren(...Object.entries(info.presets).map(([key, p]) => el('button', {
        type: 'button', className: 'pg-chip', textContent: p.label, ariaPressed: String(key === presetKey),
        onclick: () => { cfg = { ...clone(p.config), pauseButton: cfg.pauseButton ?? true }; presetKey = key; refresh(); },
      })));
      effectRow.replaceChildren(...info.effects.map(fx => el('button', { type: 'button', className: 'pg-chip', textContent: fx.label,
        ariaPressed: String(has(fx.type)), onclick: () => toggle(fx) })));
      intensity.sync(); speed.sync();
      if (redrawParticles) drawParticles();
    }

    const controls = [
      el('p', { className: 'pg-help', textContent: found + ' Customers whose phones ask for reduced motion see the still photo.' + (reduce ? ' Your device asks for reduced motion, so the preview stays still.' : '') }),
      el('div', { className: 'pg-anim-group' }, [el('span', { className: 'pg-label', textContent: 'Start from' }), presetRow]),
      el('div', { className: 'pg-anim-group' }, [el('span', { className: 'pg-label', textContent: 'Effects' }), effectRow]),
      particleBox, intensity.row, speed.row,
    ];
    if (info.ai) {
      const prompt = el('textarea', { rows: 3, maxLength: 1000, placeholder: 'For example: let steam rise gently from the cup and drift slowly to the right' });
      const ask = el('button', { type: 'button', className: 'act', textContent: 'Ask Claude' });
      ask.addEventListener('click', async () => {
        if (!prompt.value.trim()) return prompt.focus();
        ask.disabled = true; ask.textContent = 'Thinking…';
        try {
          const image = await AnimAnalyse.jpegBase64(src, 1024);
          const r = await api('/api/admin/animations/suggest', { method: 'POST', body: JSON.stringify({ src, prompt: prompt.value, config: cfg, image }) });
          cfg = r.config; presetKey = null; refresh();
          note.hidden = false;
          note.textContent = `${r.note || 'Here’s a suggestion.'}${r.needsReview ? ' Check where the particles appear: Claude estimated the area. Use “Draw the area” to adjust it.' : ''} Review the preview, then save.`;
          note.classList.toggle('is-warn', !!r.needsReview);
        } catch (e) { toast(e.message, { error: true }); }
        ask.disabled = false; ask.textContent = 'Ask Claude';
      });
      controls.push(el('div', { className: 'pg-anim-group pg-anim-ai' }, [el('span', { className: 'pg-label', textContent: 'Or describe the animation' }), prompt, ask, note]));
    } else {
      controls.push(el('p', { className: 'pg-help', textContent: 'Describing the animation to Claude needs ANTHROPIC_API_KEY set on the server.' }));
    }

    const saveBtn = el('button', { type: 'button', className: 'act', textContent: 'Save animation' });
    const foot = el('div', { className: 'pg-modal-foot' }, [saveBtn,
      el('button', { type: 'button', className: 'small', textContent: 'Reset to preset', onclick: () => {
        const k = presetKey ?? info.suggested; cfg = { ...clone(info.presets[k].config), pauseButton: cfg.pauseButton ?? true }; presetKey = k; note.hidden = true; refresh();
      } })]);
    const body = el('div', {}, [
      el('div', { className: 'pg-anim-body' }, [
        el('div', {}, [stage, el('p', { className: 'pg-help', textContent: 'The animation applies to this photo wherever it appears on the customer pages.' })]),
        el('div', { className: 'pg-anim-controls' }, controls)]),
      foot]);
    const shell = modalShell('Animate this photo', body, 'pg-anim-modal');
    saveBtn.addEventListener('click', async () => {
      try {
        await api('/api/admin/animations', { method: 'PUT', body: JSON.stringify({ src, config: cfg }) });
        shell.close(); toast('Animation saved. Customers see it now.'); refreshPreview();
      } catch (e) { toast(e.message, { error: true }); }
    });
    if (info.saved) {
      foot.append(el('button', { type: 'button', className: 'small danger', textContent: 'Remove animation', onclick: async () => {
        if (!confirm('Show this photo still again, everywhere it appears?')) return;
        try { await api('/api/admin/animations', { method: 'PUT', body: JSON.stringify({ src, config: null }) }); shell.close(); toast('Animation removed.'); refreshPreview(); }
        catch (e) { toast(e.message, { error: true }); }
      } }));
    }
    refresh();
    saveBtn.focus();
  }

  return { open };
})();
