// Staff console: claims desk + UW records.
(() => {
  'use strict';
  const $ = s => document.querySelector(s);
  const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = t => t ? new Date(t).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
  let key = ''; try { key = sessionStorage.getItem('ms-key') || ''; } catch { }
  let policies = [], claims = [], current = null;

  async function api(path, opt = {}) {
    const r = await fetch('/api/staff' + path, { ...opt, headers: { 'X-Staff-Key': key, ...(opt.body && !opt.raw ? { 'Content-Type': 'application/json' } : {}), ...(opt.headers || {}) } });
    if (r.status === 401) { askKey(); throw new Error('Staff key required'); }
    const j = r.status === 204 ? {} : await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
    return j;
  }
  function askKey() { $('#keyModal').classList.remove('hide'); $('#keyIn').focus(); }
  $('#keyForm').onsubmit = e => { e.preventDefault(); key = $('#keyIn').value; try { sessionStorage.setItem('ms-key', key); } catch { } $('#keyModal').classList.add('hide'); boot(); };

  // ---------- routing ----------
  const erpTab = window.ErpTab(api, esc);
  function route() {
    const v = ({ '#records': 'records', '#erp': 'erp' })[location.hash] || 'desk';
    ['desk', 'records', 'erp'].forEach(x => $('#v-' + x).classList.toggle('hide', x !== v));
    if (v === 'erp') erpTab.load().catch(e => $('#erpNow').textContent = 'Error: ' + e.message);
    document.querySelectorAll('nav a').forEach(a => a.dataset.v === v ? a.setAttribute('aria-current', 'page') : a.removeAttribute('aria-current'));
    if (v === 'records') loadClaims();
  }
  addEventListener('hashchange', route);

  function recPill(rec) {
    const cls = { Approve: 'g', Inspect: 'y', Refer: 'r' }[rec] || '';
    return rec ? `<span class="pill ${cls}">AI: ${esc(rec)}</span>` : '';
  }
  function statusPill(c) {
    if (c.status === 'Filed') return c.flagged && !c.reviewed ? '<span class="pill r">Filed · review</span>' : `<span class="pill g">${c.reviewed ? 'Reviewed' : 'Filed'}</span>`;
    if (c.status === 'ErpFailed') return '<span class="pill r">ERP upload failed</span>';
    if (new Date(c.expiresAt) < new Date()) return '<span class="pill">Link expired</span>';
    const label = { LinkSent: 'Link sent', InSurvey: 'In survey', Analysing: 'AI check', ReadyToSign: 'Awaiting signature' }[c.status] || c.status;
    return `<span class="pill y">${esc(label)}</span>`;
  }

  // ---------- desk ----------
  function polInfo() {
    const p = policies.find(x => x.policyNo === $('#fPolicy').value); if (!p) return;
    $('#polInfo').innerHTML = [['Customer', p.customerName], ['Vehicle', `${p.make} ${p.model} ${p.year}`], ['Plate', p.plateNo], ['VIN', p.vin], ['Cover', p.coverType], ['Mobile', p.mobile]]
      .map(([k, v]) => `<div><span>${k}</span><b>${esc(v)}</b></div>`).join('');
    $('#fHandler').value = p.uwHandler || '';
  }
  $('#fPolicy').onchange = polInfo;
  $('#fDate').value = new Date().toISOString().slice(0, 10);
  $('#newClaim').onsubmit = async e => {
    e.preventDefault(); const b = e.submitter; b.disabled = true; $('#dErr').textContent = '';
    try {
      const r = await api('/claims', { method: 'POST', body: JSON.stringify({ policyNo: $('#fPolicy').value, claimNo: $('#fClaim').value || null, incidentDate: $('#fDate').value || null, handler: $('#fHandler').value || null, channel: $('#fChannel').value }) });
      $('#fClaim').value = ''; showLink(r); loadDesk();
    } catch (err) { $('#dErr').textContent = err.message; }
    b.disabled = false;
  };
  function showLink(r) {
    const o = $('#linkOut'); o.classList.remove('hide');
    const d = r.claim.lastDelivery || '';
    const ok = /sent to/i.test(d) && !/failed|not configured/i.test(d);
    o.innerHTML = `<h3>Link ready · ${esc(r.claim.claimNo)}</h3>
      <p class="pill ${ok ? 'g' : 'y'}" style="white-space:normal">${esc(d || 'Not sent')}</p>
      <div class="msg" dir="auto" style="margin-top:10px">${esc(r.message)}</div>
      <p class="note">${ok ? 'The customer opens the link on their phone; the photos and signed report appear under UW records.' : 'Copy the message and send it yourself, or set up Gmail / Twilio in appsettings.json.'}</p>
      <div class="row"><button class="p" id="cpy">Copy message</button><a class="btn y" href="${esc(r.link)}" target="_blank" rel="noopener">Open link</a></div>`;
    $('#cpy').onclick = async () => { try { await navigator.clipboard.writeText(r.message); $('#cpy').textContent = 'Copied'; } catch { $('#cpy').textContent = 'Select and copy the text'; } };
    o.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }
  async function loadDesk() {
    try { claims = await api('/claims'); } catch { return; }
    $('#deskCount').textContent = claims.length ? `${claims.length} total` : '';
    $('#deskList').innerHTML = claims.length ? claims.slice(0, 200).map(c => `<button class="item" data-no="${esc(c.claimNo)}"><span><b>${esc(c.claimNo)}</b> · ${esc(c.customerName)}<br><span class="note">${esc(c.vehicle)} · ${fmt(c.createdAt)}</span></span>${statusPill(c)}</button>`).join('') : '<p class="mut">No claims yet.</p>';
    $('#deskList').querySelectorAll('.item').forEach(b => b.onclick = () => { location.hash = 'records'; openDetail(b.dataset.no); });
  }

  // ---------- records ----------
  let qTimer;
  ['#q', '#from', '#to', '#st'].forEach(s => $(s).addEventListener('input', () => { clearTimeout(qTimer); qTimer = setTimeout(loadClaims, 250); }));
  async function loadClaims() {
    const p = new URLSearchParams(); if ($('#q').value) p.set('q', $('#q').value); if ($('#from').value) p.set('from', $('#from').value); if ($('#to').value) p.set('to', $('#to').value); if ($('#st').value) p.set('status', $('#st').value);
    let list; try { list = await api('/claims?' + p); } catch { return; }
    $('#cnt').textContent = `${list.length} record${list.length === 1 ? '' : 's'} · ${list.filter(c => c.flagged && !c.reviewed && c.status === 'Filed').length} flagged for review`;
    $('#recList').innerHTML = list.length ? list.map(c => `<button class="item ${current === c.claimNo ? 'sel' : ''}" data-no="${esc(c.claimNo)}"><span><b>${esc(c.claimNo)}</b> · ${esc(c.customerName)}<br><span class="note">${esc(c.policyNo)} · ${esc(c.plate)} · ${c.findings} finding(s)</span></span><span class="row" style="gap:6px">${recPill(c.aiRec)}${statusPill(c)}</span></button>`).join('') : '<p class="mut">No matching records.</p>';
    $('#recList').querySelectorAll('.item').forEach(b => b.onclick = () => openDetail(b.dataset.no));
  }

  async function openDetail(no) {
    current = no; const D = $('#detail'); D.innerHTML = '<div class="spin"></div>';
    let r; try { r = await api('/claims/' + encodeURIComponent(no)); } catch (e) { D.innerHTML = `<p class="err">${esc(e.message)}</p>`; return; }
    const c = r.summary, v = r.vehicle;
    const btns = [
      r.reportUrl ? `<a class="btn p" href="${esc(r.reportUrl)}" target="_blank" rel="noopener">Open report</a><a class="btn" href="${esc(r.reportUrl)}&download=true">Download</a>` : '',
      c.status === 'Filed' && c.flagged && !c.reviewed ? '<button class="y" id="rev">Mark reviewed</button>' : '',
      c.status === 'ErpFailed' ? '<button class="y" id="retry">Retry ERP upload</button>' : '',
      !['Filed', 'ErpFailed'].includes(c.status) ? '<button id="resend">New link</button>' : '',
      c.status === 'Filed' || c.status === 'ErpFailed' ? '<button id="pay">ERP payload</button>' : '',
    ].join('');
    D.innerHTML = `<div class="stack"><div class="row" style="justify-content:space-between"><div><h2 style="margin:0">${esc(c.claimNo)}</h2><span class="note">${esc(c.policyNo)} · ${esc(c.customerName)} · handler ${esc(c.handler)}</span></div>${statusPill(c)}</div>
      <div class="row">${btns}</div><pre id="payload" class="hide"></pre>
      ${c.erpError ? `<p class="err">ERP: ${esc(c.erpError)}</p>` : ''}${c.erpRef ? `<p class="note">ERP reference: ${esc(c.erpRef)} · filed ${fmt(c.filedAt)}</p>` : ''}
      ${r.aiMessage ? `<p class="pill r" style="white-space:normal">${esc(r.aiMessage)}</p>` : ''}
      ${r.aiReview ? `<div class="card ai-${esc(r.aiReview.recommendation)}"><div class="row" style="justify-content:space-between"><h3 style="margin:0">AI review</h3>${recPill(r.aiReview.recommendation)}</div>
        <p style="margin:8px 0">${esc(r.aiReview.summary)}</p>${r.aiReview.reasons.length ? `<ul style="margin:0;padding-inline-start:18px">${r.aiReview.reasons.map(x => `<li>${esc(x)}</li>`).join('')}</ul>` : ''}
        <p class="note">Confidence ${Math.round(r.aiReview.confidence * 100)}% · AI suggestion only, the underwriter decides.</p></div>` : ''}
      <div class="card"><h3>Vehicle</h3><div class="kv">${[['Make', v.make], ['Model', v.model], ['Year', v.year], ['Colour', v.colour], ['Plate', v.plateNo], ['VIN', v.vin], ['Odometer', v.odometer || '—'], ['Incident', r.incidentDate || '—']].map(([k, x]) => `<div><span>${k}</span><b>${esc(x)}</b></div>`).join('')}</div></div>
      <div class="card"><h3>Findings (${r.findings.length})</h3>${r.findings.length ? `<table class="rs"><thead><tr><th>Fault</th><th>Part</th><th>Severity</th><th>Conf.</th><th>Photo</th></tr></thead><tbody>${r.findings.map(f => `<tr><td data-l="Fault">${esc(f.type)}</td><td data-l="Part">${esc(f.part)}</td><td data-l="Severity" class="sev-${esc(f.severity)}">${esc(f.severity)}</td><td data-l="Conf.">${Math.round(f.confidence * 100)}%${f.needsReview ? ' <span class="pill r">review</span>' : ''}</td><td data-l="Photo">${esc(f.photoLabel)}</td></tr>`).join('')}</tbody></table>` : `<p class="mut">${c.status === 'Filed' || c.status === 'ErpFailed' ? 'No automatic findings.' : 'Survey not finished yet.'}</p>`}</div>
      ${r.checks.length ? `<div class="card"><h3>Evidence checks</h3>${r.checks.map(x => `<div>${x.ok === true ? '✓' : x.ok === false ? '⚠' : '•'} ${esc(x.text)}</div>`).join('')}</div>` : ''}
      <div class="card"><h3>Photos (${r.photos.length})</h3><div class="imgs">${r.photos.map(p => `<figure><a href="${esc(p.url)}" target="_blank" rel="noopener"><img src="${esc(p.url)}" alt="${esc(p.label)}" loading="lazy"></a><figcaption><b>${esc(p.label)}</b><br>${fmt(p.receivedAt)}<br>${p.location ? esc(p.location.lat.toFixed(5) + ', ' + p.location.lng.toFixed(5)) : 'No GPS'}${p.liveCamera ? '' : ' · not live'}</figcaption></figure>`).join('') || '<p class="mut">No photos yet.</p>'}</div></div>
      ${r.signatureUrl ? `<div class="card"><h3>Signature</h3><img src="${esc(r.signatureUrl)}" alt="Client signature" style="max-width:300px;width:100%;background:#fff;border-radius:8px"><p class="note" style="overflow-wrap:anywhere">Signed ${fmt(r.signedAt)} · SHA-256 ${esc(r.reportHash)}</p></div>` : ''}</div>`;
    $('#rev')?.addEventListener('click', async () => { await api(`/claims/${encodeURIComponent(no)}/review`, { method: 'POST', body: JSON.stringify({ note: null }) }); openDetail(no); loadClaims(); });
    $('#retry')?.addEventListener('click', async e => { e.target.disabled = true; try { await api(`/claims/${encodeURIComponent(no)}/retry-erp`, { method: 'POST' }); } catch (err) { alert(err.message); } openDetail(no); loadClaims(); });
    $('#resend')?.addEventListener('click', async () => { const x = await api(`/claims/${encodeURIComponent(no)}/resend`, { method: 'POST' }); location.hash = 'desk'; showLink(x); loadDesk(); });
    $('#pay')?.addEventListener('click', async () => { const p = $('#payload'); if (p.classList.contains('hide')) p.textContent = JSON.stringify(await api(`/claims/${encodeURIComponent(no)}/payload`), null, 2); p.classList.toggle('hide'); });
    if (innerWidth < 760) D.scrollIntoView({ behavior: 'smooth' });
  }

  async function loadPolicies() {
    try { policies = await api('/policies'); $('#dErr').textContent = ''; }
    catch (e) { policies = []; $('#dErr').textContent = 'Could not read policies from the ERP: ' + e.message + ' — check the ERP connection tab.'; }
  }
  window.addEventListener('erp-changed', async () => { await loadPolicies(); fillPolicies(); const i = await api('/info'); setInfo(i); });
  const setInfo = i => $('#info').textContent = `ERP: ${i.erp} · AI: ${i.ai} · Email: ${i.email ? 'on' : 'off'} · SMS: ${i.sms ? 'on' : 'off'}`;
  function fillPolicies() {
    $('#fPolicy').innerHTML = policies.map(p => `<option value="${esc(p.policyNo)}">${esc(p.policyNo)} · ${esc(p.customerName)}</option>`).join('');
    polInfo();
  }
  async function boot() {
    try {
      const i = await api('/info');
      setInfo(i);
      await loadPolicies();
      fillPolicies(); loadDesk(); route();
    } catch (e) { if (e.message !== 'Staff key required') $('#info').textContent = 'Error: ' + e.message; }
  }
  boot();
})();
