// Client survey: consent -> live photos -> AI check -> review & sign -> done.
(() => {
  'use strict';
  const token = decodeURIComponent(location.pathname.split('/').filter(Boolean).pop() || '');
  const $ = s => document.querySelector(s);
  const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const app = $('#app'), dock = $('#dock'), dockIn = $('#dockIn');

  const stored = () => { try { return localStorage.getItem('ms-lang'); } catch { return null; } };
  let lang = stored() || ((navigator.language || '').toLowerCase().startsWith('ar') ? 'ar' : 'en');
  let D = null;                 // survey data from the server
  let loc = null, locDenied = false, watchId = null;
  const thumbs = {}, upState = {};   // slot -> objectURL / 'up' | 'ok' | 'fail'
  let pollTimer = null;

  const L = () => window.I18N[lang];
  // values (claim no., plate, names) are wrapped in Unicode isolates so they read correctly inside Arabic text
  const t = (k, vars) => String(L()[k] ?? k).replace(/\{(\w+)\}/g, (_, n) => '\u2068' + (vars?.[n] ?? '') + '\u2069');
  const slotName = s => L().slots[s.key] || s.label;

  function applyLang() {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr';
    $('#lang').textContent = L().langBtn;
    document.querySelectorAll('[data-t=brand]').forEach(e => e.firstChild.nodeValue = L().brand);
    document.querySelector('[data-t=brandSub]').textContent = L().brandSub;
    document.title = `${L().brandSub} · ${L().brand}`;
  }
  $('#lang').onclick = () => { lang = lang === 'ar' ? 'en' : 'ar'; try { localStorage.setItem('ms-lang', lang); } catch { } applyLang(); render(); };

  // ---------- API ----------
  async function api(path, opt = {}) {
    let r;
    try { r = await fetch(`/api/survey/${encodeURIComponent(token)}${path}`, opt); }
    catch { throw Object.assign(new Error('network'), { code: 'network' }); }
    const j = await r.json().catch(() => ({}));
    if (!r.ok) throw Object.assign(new Error(j.error || r.status), { code: j.error || 'network', status: r.status });
    return j;
  }
  const post = (p, body) => api(p, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });

  // ---------- screens ----------
  function steps(i) { return `<ol class="steps">${L().steps.map((s, j) => `<li class="${j < i ? 'done' : j === i ? 'on' : ''}">${j + 1}. ${esc(s)}</li>`).join('')}</ol>`; }
  function setDock(html) { if (!html) { dock.classList.add('hide'); dockIn.innerHTML = ''; return; } dock.classList.remove('hide'); dockIn.innerHTML = html; }

  function render() {
    clearTimeout(pollTimer);
    if (!D) return;
    if (D.error) return renderError(D.error);
    if (D.done) return renderDone();
    if (!D.consent) return renderConsent();
    if (D.status === 'Analysing') return renderAnalysing();
    if (D.status === 'ReadyToSign') return renderReview();
    return renderCapture();
  }

  function renderError(code) {
    setDock('');
    const [h, p] = L().errors[code] || L().errors.network;
    app.innerHTML = `<div class="card" style="margin-top:24px"><h1>${esc(h)}</h1><p>${esc(p)}</p>${code === 'network' ? `<button class="p" id="rt">${esc(t('retry'))}</button>` : ''}</div>`;
    $('#rt')?.addEventListener('click', load);
  }

  function renderConsent() {
    const v = D.vehicle;
    app.innerHTML = `${steps(0)}<div class="grid2"><section>
      <p class="mut">${esc(t('hello', { name: D.customerName }))}</p>
      <h1>${esc(t('title', { claim: D.claimNo }))}</h1>
      <p>${esc(t('intro', { car: `${v.make} ${v.model}`, plate: v.plateNo }))}</p>
      <label class="check"><input type="checkbox" id="agree"><span>${esc(t('agree'))}</span></label>
      <p class="err" id="cErr" role="alert"></p></section>
      <section class="card"><h3>${esc(t('needTitle'))}</h3><p class="mut">${esc(t('need'))}</p></section></div>`;
    setDock(`<button class="p w100" id="go" disabled>${esc(t('start'))}</button>`);
    $('#agree').onchange = e => $('#go').disabled = !e.target.checked;
    $('#go').onclick = startConsent;
  }

  async function startConsent() {
    const go = $('#go'); go.disabled = true; go.textContent = t('locating');
    await getLocation();
    if (!loc) {
      $('#cErr').textContent = t('gpsOff');
      setDock(`<div class="row" style="justify-content:stretch"><button class="p" style="flex:1" id="go">${esc(t('retry'))}</button><button id="noGps" style="flex:1">${esc(t('continueNoGps'))}</button></div>`);
      $('#go').onclick = startConsent; $('#noGps').onclick = () => sendConsent();
      return;
    }
    await sendConsent();
  }
  async function sendConsent() {
    try { D = await post('/consent', { agree: true, lat: loc?.lat, lng: loc?.lng, acc: loc?.acc, language: lang }); startWatch(); render(); }
    catch (e) { fail(e); }
  }

  function getLocation() {
    return new Promise(res => {
      if (!navigator.geolocation) return res();
      navigator.geolocation.getCurrentPosition(p => { setLoc(p); res(); }, () => { locDenied = true; res(); }, { enableHighAccuracy: true, timeout: 10000, maximumAge: 30000 });
    });
  }
  function setLoc(p) { loc = { lat: +p.coords.latitude.toFixed(6), lng: +p.coords.longitude.toFixed(6), acc: Math.round(p.coords.accuracy) }; }
  function startWatch() { if (navigator.geolocation && watchId == null) watchId = navigator.geolocation.watchPosition(setLoc, () => { }, { enableHighAccuracy: true, maximumAge: 15000 }); }

  // ---------- capture ----------
  const has = k => D.photos.some(p => p.slot === k);
  function carSvg() {
    const pos = { front: [100, 16], fr: [176, 56], right: [190, 200], rr: [176, 344], rear: [100, 384], rl: [24, 344], left: [10, 200], fl: [24, 56] };
    const dots = D.slots.filter(s => s.required).map((s, i) => {
      const [x, y] = pos[s.key] || [0, 0], on = has(s.key);
      return `<g class="pdot" data-slot="${s.key}" role="button" tabindex="0" aria-label="${esc((i + 1) + '. ' + slotName(s))}" style="cursor:pointer"><circle cx="${x}" cy="${y}" r="22" fill="transparent"/><circle cx="${x}" cy="${y}" r="14" fill="${on ? '#ffe600' : 'var(--bg)'}" stroke="currentColor" stroke-width="2"/><text x="${x}" y="${y + 5}" text-anchor="middle" font-size="13" font-weight="700" fill="${on ? '#111' : 'currentColor'}" pointer-events="none">${i + 1}</text></g>`;
    }).join('');
    return `<svg viewBox="-8 0 216 400" role="img" aria-label="${esc(t('count', { n: D.slots.filter(s => s.required && has(s.key)).length }))}" dir="ltr" style="color:var(--ink)">
      <rect x="48" y="44" width="104" height="312" rx="44" fill="var(--con2)" stroke="currentColor" stroke-width="2"/>
      <path d="M62 120 Q100 98 138 120 L132 160 Q100 150 68 160Z" fill="var(--bg)" stroke="currentColor" stroke-width="1.5"/>
      <path d="M68 280 Q100 290 132 280 L138 310 Q100 326 62 310Z" fill="var(--bg)" stroke="currentColor" stroke-width="1.5"/>
      ${[[40, 100], [148, 100], [40, 290], [148, 290]].map(([x, y]) => `<rect x="${x}" y="${y}" width="12" height="40" rx="4" fill="currentColor"/>`).join('')}
      <text x="100" y="68" text-anchor="middle" font-size="10" fill="currentColor" opacity=".7">${esc(L().front)}</text>${dots}</svg>`;
  }

  function renderCapture() {
    const req = D.slots.filter(s => s.required);
    app.innerHTML = `${steps(1)}<div class="grid2"><section><h2>${esc(t('takeTitle'))}</h2><p class="mut">${esc(t('takeHint'))}</p>
      <div class="plan" id="plan">${carSvg()}</div>${locDenied && !loc ? `<p class="err">${esc(t('gpsOff'))}</p>` : ''}</section>
      <section class="slots" id="slots">${D.slots.map((s, i) => slotHtml(s, i)).join('')}</section></div>`;
    app.querySelectorAll('#slots [data-slot]').forEach(b => b.onclick = () => chooseSource(D.slots.find(s => s.key === b.dataset.slot)));
    bindPlan();
    D.photos.forEach(p => { if (!thumbs[p.slot]) loadThumb(p.slot); });
    updateDock();
    loadDetector();   // start loading the car check in the background
  }
  function bindPlan() {
    document.querySelectorAll('#plan .pdot').forEach(g => {
      const go = () => { if (upState[g.dataset.slot] !== 'up') chooseSource(D.slots.find(s => s.key === g.dataset.slot)); };
      g.addEventListener('click', go);
      g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
    });
  }
  function slotHtml(s, i) {
    const st = upState[s.key], done = has(s.key);
    const status = st === 'up' ? t('uploading') : st === 'fail' ? t('failed') : done ? '✓ ' + t('uploaded') : t('notTaken');
    return `<div class="slot ${done ? 'done' : ''} ${st === 'fail' ? 'bad' : ''}" id="sl-${s.key}">
      ${thumbs[s.key] ? `<img class="th" src="${thumbs[s.key]}" alt="">` : '<div class="th"></div>'}
      <div><b>${s.required ? (i + 1) + '. ' : ''}${esc(slotName(s))}</b>${s.required ? ` <span class="req">${esc(t('required'))}</span>` : ''}<div class="st">${esc(status)}</div></div>
      <button class="sm ${done ? '' : 'p'}" data-slot="${s.key}" ${st === 'up' ? 'disabled' : ''}>${esc(done ? t('retake') : t('take'))}</button></div>`;
  }
  function refreshSlot(key) {
    const s = D.slots.find(x => x.key === key), i = D.slots.indexOf(s);
    const el = $('#sl-' + key); if (!el) return;
    el.outerHTML = slotHtml(s, i);
    $(`#slots [data-slot="${key}"]`).onclick = () => chooseSource(s);
    $('#plan').innerHTML = carSvg();
    bindPlan();
    updateDock();
  }
  function updateDock() {
    const n = D.slots.filter(s => s.required && has(s.key)).length, busy = Object.values(upState).includes('up');
    setDock(`<div class="row" style="justify-content:space-between"><span>${esc(t('count', { n }))}</span></div><div class="bar"><i style="width:${n / 8 * 100}%"></i></div>
      <button class="p w100" id="runAi" ${n < 8 || busy ? 'disabled' : ''}>${esc(n < 8 ? t('needMore', { n: 8 - n }) : t('runAi'))}</button>`);
    $('#runAi').onclick = async () => { $('#runAi').disabled = true; try { D = await post('/analyze'); render(); } catch (e) { fail(e); } };
  }
  async function loadThumb(slot) {
    try { const r = await fetch(`/api/survey/${encodeURIComponent(token)}/photos/${slot}`); if (r.ok) { thumbs[slot] = URL.createObjectURL(await r.blob()); refreshSlot(slot); } } catch { }
  }

  // ---------- car check (TensorFlow.js COCO-SSD, runs on the phone; never blocks if it can't load) ----------
  const EXTERIOR = ['front', 'fr', 'right', 'rr', 'rear', 'rl', 'left', 'fl'];
  const tries = {};
  let detectorP = null;
  const loadScript = src => new Promise((ok, no) => { const s = document.createElement('script'); s.src = src; s.onload = ok; s.onerror = no; document.head.appendChild(s); });
  function loadDetector() {
    if (detectorP) return detectorP;
    detectorP = (async () => {
      try {
        if (!window.tf) await loadScript('/lib/tf.min.js');
        if (!window.cocoSsd) await loadScript('/lib/coco-ssd.min.js');
        // 1) weights hosted on our own server (downloaded at build), 2) Google's hosted copy
        for (const cfg of [{ base: 'lite_mobilenet_v2', modelUrl: '/lib/coco-ssd-lite/model.json' }, { base: 'lite_mobilenet_v2' }]) {
          try { return await window.cocoSsd.load(cfg); } catch { }
        }
      } catch { }
      return null;
    })();
    return detectorP;
  }
  async function carCheck(slot, img) {
    if (!EXTERIOR.includes(slot.key)) return { ok: true, skipped: true };
    const det = await Promise.race([loadDetector(), new Promise(r => setTimeout(() => r(null), 12000))]);
    if (!det) return { ok: true, skipped: true };
    const max = 640, sc = Math.min(1, max / Math.max(img.width, img.height));
    const c = document.createElement('canvas'); c.width = Math.round(img.width * sc); c.height = Math.round(img.height * sc);
    c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
    try {
      const preds = await det.detect(c, 10, 0.3);
      const best = preds.filter(p => ['car', 'truck', 'bus'].includes(p.class))
        .map(p => ({ score: p.score, area: (p.bbox[2] * p.bbox[3]) / (c.width * c.height) }))
        .sort((x, y) => y.score * y.area - x.score * x.area)[0];
      return { ok: !!best && best.score >= 0.5 && best.area >= 0.12, best };
    } catch { return { ok: true, skipped: true }; }
  }

  // ---------- choose: live camera or gallery ----------
  function chooseSource(slot) {
    if (!D.allowGallery) return openCamera(slot);
    const m = document.createElement('div'); m.className = 'modal sheet'; m.setAttribute('role', 'dialog');
    m.innerHTML = `<div class="card stack"><h3 style="margin:0">${esc(t('srcTitle', { slot: slotName(slot) }))}</h3>
      <button class="p w100" id="srcCam">📷 ${esc(t('srcCamera'))}</button>
      <button class="w100" id="srcGal">🖼 ${esc(t('srcGallery'))}</button>
      <button class="ghost w100" id="srcX">${esc(t('cancel'))}</button></div>`;
    document.body.appendChild(m);
    const close = () => m.remove();
    m.onclick = e => { if (e.target === m) close(); };
    m.querySelector('#srcX').onclick = close;
    m.querySelector('#srcCam').onclick = () => { close(); openCamera(slot); };
    m.querySelector('#srcGal').onclick = () => { close(); pickFile(slot, '#fileGal'); };
    m.querySelector('#srcCam').focus();
  }
  function pickFile(slot, sel) {
    const inp = $(sel); inp.value = '';
    inp.onchange = async () => {
      const f = inp.files[0]; if (!f) return;
      try { review(slot, await loadImage(f), false); } catch { alertBox(t('camErr')); }
    };
    inp.click();
  }

  // ---------- live camera ----------
  let stream = null;
  async function openCamera(slot) {
    const live = window.isSecureContext && navigator.mediaDevices?.getUserMedia;
    if (!live) {
      if (!D.allowFileFallback && !D.allowGallery) return alertBox(t('httpsNeeded'));
      return pickFile(slot, '#fileCam');
    }
    const ov = document.createElement('div');
    ov.className = 'cam'; ov.setAttribute('role', 'dialog'); ov.setAttribute('aria-label', slotName(slot));
    ov.innerHTML = `<video playsinline autoplay muted></video><div class="frame"></div>
      <div class="ctop"><b>${esc(slotName(slot))}</b><button class="sm" id="camX">${esc(t('camClose'))}</button></div>
      <div class="cbot"><span class="qmsg">${esc(t('camHint'))}</span><button class="shutter" id="shot" aria-label="${esc(t('shutter'))}"></button></div>`;
    document.body.appendChild(ov);
    const close = () => { stopStream(); ov.remove(); };
    ov.querySelector('#camX').onclick = close;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1440 } } });
      const v = ov.querySelector('video'); v.srcObject = stream; await v.play().catch(() => { });
      ov.querySelector('#shot').onclick = () => {
        if (!v.videoWidth) return;
        const c = document.createElement('canvas'); c.width = v.videoWidth; c.height = v.videoHeight;
        c.getContext('2d').drawImage(v, 0, 0);
        close(); review(slot, c, true);
      };
    } catch {
      close();
      if (D.allowFileFallback || D.allowGallery) pickFile(slot, '#fileCam');
      else alertBox(t('camErr'));
    }
  }
  function stopStream() { stream?.getTracks().forEach(tr => tr.stop()); stream = null; }
  async function loadImage(file) {
    try { return await createImageBitmap(file, { imageOrientation: 'from-image' }); }
    catch { return await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = URL.createObjectURL(file); }); }
  }

  function quality(img) {
    const w = 200, h = Math.max(2, Math.round(200 * img.height / img.width));
    const c = document.createElement('canvas'); c.width = w; c.height = h;
    const x = c.getContext('2d', { willReadFrequently: true }); x.drawImage(img, 0, 0, w, h);
    const d = x.getImageData(0, 0, w, h).data, g = new Float32Array(w * h); let sum = 0;
    for (let i = 0; i < w * h; i++) { g[i] = .299 * d[i * 4] + .587 * d[i * 4 + 1] + .114 * d[i * 4 + 2]; sum += g[i]; }
    const mean = sum / (w * h); let m = 0, m2 = 0, n = 0;
    for (let y = 1; y < h - 1; y++) for (let xx = 1; xx < w - 1; xx++) { const i = y * w + xx, l = 4 * g[i] - g[i - 1] - g[i + 1] - g[i - w] - g[i + w]; m += l; m2 += l * l; n++; }
    const sharp = n ? m2 / n - (m / n) ** 2 : 0, issues = [];
    if (img.width < 640 || img.height < 480) issues.push('low resolution');
    if (mean < 45) issues.push('too dark'); if (mean > 220) issues.push('too bright'); if (sharp < 35) issues.push('blurry');
    return issues;
  }
  function toJpeg(img, max = 1600, q = .85) {
    const s = Math.min(1, max / Math.max(img.width, img.height));
    const c = document.createElement('canvas'); c.width = Math.round(img.width * s); c.height = Math.round(img.height * s);
    c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
    return new Promise(r => c.toBlob(r, 'image/jpeg', q));
  }

  async function review(slot, img, live) {
    const issues = quality(img), blob = await toJpeg(img), url = URL.createObjectURL(blob);
    const ov = document.createElement('div'); ov.className = 'cam'; ov.setAttribute('role', 'dialog');
    ov.innerHTML = `<img class="pv" src="${url}" alt=""><div class="ctop"><b>${esc(slotName(slot))}</b></div>
      <div class="cbot"><span class="qmsg" role="status">${esc(t('checking'))}</span></div>`;
    document.body.appendChild(ov);
    const car = await carCheck(slot, img);
    if (!car.ok) issues.push('no car detected');
    if (issues.length) tries[slot.key] = (tries[slot.key] || 0) + 1;
    const noCar = !car.ok, canOverride = !issues.length || tries[slot.key] >= 2;
    const qtxt = noCar ? t('noCar') : issues.length ? t('qMsg', { issues: issues.map(i => L().q[i] || i).join(lang === 'ar' ? '، ' : ', ') }) : '';
    ov.querySelector('.cbot').innerHTML = `${qtxt ? `<span class="qmsg" role="alert">${esc(qtxt)}</span>` : ''}
      <button ${issues.length ? 'class="y"' : ''} id="rt">${esc(t('retakeBtn'))}</button>
      ${canOverride ? `<button ${issues.length ? '' : 'class="y"'} id="ok">${esc(issues.length ? t('useReview') : t('usePhoto'))}</button>` : ''}`;
    ov.querySelector('#rt').onclick = () => { ov.remove(); URL.revokeObjectURL(url); live ? openCamera(slot) : chooseSource(slot); };
    ov.querySelector('#ok')?.addEventListener('click', () => { ov.remove(); tries[slot.key] = 0; upload(slot, blob, url, live, issues); });
  }

  async function upload(slot, blob, url, live, issues) {
    if (thumbs[slot.key]) URL.revokeObjectURL(thumbs[slot.key]);
    thumbs[slot.key] = url; upState[slot.key] = 'up'; refreshSlot(slot.key);
    const fd = new FormData();
    fd.append('slot', slot.key); fd.append('photo', blob, slot.key + '.jpg');
    fd.append('capturedAt', new Date().toISOString()); fd.append('live', String(live));
    if (loc) { fd.append('lat', loc.lat); fd.append('lng', loc.lng); fd.append('acc', loc.acc); }
    if (issues.length) { fd.append('quality', issues.join(', ')); fd.append('qualityOverride', 'true'); }
    try {
      const p = await api('/photos', { method: 'POST', body: fd });
      D.photos = D.photos.filter(x => x.slot !== p.slot).concat(p);
      upState[slot.key] = 'ok';
    } catch (e) {
      if (e.status === 410 || e.status === 404) return fail(e);
      upState[slot.key] = 'fail';
    }
    refreshSlot(slot.key);
  }

  // ---------- analysing ----------
  function renderAnalysing() {
    setDock('');
    app.innerHTML = `${steps(2)}<div class="card" style="text-align:center;padding:40px 20px"><div class="spin" style="margin:0 auto 18px"></div>
      <h2>${esc(t('analysing'))}</h2><p class="mut">${esc(t('analysingHint'))}</p></div>`;
    const poll = async () => { try { D = await api(''); } catch (e) { if (e.status) return fail(e); } if (D.status === 'Analysing') pollTimer = setTimeout(poll, 2500); else render(); };
    pollTimer = setTimeout(poll, 2000);
  }

  // ---------- review & sign ----------
  function renderReview() {
    const v = D.vehicle, F = L().fields, cols = L().cols;
    const rows = [[F.make, v.make], [F.model, v.model], [F.year, v.year], [F.colour, v.colour], [F.plate, v.plateNo], [F.vin, v.vin], [F.odometer, v.odometer || '—']];
    const findings = D.findings.length
      ? `<table class="rs"><thead><tr>${cols.map(c => `<th>${esc(c)}</th>`).join('')}</tr></thead><tbody>${D.findings.map(f => `<tr>
          <td data-l="${esc(cols[0])}">${esc(L().types[f.type] || f.type)}</td><td data-l="${esc(cols[1])}">${esc(f.part)}</td>
          <td data-l="${esc(cols[2])}" class="sev-${esc(f.severity)}">${esc(L().sev[f.severity] || f.severity)}</td><td data-l="${esc(cols[3])}">${Math.round(f.confidence * 100)}%</td></tr>`).join('')}</tbody></table>`
      : `<p><b>${esc(D.aiStatus === 'ok' ? t('noDamage') : t('manual'))}</b></p>`;
    app.innerHTML = `${steps(3)}<h1>${esc(t('review'))}</h1><div class="grid2"><section class="stack">
      <div class="card"><h3>${esc(t('vehicle'))}</h3><div class="kv">${rows.map(([k, x]) => `<div><span>${esc(k)}</span><b>${esc(x)}</b></div>`).join('')}</div></div>
      <div class="card"><h3>${esc(t('findings'))}</h3>${findings}</div>
      <div class="card"><h3>${esc(t('photos'))}</h3><div class="imgs">${D.photos.map(p => { const s = D.slots.find(x => x.key === p.slot); return `<figure><img src="${thumbs[p.slot] || `/api/survey/${encodeURIComponent(token)}/photos/${p.slot}`}" alt="" loading="lazy"><figcaption>${esc(slotName(s))}</figcaption></figure>`; }).join('')}</div></div>
      </section><section class="stack"><div class="card"><h3>${esc(t('sign'))}</h3><p class="mut">${esc(t('signHint'))}</p>
      <canvas class="sig" id="sig" aria-label="${esc(t('sign'))}"></canvas><div class="row" style="margin-top:8px"><button class="sm" id="clr">${esc(t('clear'))}</button></div>
      <label class="check"><input type="checkbox" id="decl"><span>${esc(t('declare'))}</span></label><p class="err" id="sErr" role="alert"></p></div></section></div>`;
    setDock(`<button class="p w100" id="submit" disabled>${esc(t('submit'))}</button>`);
    const cv = $('#sig'), ctx = cv.getContext('2d'); let drew = false, down = false;
    const fit = () => { const r = cv.getBoundingClientRect(), d = window.devicePixelRatio || 1; cv.width = r.width * d; cv.height = r.height * d; ctx.setTransform(d, 0, 0, d, 0, 0); ctx.lineWidth = 2.4; ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.strokeStyle = '#111'; drew = false; upd(); };
    const pt = e => { const r = cv.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
    cv.onpointerdown = e => { down = true; cv.setPointerCapture(e.pointerId); ctx.beginPath(); ctx.moveTo(...pt(e)); e.preventDefault(); };
    cv.onpointermove = e => { if (!down) return; ctx.lineTo(...pt(e)); ctx.stroke(); if (!drew) { drew = true; upd(); } };
    cv.onpointerup = cv.onpointercancel = () => down = false;
    const upd = () => { const b = $('#submit'); if (b) b.disabled = !(drew && $('#decl').checked); };
    $('#clr').onclick = fit; $('#decl').onchange = upd;
    let lastW = 0; const ro = new ResizeObserver(() => { const w = cv.getBoundingClientRect().width; if (Math.abs(w - lastW) > 2) { lastW = w; fit(); } }); ro.observe(cv);
    $('#submit').onclick = async () => {
      const b = $('#submit'); b.disabled = true; b.textContent = t('submitting');
      // white background so the signature is readable anywhere
      const out = document.createElement('canvas'); out.width = cv.width; out.height = cv.height;
      const o = out.getContext('2d'); o.fillStyle = '#fff'; o.fillRect(0, 0, out.width, out.height); o.drawImage(cv, 0, 0);
      try { const r = await post('/sign', { agree: true, signaturePng: out.toDataURL('image/png') }); ro.disconnect(); D.done = r; render(); }
      catch (e) { if (e.status === 410) return fail(e); $('#sErr').textContent = e.code === 'network' ? L().errors.network[1] : e.message; b.disabled = false; b.textContent = t('submit'); }
    };
  }

  function renderDone() {
    setDock(''); if (watchId != null) navigator.geolocation.clearWatch(watchId);
    app.innerHTML = `${steps(4)}<div class="card" style="margin-top:8px"><h1>${esc(t('doneTitle', { name: D.customerName }))}</h1>
      <p>${esc(t(D.done.filed ? 'doneText' : 'doneLate', { claim: D.claimNo }))}</p>
      <p class="note" dir="ltr" style="overflow-wrap:anywhere">SHA-256 ${esc((D.done.reportHash || '').slice(0, 32))}…</p></div>`;
  }

  function alertBox(msg) {
    const m = document.createElement('div'); m.className = 'modal';
    m.innerHTML = `<div class="card"><p>${esc(msg)}</p><button class="p w100">OK</button></div>`;
    m.querySelector('button').onclick = () => m.remove(); document.body.appendChild(m);
  }
  function fail(e) { D = { ...(D || {}), error: ['link-invalid', 'link-used', 'link-expired'].includes(e.code) ? e.code : 'network' }; render(); }

  async function load() {
    try { D = await api(''); if (D.language && !stored()) { lang = D.language; applyLang(); } }
    catch (e) { D = { error: ['link-invalid', 'link-used', 'link-expired'].includes(e.code) ? e.code : 'network' }; }
    render();
  }
  applyLang(); load();
})();
