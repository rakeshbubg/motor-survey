// Staff console: ERP connection tab (choose, configure, test and switch the ERP).
window.ErpTab = (api, esc) => {
  const $ = s => document.querySelector(s);
  const LOGO = { Excel: ['#1d6f42', 'XLS'], Odoo: ['#714b67', 'odoo'], Dynamics: ['#0b53ce', 'D365'], SAP: ['#0a6ed1', 'SAP'] };
  const LABEL = { PolicyNo: 'Policy no.', CustomerName: 'Customer name', Mobile: 'Mobile', Email: 'Email', Make: 'Make', Model: 'Model', Year: 'Year', Colour: 'Colour', PlateNo: 'Plate no.', VIN: 'VIN / chassis', CoverType: 'Cover type', PolicyStart: 'Policy start', PolicyEnd: 'Policy end', SumInsuredKWD: 'Sum insured (KWD)', UwHandler: 'UW handler' };
  let view = null, sel = null, odooFields = [];

  async function load() {
    view = await api('/erp');
    sel = sel || view.active;
    $('#erpNow').innerHTML = `Connected now: <b>${esc(view.name)}</b>${view.updatedAt ? ` · saved ${new Date(view.updatedAt).toLocaleString('en-GB')}` : ''}. Click an ERP to set it up.`;
    cards(); form();
  }

  function cards() {
    $('#erpCards').innerHTML = view.providers.map(p => {
      const [bg, txt] = LOGO[p.id] || ['#555', p.id];
      const active = p.id === view.active;
      return `<button type="button" class="erp-card ${p.id === sel ? 'sel' : ''}" role="radio" aria-checked="${p.id === sel}" data-id="${esc(p.id)}" ${p.available ? '' : 'disabled'}>
        <span class="logo" style="background:${bg}">${esc(txt)}</span><b>${esc(p.title)}</b><p>${esc(p.description)}</p>
        ${active ? '<span class="pill g">Connected</span>' : p.available ? '' : '<span class="pill">Coming soon</span>'}</button>`;
    }).join('');
    $('#erpCards').querySelectorAll('.erp-card:not(:disabled)').forEach(b => b.onclick = () => { sel = b.dataset.id; cards(); form(); $('#erpForm').scrollIntoView({ behavior: 'smooth', block: 'nearest' }); });
  }

  function form() {
    const f = $('#erpForm'); f.classList.remove('hide');
    if (sel === 'Excel') {
      f.innerHTML = `<h2>Excel file</h2><p class="mut">Policies are read from the <b>Policies</b> sheet. Upload a new file to replace it (same columns as the sample).</p>
        <div class="kv"><div><span>Current file</span><b>${esc(view.excel.fileName)}</b></div><div><span>Status</span><b>${view.excel.exists ? 'Found' : 'Missing'}</b></div></div>
        <label class="f" style="margin-top:14px">Upload .xlsx<input type="file" id="xlsx" accept=".xlsx" style="margin-top:6px"></label>
        <div class="row"><button type="button" id="tst">Test connection</button><button type="button" class="p" id="sav">${view.active === 'Excel' ? 'Save' : 'Use Excel'}</button></div><div id="res"></div>`;
      $('#xlsx').onchange = async e => {
        const file = e.target.files[0]; if (!file) return;
        const fd = new FormData(); fd.append('file', file);
        try { const r = await api('/erp/excel', { method: 'POST', body: fd, raw: true }); view = r.view; show(true, `Uploaded: ${r.count} policies found.`); cards(); }
        catch (err) { show(false, err.message); }
      };
    } else if (sel === 'Odoo') {
      const o = view.odoo;
      f.innerHTML = `<h2>Odoo</h2><p class="mut">Use an integration user. Create its API key in Odoo: <i>Preferences → Account Security → New API Key</i>.</p>
        <fieldset><legend>Connection</legend><div class="kv" style="gap:0 14px">
          <label class="f">Odoo address<input type="text" id="oUrl" placeholder="https://yourcompany.odoo.com" value="${esc(o.url)}" inputmode="url" autocomplete="off"></label>
          <label class="f">Database<input type="text" id="oDb" value="${esc(o.database)}" autocomplete="off"></label>
          <label class="f">API version<select id="oApi"><option value="Json2" ${o.api !== 'JsonRpc' ? 'selected' : ''}>Odoo 19 or later (JSON-2)</option><option value="JsonRpc" ${o.api === 'JsonRpc' ? 'selected' : ''}>Odoo 17 / 18 (JSON-RPC)</option></select></label>
          <label class="f" id="oUserL">Username (login)<input type="text" id="oUser" value="${esc(o.username)}" autocomplete="off"></label>
          <label class="f">API key<input type="password" id="oKey" placeholder="${o.hasKey ? 'Saved — leave blank to keep' : 'Paste the API key'}" autocomplete="new-password"></label>
          <label class="f">Policy model<input type="text" id="oModel" value="${esc(o.policyModel)}" placeholder="x_motor_policy" autocomplete="off"></label>
        </div>
        <label class="check"><input type="checkbox" id="oNote" ${o.postChatterNote ? 'checked' : ''}><span>Post a note in the policy's chatter when a survey is filed (UW handler sees it)</span></label></fieldset>
        <fieldset><legend>Field mapping</legend><p class="mut" style="margin-top:-4px">Which Odoo field holds each value. <button type="button" class="sm" id="ldF">Load fields from Odoo</button></p>
          <div class="map" id="map">${Object.keys(LABEL).map(k => `<label for="m-${k}">${LABEL[k]}</label><input type="text" id="m-${k}" list="odooFields" value="${esc(o.fields[k] || '')}" autocomplete="off">`).join('')}</div>
          <datalist id="odooFields"></datalist></fieldset>
        <div class="row" style="margin-top:14px"><button type="button" id="tst">Test connection</button><button type="button" class="p" id="sav">${view.active === 'Odoo' ? 'Save changes' : 'Save and use Odoo'}</button></div><div id="res"></div>`;
      const api2 = () => $('#oUserL').classList.toggle('hide', $('#oApi').value !== 'JsonRpc');
      $('#oApi').onchange = api2; api2();
      $('#ldF').onclick = async () => {
        const b = $('#ldF'); b.disabled = true; b.textContent = 'Loading…';
        try {
          const r = await api('/erp/fields', { method: 'POST', body: JSON.stringify(body()) });
          if (!r.ok) show(false, 'Could not load fields: ' + r.error);
          else { odooFields = r.fields; $('#odooFields').innerHTML = r.fields.map(x => `<option value="${esc(x.name)}">${esc(x.label)} (${esc(x.type)})</option>`).join(''); autoMap(); show(true, `${r.fields.length} fields loaded from ${esc($('#oModel').value)}. Pick from the suggestions in each box.`); }
        } catch (err) { show(false, err.message); }
        b.disabled = false; b.textContent = 'Load fields from Odoo';
      };
    }
    $('#tst').onclick = test; $('#sav').onclick = save;
  }

  // Fill empty mapping boxes with the closest Odoo field name (e.g. "Plate" -> x_plate_no)
  function autoMap() {
    const norm = s => String(s).toLowerCase().replace(/^x_(studio_)?/, '').replace(/[^a-z0-9]/g, '');
    for (const k of Object.keys(LABEL)) {
      const inp = $('#m-' + k); if (inp.value) continue;
      const want = [norm(k), norm(LABEL[k])];
      const hit = odooFields.find(f => want.includes(norm(f.name)) || want.includes(norm(f.label)));
      if (hit) inp.value = hit.name;
    }
  }

  function body() {
    if (sel !== 'Odoo') return { provider: sel };
    const fields = {}; Object.keys(LABEL).forEach(k => fields[k] = $('#m-' + k).value.trim());
    return { provider: 'Odoo', apiKey: $('#oKey').value || null,
      odoo: { url: $('#oUrl').value.trim(), database: $('#oDb').value.trim(), username: $('#oUser').value.trim(), api: $('#oApi').value, policyModel: $('#oModel').value.trim(), postChatterNote: $('#oNote').checked, fields } };
  }

  function show(ok, html) { $('#res').innerHTML = `<div class="result ${ok ? 'ok' : 'bad'}" role="status">${html}</div>`; }

  async function test() {
    const b = $('#tst'); b.disabled = true; b.textContent = 'Testing…';
    try {
      const r = await api('/erp/test', { method: 'POST', body: JSON.stringify(body()) });
      if (!r.ok) show(false, '<b>Not connected.</b> ' + esc(r.error));
      else show(true, `<b>Connected</b> in ${r.ms} ms · ${r.count} polic${r.count === 1 ? 'y' : 'ies'} found.` + (r.sample.length ? `<table class="rs" style="margin-top:8px"><thead><tr><th>Policy</th><th>Customer</th><th>Vehicle</th><th>Plate</th></tr></thead><tbody>${r.sample.map(p => `<tr><td data-l="Policy">${esc(p.policyNo)}</td><td data-l="Customer">${esc(p.customerName)}</td><td data-l="Vehicle">${esc(p.make)} ${esc(p.model)} ${esc(p.year || '')}</td><td data-l="Plate">${esc(p.plateNo)}</td></tr>`).join('')}</tbody></table>` : '<br>No policies returned — check the model and field mapping.'));
    } catch (err) { show(false, esc(err.message)); }
    b.disabled = false; b.textContent = 'Test connection';
  }

  async function save() {
    const b = $('#sav'); b.disabled = true;
    try { view = await api('/erp/save', { method: 'POST', body: JSON.stringify(body()) }); sel = view.active; await load(); show(true, `<b>Saved.</b> The app now uses ${esc(view.name)}. New claims read policies from it and signed surveys are filed there.`); window.dispatchEvent(new Event('erp-changed')); }
    catch (err) { show(false, esc(err.message)); b.disabled = false; }
  }

  return { load };
};
