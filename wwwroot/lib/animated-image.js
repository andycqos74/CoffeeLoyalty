// <animated-image>: plays a still image as a subtle, seamless loop on a <canvas> (drift, parallax, twinkling
// lights, sparkles, pulses along lines, particles in a region). Served by the engine at /_engine/animated-image.js.
//
//   <animated-image src="/img/anim/hero.json" config='{"effects":[...]}' position="65% 50%" controls>
//     <img src="/img/hero.jpg" alt="…">           fallback: shown first, for no-JS, reduced motion and the LCP
//   </animated-image>
//
// src is the analysis JSON ({ width, height, layers: [{ src, depth }], lights: { nodes, stars, hub } }); layer
// srcs are resolved against it. config is the effect config (see src/anim/config.js). Every effect repeats a whole
// number of times per loop, t ∈ [0,1), so the loop is seamless and a video export can use the same maths.
// Respects prefers-reduced-motion (stays on the still image), pauses off-screen and in hidden tabs, and caps the
// canvas at 2× device pixels.
(() => {
  if (customElements.get('animated-image')) return;
  const TAU = Math.PI * 2;
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  const css = document.createElement('style');
  css.textContent = 'animated-image{display:block;position:relative;overflow:hidden}'
    + 'animated-image>img{display:block;width:100%;height:100%;object-fit:cover}'
    + 'animated-image>canvas{position:absolute;inset:0;width:100%;height:100%;opacity:0;transition:opacity .9s ease;pointer-events:none}'
    + 'animated-image[data-live]>canvas{opacity:1}'
    + '.ai-pause{position:absolute;right:12px;bottom:12px;z-index:2;width:34px;height:34px;border-radius:50%;border:0;background:rgba(0,0,0,.45);color:#fff;cursor:pointer;display:grid;place-items:center}'
    + '.ai-pause:focus-visible{outline:2px solid #fff;outline-offset:2px}';
  document.head.append(css);

  let seed = 7;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  function sprite(size, stops) {
    const c = document.createElement('canvas'); c.width = c.height = size;
    const g = c.getContext('2d'); const r = size / 2; const gr = g.createRadialGradient(r, r, 0, r, r, r);
    stops.forEach(([o, col]) => gr.addColorStop(o, col)); g.fillStyle = gr; g.fillRect(0, 0, size, size); return c;
  }
  let SP;
  const sprites = () => SP || (SP = {
    glow: sprite(128, [[0, 'rgba(255,255,255,1)'], [0.12, 'rgba(220,245,255,.85)'], [0.35, 'rgba(120,200,255,.25)'], [1, 'rgba(80,160,255,0)']]),
    dot: sprite(32, [[0, 'rgba(255,250,235,1)'], [0.3, 'rgba(255,235,200,.6)'], [1, 'rgba(255,220,180,0)']]),
    ring: sprite(32, [[0, 'rgba(255,255,255,0)'], [0.55, 'rgba(255,255,255,.08)'], [0.8, 'rgba(255,250,230,.9)'], [1, 'rgba(255,250,230,0)']]),
    soft: sprite(32, [[0, 'rgba(255,255,255,.9)'], [1, 'rgba(255,255,255,0)']]),
    ember: sprite(32, [[0, 'rgba(255,240,200,1)'], [0.4, 'rgba(255,160,60,.5)'], [1, 'rgba(255,120,30,0)']]),
  });
  const PARTICLE = { bubbles: ['ring', -1, 0.25], snow: ['soft', 1, 0.6], dust: ['dot', -0.3, 1], embers: ['ember', -1, 0.5] };
  const clamp = (v, a, b) => Math.min(b, Math.max(a, +v || 0));
  const loadImg = (src) => new Promise((ok, no) => { const i = new Image(); i.decoding = 'async'; i.onload = () => ok(i); i.onerror = no; i.src = src; });

  class AnimatedImage extends HTMLElement {
    static get observedAttributes() { return ['config', 'src']; }

    connectedCallback() {
      this.mx = this.my = 0; this.tx = this.ty = null; this.clock = 0; this.visible = false; this.paused = false;
      // Focal point: the position attribute, else the object-position the page's CSS gives this element.
      const pos = (this.getAttribute('position') || getComputedStyle(this).objectPosition || '50% 50%').split(/\s+/)
        .map((s) => (s.endsWith('%') ? clamp(parseFloat(s) / 100, 0, 1) : 0.5));
      this.fx = pos[0] ?? 0.5; this.fy = pos[1] ?? pos[0] ?? 0.5;
      const img = this.querySelector('img');
      if (img) img.style.objectPosition = `${this.fx * 100}% ${this.fy * 100}%`;
      if (reduce.matches || !window.IntersectionObserver) return;
      this.io = new IntersectionObserver(([e]) => { this.visible = e.isIntersecting; if (this.visible) this.load(); this.kick(); }, { rootMargin: '200px' });
      this.io.observe(this);
      this.onVis = () => this.kick();
      this.onMove = (e) => {
        if (e.pointerType === 'touch') return;
        const r = this.getBoundingClientRect();
        const x = (e.clientX - r.left) / r.width; const y = (e.clientY - r.top) / r.height;
        if (x < 0 || x > 1 || y < 0 || y > 1) this.tx = this.ty = null; else { this.tx = (x - 0.5) * 2; this.ty = (y - 0.5) * 2; }
      };
      document.addEventListener('visibilitychange', this.onVis);
      document.addEventListener('pointermove', this.onMove, { passive: true });
    }

    disconnectedCallback() {
      this.io?.disconnect(); this.ro?.disconnect(); cancelAnimationFrame(this.raf); this.raf = 0;
      document.removeEventListener('visibilitychange', this.onVis);
      document.removeEventListener('pointermove', this.onMove);
    }

    attributeChangedCallback(name) {
      if (name === 'config') this.prepare();
      else if (this.loaded) { this.loaded = this.loading = null; this.load(); }
    }

    get config() {
      try { return JSON.parse(this.getAttribute('config') || '{}') || {}; } catch { return {}; }
    }

    async load() {
      if (this.loading) return;
      const url = new URL(this.getAttribute('src') || '', location.href);
      if (url.pathname.endsWith('/')) url.pathname += 'analysis.json';
      this.loading = (async () => {
        const a = await (await fetch(url)).json();
        const layers = await Promise.all((a.layers || []).map(async (l) => ({ depth: +l.depth || 0, img: await loadImg(new URL(l.src, url)) })));
        if (!layers.length) throw new Error('no layers');
        this.analysis = a; this.layers = layers; this.loaded = true;
        if (!this.canvas) {
          this.canvas = document.createElement('canvas'); this.canvas.setAttribute('aria-hidden', 'true');
          this.append(this.canvas); this.ctx = this.canvas.getContext('2d');
          this.ro = new ResizeObserver(() => this.resize()); this.ro.observe(this);
          if (this.hasAttribute('controls')) this.addPause();
        }
        this.prepare(); this.resize(); this.kick();
      })().catch(() => { this.loading = null; });
    }

    addPause() {
      const b = document.createElement('button'); b.type = 'button'; b.className = 'ai-pause';
      const set = () => {
        b.setAttribute('aria-label', this.paused ? 'Play animation' : 'Pause animation');
        b.innerHTML = this.paused ? '<svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true"><path d="M3 1.5v11l9-5.5z" fill="currentColor"/></svg>'
          : '<svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true"><path d="M3 1.5h3v11H3zM8 1.5h3v11H8z" fill="currentColor"/></svg>';
      };
      b.onclick = () => { this.paused = !this.paused; set(); this.kick(); };
      set(); this.append(b);
    }

    resize() {
      if (!this.canvas) return;
      const r = this.getBoundingClientRect(); const d = Math.min(2, devicePixelRatio || 1);
      this.canvas.width = Math.max(1, Math.round(r.width * d)); this.canvas.height = Math.max(1, Math.round(r.height * d));
      if (this.loaded && !this.raf) this.draw(this.clock);
    }

    // Turns the config + analysis into per-effect particle lists (fixed random phases, so the loop never changes).
    prepare() {
      const a = this.analysis; if (!a) return;
      const c = this.config; const L = a.lights || {}; const nodes = L.nodes || []; const stars = L.stars || [];
      const hub = L.hub || (nodes[0] && nodes[0].slice(0, 2));
      seed = 7;
      this.loop = clamp(c.loopSeconds || 8, 4, 12); this.speed = clamp(c.speed ?? 1, 0.25, 2); this.I = clamp(c.intensity ?? 0.6, 0, 1);
      const near = Math.max(0, ...this.layers.map((l) => l.depth));
      this.effects = (c.effects || []).map((e) => {
        const f = { ...e, depth: e.depth ?? (e.layer != null ? this.layers[e.layer]?.depth : null) ?? near };
        if (e.type === 'twinkle') {
          const d = clamp(e.density ?? 0.5, 0, 1);
          f.items = stars.filter((_, i) => rnd() < d).map(([x, y, b]) => ({ x, y, b: (b || 200) / 255, k: 1 + Math.floor(rnd() * 3), ph: rnd() * TAU, s: 0.6 + rnd() * 1.2 }));
        } else if (e.type === 'sparkle') {
          f.items = nodes.map(([x, y, ar]) => ({ x, y, r: Math.min(60, 10 + Math.sqrt(ar) * 1.6), ph: rnd(), k: 1 + Math.floor(rnd() * 2) }));
        } else if (e.type === 'pulses' && hub) {
          f.hub = hub; const min = e.minArea ?? 40; const far = Math.max(a.width, a.height) * 0.08;
          f.items = nodes.slice(1).filter((n) => n[2] > min && Math.hypot(n[0] - hub[0], n[1] - hub[1]) > far).map(([x, y]) => ({ x, y, ph: rnd(), dir: rnd() < 0.5 ? 1 : -1 }));
        } else if (e.type === 'particles') {
          const r = e.region || {}; const b = r.bbox || [0, 0, a.width, a.height];
          f.poly = r.poly || [[b[0], b[1]], [b[0] + b[2], b[1]], [b[0] + b[2], b[1] + b[3]], [b[0], b[1] + b[3]]];
          const xs = f.poly.map((p) => p[0]); const ys = f.poly.map((p) => p[1]);
          f.box = [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
          f.kind = PARTICLE[e.kind] ? e.kind : 'dust';
          const base = Math.max(a.width, a.height) * (e.size ?? 1) * 0.008;
          f.items = Array.from({ length: Math.round(clamp(e.count ?? 20, 1, 200)) }, () => ({ x: rnd(), k: 1 + Math.floor(rnd() * 2), ph: rnd(), w: 1 + Math.floor(rnd() * 3), wp: rnd() * TAU, s: base * (0.5 + rnd()) }));
        }
        return f;
      });
      if (this.loaded && !this.raf) this.draw(this.clock);
    }

    kick() {
      const run = this.loaded && this.visible && !document.hidden && !this.paused;
      if (run && !this.raf) { this.last = performance.now(); this.raf = requestAnimationFrame((n) => this.frame(n)); }
      if (!run && this.raf) { cancelAnimationFrame(this.raf); this.raf = 0; }
    }

    frame(now) {
      const dt = Math.min(0.1, (now - this.last) / 1000); this.last = now;
      this.clock += dt * this.speed;
      const ease = 1 - Math.pow(0.94, dt * 60);
      const t = (this.clock % this.loop) / this.loop;
      const sway = this.effects.find((e) => e.type === 'parallax' && e.autoSway !== false);
      const gx = this.tx ?? (sway ? Math.sin(t * TAU) * 0.35 : 0); const gy = this.ty ?? (sway ? Math.cos(t * TAU) * 0.2 : 0);
      this.mx += (gx - this.mx) * ease; this.my += (gy - this.my) * ease;
      this.draw(this.clock);
      if (!this.dataset.live) this.dataset.live = '';
      this.raf = requestAnimationFrame((n) => this.frame(n));
    }

    draw(clock) {
      const { ctx, canvas: cv, analysis: a, I } = this; if (!ctx || !a) return;
      const W = a.width; const H = a.height; const t = (clock % this.loop) / this.loop; const m = 0.5 + 0.5 * I;
      const drift = this.effects.find((e) => e.type === 'drift'); const par = this.effects.find((e) => e.type === 'parallax');
      const pan = drift ? [Math.sin(t * TAU) * (drift.panX ?? 14) * m, Math.cos(t * TAU) * (drift.panY ?? 8) * m] : [0, 0];
      const dz = drift ? (drift.zoom ?? 0.025) * m : 0; const ps = par ? clamp(par.strength ?? 1, 0, 2) : 0;
      const pmx = par && par.pointer !== false ? this.mx : 0; const pmy = par && par.pointer !== false ? this.my : 0;
      // Enough extra zoom that drift and parallax never show an edge.
      const zoom = 1.01 + dz + 2 * (Math.abs(drift?.panX ?? 0) / W + 0.02 * ps) + dz * Math.sin(t * TAU);
      const s = Math.max(cv.width / W, cv.height / H) * zoom;
      const ox = (cv.width - W * s) * this.fx; const oy = (cv.height - H * s) * this.fy;
      const at = (depth) => ctx.setTransform(s, 0, 0, s, ox + (pan[0] + pmx * depth * ps * 0.02 * W) * s, oy + (pan[1] + pmy * depth * ps * 0.012 * W) * s);
      ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.globalAlpha = 1; ctx.globalCompositeOperation = 'source-over';
      ctx.clearRect(0, 0, cv.width, cv.height);
      for (const l of this.layers) { at(l.depth); ctx.drawImage(l.img, 0, 0, W, H); }
      const S = sprites(); ctx.globalCompositeOperation = 'lighter';
      for (const e of this.effects) {
        if (!e.items) continue;
        at(e.depth);
        if (e.type === 'twinkle') {
          for (const p of e.items) {
            const al = Math.pow(Math.max(0, Math.sin(t * TAU * p.k + p.ph)), 6) * I * p.b; if (al < 0.02) continue;
            const z = 14 * p.s; ctx.globalAlpha = al; ctx.drawImage(S.dot, p.x - z / 2, p.y - z / 2, z, z);
          }
        } else if (e.type === 'sparkle') {
          for (const n of e.items) {
            const w = 0.5 + 0.5 * Math.sin((t * n.k + n.ph) * TAU); const R = n.r * (2.2 + 1.4 * w);
            ctx.globalAlpha = (0.25 + 0.55 * w) * I; ctx.drawImage(S.glow, n.x - R, n.y - R, R * 2, R * 2);
            const fl = Math.pow(w, 8) * I;
            if (e.flare !== false && fl > 0.03) { ctx.globalAlpha = fl; const Ln = n.r * 5; ctx.drawImage(S.glow, n.x - Ln, n.y - 3, Ln * 2, 6); ctx.drawImage(S.glow, n.x - 3, n.y - Ln, 6, Ln * 2); }
          }
        } else if (e.type === 'pulses') {
          const [hx, hy] = e.hub; ctx.lineCap = 'round'; ctx.lineWidth = 3;
          for (const f of e.items) {
            let p = (t * 2 + f.ph) % 1; if (f.dir < 0) p = 1 - p;
            const x = hx + (f.x - hx) * p; const y = hy + (f.y - hy) * p; const fade = Math.sin(p * Math.PI);
            ctx.globalAlpha = 0.9 * I * fade; ctx.drawImage(S.glow, x - 22, y - 22, 44, 44);
            const bx = hx + (f.x - hx) * (p - 0.06 * f.dir); const by = hy + (f.y - hy) * (p - 0.06 * f.dir);
            const g = ctx.createLinearGradient(bx, by, x, y); g.addColorStop(0, 'rgba(127,212,255,0)'); g.addColorStop(1, 'rgba(190,235,255,.9)');
            ctx.strokeStyle = g; ctx.globalAlpha = 0.7 * I * fade; ctx.beginPath(); ctx.moveTo(bx, by); ctx.lineTo(x, y); ctx.stroke();
          }
        } else if (e.type === 'particles') {
          const [x0, y0, x1, y1] = e.box; const [sp, dir, alpha] = PARTICLE[e.kind];
          ctx.save(); ctx.beginPath(); e.poly.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y))); ctx.clip();
          for (const p of e.items) {
            const q = (t * p.k + p.ph) % 1; const travel = dir < 0 ? 1 - q : q;
            const y = y0 + (y1 - y0) * (Math.abs(dir) < 1 ? 0.5 + (travel - 0.5) * Math.abs(dir) * 2 : travel);
            const x = x0 + (x1 - x0) * p.x + Math.sin((t * p.w) * TAU + p.wp) * p.s * 1.5;
            ctx.globalAlpha = Math.sin(q * Math.PI) * (0.35 + 0.65 * I) * (alpha + 0.4);
            ctx.drawImage(S[sp], x - p.s, y - p.s, p.s * 2, p.s * 2);
          }
          ctx.restore();
        }
      }
      ctx.globalAlpha = 1; ctx.globalCompositeOperation = 'source-over';
    }
  }
  customElements.define('animated-image', AnimatedImage);
})();
