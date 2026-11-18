// Finds the points of light in a photo so the <animated-image> player can make them twinkle, sparkle and send
// pulses between them. A browser port of the Gleam site engine's src/anim/analyse.js (same thresholds and
// output), run in the admin's browser so the server needs no image library.
//
//   nodes: bright blobs. Blur, threshold, connected components, then drop any blob near a bigger one. The biggest
//          is the "hub" (where pulses start).
//   stars: small points: local maxima over a 7x7 window that stand out from their surroundings, at most 700.
//
// AnimAnalyse.detectLights(url) -> { width, height, brightness, lights: { nodes, stars, hub } }
// AnimAnalyse.jpegBase64(url, maxSide) -> the photo as a JPEG (base64, no prefix), for the Claude request.
window.AnimAnalyse = (() => {
  const MAX_SIDE = 2048, MAX_STARS = 700, MAX_NODES = 80;

  function loadImage(url) {
    return new Promise((ok, no) => {
      const img = new Image();
      img.decoding = 'async';
      img.onload = () => ok(img);
      img.onerror = () => no(new Error('The photo could not be loaded.'));
      img.src = url;
    });
  }

  function draw(img, maxSide, background) {
    const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
    const w = Math.max(1, Math.round(img.naturalWidth * scale)), h = Math.max(1, Math.round(img.naturalHeight * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w; canvas.height = h;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    ctx.fillStyle = background; ctx.fillRect(0, 0, w, h);
    ctx.drawImage(img, 0, 0, w, h);
    return { canvas, ctx, w, h };
  }

  // Deterministic random numbers, so the same photo always gives the same analysis.
  function seeded(seed) {
    let s = seed % 2147483647 || 1;
    return () => (s = (s * 16807) % 2147483647) / 2147483647;
  }

  function percentile(gray, p) {
    const hist = new Uint32Array(256);
    for (let i = 0; i < gray.length; i++) hist[gray[i]]++;
    const target = gray.length * p;
    let n = 0;
    for (let v = 0; v < 256; v++) { n += hist[v]; if (n >= target) return v; }
    return 255;
  }

  // Gaussian blur approximated by three box blurs (separable running sums).
  function boxesForGauss(sigma, n) {
    const wIdeal = Math.sqrt((12 * sigma * sigma / n) + 1);
    let wl = Math.floor(wIdeal); if (wl % 2 === 0) wl--;
    const wu = wl + 2;
    const m = Math.round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4));
    return Array.from({ length: n }, (_, i) => ((i < m ? wl : wu) - 1) / 2);
  }
  function boxPass(src, dst, w, h, r, horizontal) {
    const len = horizontal ? w : h, lines = horizontal ? h : w;
    const step = horizontal ? 1 : w, lineStep = horizontal ? w : 1;
    const span = r + r + 1;
    for (let l = 0; l < lines; l++) {
      const base = l * lineStep;
      let acc = src[base] * (r + 1);
      for (let i = 0; i < r; i++) acc += src[base + Math.min(i, len - 1) * step];
      for (let i = 0; i < len; i++) {
        acc += src[base + Math.min(i + r, len - 1) * step] - src[base + Math.max(i - r - 1, 0) * step];
        dst[base + i * step] = acc / span;
      }
    }
  }
  function blur(gray, w, h, sigma) {
    let a = Float32Array.from(gray), b = new Float32Array(gray.length);
    for (const r of boxesForGauss(sigma, 3)) {
      const rr = Math.max(0, Math.round(r));
      boxPass(a, b, w, h, rr, true); boxPass(b, a, w, h, rr, false);
    }
    const out = new Uint8Array(gray.length);
    for (let i = 0; i < out.length; i++) out[i] = Math.max(0, Math.min(255, Math.round(a[i])));
    return out;
  }

  // Separable maximum filter (radius r): the "dilate then compare" trick for local maxima.
  function maxFilter(src, w, h, r) {
    const tmp = new Uint8Array(src.length), out = new Uint8Array(src.length);
    for (let y = 0; y < h; y++) {
      const row = y * w;
      for (let x = 0; x < w; x++) {
        let m = 0;
        for (let i = Math.max(0, x - r), e = Math.min(w - 1, x + r); i <= e; i++) if (src[row + i] > m) m = src[row + i];
        tmp[row + x] = m;
      }
    }
    for (let x = 0; x < w; x++) {
      for (let y = 0; y < h; y++) {
        let m = 0;
        for (let i = Math.max(0, y - r), e = Math.min(h - 1, y + r); i <= e; i++) if (tmp[i * w + x] > m) m = tmp[i * w + x];
        out[y * w + x] = m;
      }
    }
    return out;
  }

  // 8-connected components of pixels at or above thr: [{ x, y, area }] with x, y the centroid.
  function blobs(gray, w, h, thr, minArea) {
    const seen = new Uint8Array(gray.length), stack = new Int32Array(gray.length), found = [];
    for (let start = 0; start < gray.length; start++) {
      if (seen[start] || gray[start] < thr) continue;
      let top = 0, area = 0, sx = 0, sy = 0;
      stack[top++] = start; seen[start] = 1;
      while (top) {
        const p = stack[--top], px = p % w, py = (p - px) / w;
        area++; sx += px; sy += py;
        for (let dy = -1; dy <= 1; dy++) {
          const ny = py + dy;
          if (ny < 0 || ny >= h) continue;
          for (let dx = -1; dx <= 1; dx++) {
            const nx = px + dx;
            if (nx < 0 || nx >= w) continue;
            const q = ny * w + nx;
            if (!seen[q] && gray[q] >= thr) { seen[q] = 1; stack[top++] = q; }
          }
        }
      }
      if (area > minArea) found.push({ x: Math.round(sx / area), y: Math.round(sy / area), area });
    }
    return found;
  }

  async function detectLights(url) {
    const img = await loadImage(url);
    const { ctx, w, h } = draw(img, MAX_SIDE, '#000');
    const rgba = ctx.getImageData(0, 0, w, h).data;
    const gray = new Uint8Array(w * h);
    for (let i = 0, j = 0; i < gray.length; i++, j += 4) gray[i] = Math.round(0.2126 * rgba[j] + 0.7152 * rgba[j + 1] + 0.0722 * rgba[j + 2]);
    const soft = blur(gray, w, h, 1.7), local = blur(gray, w, h, 12);
    const scale = Math.max(w, h) / 1920; // the prototype's distances were tuned at 1920 px

    let sum = 0;
    for (let i = 0; i < gray.length; i += 7) sum += gray[i];
    const brightness = sum / Math.ceil(gray.length / 7) / 255;

    // Nodes: the brightest 0.5 % of the blurred image, but never below 175 (the prototype's dark-image setting).
    const nodeThr = Math.min(250, Math.max(175, percentile(soft, 0.995)));
    const minArea = Math.max(4, Math.round(12 * scale * scale));
    const merge = 40 * scale, maxArea = w * h * 0.004; // anything bigger is a bright region (sky, a window), not a light
    const kept = [];
    for (const b of blobs(soft, w, h, nodeThr, minArea).filter(b => b.area <= maxArea).sort((a, c) => c.area - a.area)) {
      if (kept.some(k => Math.hypot(k.x - b.x, k.y - b.y) < merge)) continue;
      kept.push(b);
      if (kept.length >= MAX_NODES) break;
    }
    const nodes = kept.map(b => [b.x, b.y, b.area]);

    // Stars: local maxima above the 97th percentile (at least 150) that are clearly brighter than their surroundings.
    const starThr = Math.min(245, Math.max(150, percentile(gray, 0.97)));
    const contrast = brightness > 0.35 ? 60 : 30;
    const peaks = maxFilter(gray, w, h, 3);
    let stars = [];
    for (let y = 3; y < h - 3; y++) {
      for (let x = 3, i = y * w + 3; x < w - 3; x++, i++) {
        const g = gray[i];
        if (g >= starThr && g === peaks[i] && g - local[i] >= contrast) stars.push([x, y, g]);
      }
    }
    if (stars.length > MAX_STARS) {
      const rnd = seeded(w * 31 + h);
      for (let i = stars.length - 1; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [stars[i], stars[j]] = [stars[j], stars[i]]; }
      stars = stars.slice(0, MAX_STARS).sort((a, b) => a[1] - b[1] || a[0] - b[0]);
    }
    return { width: w, height: h, brightness: Math.round(brightness * 1000) / 1000, lights: { nodes, stars, hub: nodes.length ? [nodes[0][0], nodes[0][1]] : null } };
  }

  async function jpegBase64(url, maxSide = 1024) {
    const { canvas } = draw(await loadImage(url), maxSide, '#fff');
    return canvas.toDataURL('image/jpeg', 0.8).split(',')[1];
  }

  return { detectLights, jpegBase64, loadImage };
})();
