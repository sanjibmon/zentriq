(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const day = (v) => (v ? String(v).slice(0, 10) : '—');
  const when = (v) => (v ? String(v).replace('T', ' ').slice(0, 16) : '—');
  const num = (v) => (v == null ? 'Unlimited' : Number(v).toLocaleString());
  const view = $('view');
  let me = null, catalog = null;

  async function api(path, opts = {}) {
    const init = { method: opts.method || 'GET', headers: { 'X-ZLS-Request': '1' }, credentials: 'same-origin' };
    if (opts.body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(opts.body); }
    const r = await fetch('/admin/api' + path, init);
    if (r.status === 401 && path !== '/login') { me = null; showLogin(); throw new Error('Signed out.'); }
    const text = await r.text();
    const j = text ? JSON.parse(text) : null;
    if (!r.ok) throw new Error((j && (j.title || j.detail)) || `Request failed (${r.status}).`);
    return j;
  }

  function flash(el, text, kind = 'bad') { el.textContent = text; el.className = 'msg ' + kind; }

  // ---- dialog helper ---------------------------------------------------------------------------------------
  function dialog(title, bodyHtml, buttons) {
    return new Promise((resolve) => {
      const d = $('dlg');
      $('dlgtitle').textContent = title;
      $('dlgbody').innerHTML = bodyHtml + '<div id="dlgmsg" class="msg hidden"></div>';
      const foot = $('dlgfoot'); foot.innerHTML = '';
      const close = (v) => { d.close(); resolve(v); };
      for (const b of buttons) {
        const btn = document.createElement('button');
        btn.type = 'button'; btn.textContent = b.label; if (b.kind) btn.className = b.kind;
        btn.addEventListener('click', async () => {
          if (!b.run) return close(null);
          btn.disabled = true;
          try { const v = await b.run(); if (v !== false) close(v); }
          catch (e) { flash($('dlgmsg'), e.message); }
          finally { btn.disabled = false; }
        });
        foot.appendChild(btn);
      }
      d.addEventListener('cancel', () => resolve(null), { once: true });
      d.showModal();
    });
  }
  const val = (id) => { const el = $(id); return el ? el.value.trim() : ''; };
  const intOrNull = (id) => { const v = val(id); return v === '' ? null : parseInt(v, 10); };

  function download(name, text) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    a.download = name; a.click();
  }
  function readFile(input) {
    return new Promise((res, rej) => {
      const f = input.files[0];
      if (!f) return rej(new Error('Choose a file.'));
      f.text().then((t) => { try { res(JSON.parse(t)); } catch { rej(new Error('The file is not valid JSON.')); } });
    });
  }

  // ---- sign-in ---------------------------------------------------------------------------------------------
  function showLogin(needCode = false) {
    $('tabs').classList.add('hidden'); $('logout').classList.add('hidden'); $('who').textContent = '';
    view.innerHTML = `
      <section class="card narrow">
        <h1>Sign in</h1><p class="muted">License administration for Zentriq.</p>
        <form id="lf">
          <div><label for="le">Email</label><input id="le" type="email" autocomplete="username" required></div>
          <div class="mt"><label for="lp">Passphrase</label><input id="lp" type="password" autocomplete="current-password" required></div>
          <div id="lcw" class="${needCode ? '' : 'hidden'}"><label for="lc">Authenticator code</label><input id="lc" inputmode="numeric" autocomplete="one-time-code" maxlength="6"></div>
          <div id="lmsg" class="msg hidden"></div>
          <div class="row end"><button class="primary" type="submit">Sign in</button></div>
        </form>
      </section>`;
    $('lf').addEventListener('submit', async (e) => {
      e.preventDefault();
      try {
        const r = await api('/login', { method: 'POST', body: { email: val('le'), password: $('lp').value, code: val('lc') || null } });
        if (r.needCode) { $('lcw').classList.remove('hidden'); $('lc').focus(); flash($('lmsg'), 'Enter the code from your authenticator app.', 'ok'); return; }
        me = r; start();
      } catch (err) { flash($('lmsg'), err.message); }
    });
  }

  async function start() {
    try { me = me || await api('/me'); } catch { return; }
    catalog = await api('/catalog');
    $('tabs').classList.remove('hidden'); $('logout').classList.remove('hidden');
    $('who').textContent = `${me.name} · ${me.isOwner ? 'Owner' : 'Administrator'}`;
    route();
  }
  $('logout').addEventListener('click', async () => { await api('/logout', { method: 'POST' }).catch(() => {}); me = null; showLogin(); });
  window.addEventListener('hashchange', () => me && route());

  function route() {
    const h = location.hash.replace(/^#/, '') || '/';
    document.querySelectorAll('nav.tabs a').forEach((a) => {
      const t = a.getAttribute('href').slice(1);
      a.classList.toggle('active', t === '/' ? h === '/' : h.startsWith(t));
    });
    const m = h.match(/^\/licenses\/(.+)$/);
    const go = m ? () => licenseDetail(decodeURIComponent(m[1]))
      : h.startsWith('/licenses') ? licenses
      : h.startsWith('/customers') ? customers
      : h.startsWith('/audit') ? audit
      : h.startsWith('/settings') ? settings : dashboard;
    go().catch((e) => { view.innerHTML = `<div class="msg bad">${esc(e.message)}</div>`; });
  }

  const statusChip = (s) => `<span class="chip ${s === 'active' ? 'ok' : s === 'suspended' ? 'warn' : 'bad'}">${esc(s)}</span>`;
  const typeText = (l) => l.licenseType === 'perpetual' ? 'Perpetual' : `Subscription · ${l.termYears || '?'} yr`;
  const ends = (l) => l.licenseType === 'perpetual' ? `Support to ${day(l.supportUntil)}` : `Ends ${day(l.expiresAt)}`;

  // ---- dashboard -------------------------------------------------------------------------------------------
  async function dashboard() {
    const d = await api('/dashboard');
    const c = d.counts;
    view.innerHTML = `
      <div class="between"><h1>Dashboard</h1><button class="primary" id="nl">New license</button></div>
      <div class="grid4">
        <div class="stat"><span class="muted small">Customers</span><b>${c.customers}</b></div>
        <div class="stat"><span class="muted small">Active licenses</span><b>${c.active}</b></div>
        <div class="stat"><span class="muted small">Active activations</span><b>${c.activations}</b></div>
        <div class="stat"><span class="muted small">Online, no check-in for 7 days</span><b class="${c.stale ? 'warn' : ''}">${c.stale}</b></div>
      </div>
      <div class="card"><h2>Renewals due (90 days)</h2>
        ${d.expiring.length ? `<div class="table-wrap"><table><thead><tr><th>License</th><th>Customer</th><th>Type</th><th>Ends</th></tr></thead><tbody>
        ${d.expiring.map((x) => `<tr class="link" data-id="${esc(x.licenseId)}"><td class="mono">${esc(x.licenseId)}</td><td>${esc(x.company)}</td><td>${x.licenseType === 'perpetual' ? 'Support' : 'Subscription'}</td><td>${day(x.ends)}</td></tr>`).join('')}
        </tbody></table></div>` : '<p class="muted">Nothing is due.</p>'}
      </div>
      <div class="card"><h2>Recent activity</h2><div class="table-wrap"><table><tbody>
        ${d.recent.map((x) => `<tr><td class="small muted">${when(x.at)}</td><td>${esc(x.action)}</td><td class="mono small">${esc(x.target)}</td><td class="small">${esc(x.actor)}</td></tr>`).join('')}
      </tbody></table></div></div>`;
    view.querySelectorAll('tr[data-id]').forEach((tr) => tr.addEventListener('click', () => { location.hash = '#/licenses/' + encodeURIComponent(tr.dataset.id); }));
    $('nl').addEventListener('click', () => newLicense());
  }

  // ---- customers -------------------------------------------------------------------------------------------
  async function customers() {
    view.innerHTML = `
      <div class="between"><h1>Customers</h1><div class="row"><input id="cq" placeholder="Search"><button class="primary" id="nc">New customer</button></div></div>
      <div class="card table-wrap" id="cl"></div>`;
    const load = async () => {
      const rows = await api('/customers?q=' + encodeURIComponent(val('cq')));
      $('cl').innerHTML = rows.length ? `<table><thead><tr><th>Company</th><th>Contact</th><th>Country</th><th>Licenses</th><th></th></tr></thead><tbody>
        ${rows.map((c) => `<tr><td><b>${esc(c.company)}</b></td><td>${esc(c.contactName)}<div class="small muted">${esc(c.contactEmail)}</div></td><td>${esc(c.country)}</td>
          <td><a href="#/licenses?customer=${c.id}">${c.licenses}</a></td><td class="row end"><button data-edit="${c.id}">Edit</button><button data-lic="${c.id}">New license</button></td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">No customers yet.</p>';
      $('cl').querySelectorAll('[data-edit]').forEach((b) => b.addEventListener('click', () => editCustomer(b.dataset.edit).then((ok) => ok && load())));
      $('cl').querySelectorAll('[data-lic]').forEach((b) => b.addEventListener('click', () => newLicense(b.dataset.lic)));
    };
    $('cq').addEventListener('input', () => load());
    $('nc').addEventListener('click', () => editCustomer(null).then((ok) => ok && load()));
    await load();
  }

  async function editCustomer(id) {
    const c = id ? (await api('/customers/' + id)).customer : {};
    const f = (k, label, type = 'text') => `<div><label for="c_${k}">${label}</label><input id="c_${k}" type="${type}" value="${esc(c[k])}"></div>`;
    return dialog(id ? 'Edit customer' : 'New customer', `
      <p class="small muted">These details are signed into the customer's license file and shown in their Zentriq console.</p>
      <div class="grid2">${f('company', 'Company')}${f('contactName', 'Contact name')}${f('contactEmail', 'Contact email', 'email')}${f('contactPhone', 'Phone')}
      ${f('country', 'Country')}${f('taxId', 'Tax / VAT id')}</div>
      <div class="mt"><label for="c_address">Address</label><textarea id="c_address">${esc(c.address)}</textarea></div>
      <div><label for="c_notes">Internal notes (not in the license)</label><textarea id="c_notes">${esc(c.notes)}</textarea></div>`,
    [{ label: 'Cancel' }, { label: 'Save', kind: 'primary', run: async () => {
      const body = {}; for (const k of ['company', 'contactName', 'contactEmail', 'contactPhone', 'country', 'taxId', 'address', 'notes']) body[k] = val('c_' + k);
      const r = await api(id ? '/customers/' + id : '/customers', { method: id ? 'PUT' : 'POST', body });
      return r.id;
    } }]);
  }

  // ---- licenses --------------------------------------------------------------------------------------------
  async function licenses() {
    const params = new URLSearchParams(location.hash.split('?')[1] || '');
    view.innerHTML = `
      <div class="between"><h1>Licenses</h1>
        <div class="row"><input id="lq" placeholder="License, customer or key"><select id="ls"><option value="">All</option><option>active</option><option>suspended</option><option>revoked</option></select>
        <button class="primary" id="nl">New license</button></div></div>
      <div class="card table-wrap" id="ll"></div>`;
    const load = async () => {
      const q = new URLSearchParams({ q: val('lq'), status: val('ls') });
      if (params.get('customer')) q.set('customerId', params.get('customer'));
      const rows = await api('/licenses?' + q);
      $('ll').innerHTML = rows.length ? `<table><thead><tr><th>License</th><th>Customer</th><th>Edition</th><th>Type</th><th>IT devices</th><th>Non-IT</th><th>Cloud/VM</th><th>Techs</th><th>Activations</th><th>Status</th></tr></thead><tbody>
        ${rows.map(({ license: l, company, activations }) => `<tr class="link" data-id="${esc(l.id)}"><td class="mono">${esc(l.id)}<div class="small muted">${ends(l)}</div></td><td>${esc(company)}</td>
          <td>${l.edition === 'advanced' ? 'Advanced' : 'Standard'}</td><td>${typeText(l)}</td><td>${num(l.devices)}</td><td>${num(l.nonItPack)}</td><td>${num(l.cloudInstances)}</td><td>${num(l.technicians)}</td>
          <td>${activations} / ${l.maxActivations}</td><td>${statusChip(l.status)}</td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">No licenses match.</p>';
      $('ll').querySelectorAll('tr[data-id]').forEach((tr) => tr.addEventListener('click', () => { location.hash = '#/licenses/' + encodeURIComponent(tr.dataset.id); }));
    };
    $('lq').addEventListener('input', load); $('ls').addEventListener('change', load);
    $('nl').addEventListener('click', () => newLicense(params.get('customer')));
    await load();
  }

  function entitlementFields(l = {}) {
    const packs = catalog.nonItPacks.map((p) => `<option value="${p}" ${l.nonItPack === p ? 'selected' : ''}>${p.toLocaleString()} non-IT assets</option>`).join('');
    const mods = catalog.modules.map((m) => `<label class="check"><input type="checkbox" class="mod" value="${esc(m)}" ${(l.extraModules || []).includes(m) ? 'checked' : ''}>${esc(m)}</label>`).join('');
    return `
      <div class="grid2">
        <div><label for="e_edition">Edition</label><select id="e_edition">${catalog.editions.map((e) => `<option value="${e.id}" ${l.edition === e.id ? 'selected' : ''}>${e.title}</option>`).join('')}</select></div>
        <div><label for="e_devices">IT devices (endpoints, servers, network devices)</label><input id="e_devices" type="number" min="0" value="${l.devices ?? 100}"></div>
        <div><label for="e_nonit">Non-IT assets</label><select id="e_nonit">${packs}<option value="" ${l.id && l.nonItPack == null ? 'selected' : ''}>Unlimited</option></select></div>
        <div><label for="e_cloud">Cloud and virtual machine instances</label><input id="e_cloud" type="number" min="0" value="${l.cloudInstances ?? 0}"></div>
        <div><label for="e_tech">Remote support technicians</label><input id="e_tech" type="number" min="0" value="${l.technicians ?? 0}"></div>
        <div><label for="e_max">Servers that may activate this license</label><input id="e_max" type="number" min="1" max="100" value="${l.maxActivations ?? 1}"></div>
        <div><label for="e_grace">Grace days after the end</label><input id="e_grace" type="number" min="0" max="90" value="${l.graceDays ?? 30}"></div>
      </div>
      <p class="small muted">IT accessories are always unlimited. The edition sets the product sections; add single modules below if needed.</p>
      <details><summary class="small">Extra modules</summary><div class="grid3">${mods}</div></details>
      <div class="mt"><label for="e_notes">Internal notes</label><textarea id="e_notes">${esc(l.notes)}</textarea></div>`;
  }
  const entitlementBody = () => ({
    edition: val('e_edition'), devices: intOrNull('e_devices'), nonItPack: intOrNull('e_nonit'), cloudInstances: intOrNull('e_cloud'),
    technicians: intOrNull('e_tech'), maxActivations: intOrNull('e_max'), graceDays: intOrNull('e_grace'), notes: val('e_notes'),
    extraModules: [...document.querySelectorAll('.mod:checked')].map((x) => x.value),
  });

  async function newLicense(customerId) {
    const cs = await api('/customers');
    if (!cs.length) { await editCustomer(null); return newLicense(customerId); }
    const id = await dialog('New license', `
      <div class="grid2">
        <div><label for="n_cust">Customer</label><select id="n_cust">${cs.map((c) => `<option value="${c.id}" ${c.id === customerId ? 'selected' : ''}>${esc(c.company)}</option>`).join('')}</select></div>
        <div><label for="n_start">Starts</label><input id="n_start" type="date" value="${new Date().toISOString().slice(0, 10)}"></div>
        <div><label for="n_type">License type</label><select id="n_type"><option value="subscription">Subscription</option><option value="perpetual">Perpetual with yearly support</option></select></div>
        <div id="n_termw"><label for="n_term">Subscription term</label><select id="n_term">${catalog.subscriptionYears.map((y) => `<option value="${y}">${y} year${y > 1 ? 's' : ''}</option>`).join('')}</select></div>
        <div id="n_supw" class="hidden"><label for="n_sup">Support included</label><select id="n_sup">${catalog.supportYears.map((y) => `<option value="${y}">${y} year${y > 1 ? 's' : ''}</option>`).join('')}</select></div>
      </div>
      <h3>Entitlements</h3>${entitlementFields()}`,
    [{ label: 'Cancel' }, { label: 'Create license', kind: 'primary', run: async () => {
      const t = val('n_type');
      const r = await api('/licenses', { method: 'POST', body: { customerId: val('n_cust'), licenseType: t, termYears: t === 'subscription' ? intOrNull('n_term') : null,
        supportYears: t === 'perpetual' ? intOrNull('n_sup') : null, startsAt: val('n_start') ? val('n_start') + 'T00:00:00Z' : null, ...entitlementBody() } });
      await showKey(r.licenseKey, 'License created');
      return r.id;
    } }]).catch(() => null);
    if (id) location.hash = '#/licenses/' + encodeURIComponent(id);
  }
  // The type selector swaps the term field (bound after the dialog renders).
  document.addEventListener('change', (e) => {
    if (e.target.id !== 'n_type') return;
    const p = e.target.value === 'perpetual';
    $('n_termw').classList.toggle('hidden', p); $('n_supw').classList.toggle('hidden', !p);
  });

  function showKey(key, title) {
    return new Promise((resolve) => {
      const d = document.createElement('dialog');
      d.innerHTML = `<div class="dhead">${esc(title)}</div><div class="dbody">
        <p>Give this license key to the customer. <b>It is shown only once</b>; the portal keeps only a fingerprint.</p>
        <div class="keybox">${esc(key)}</div>
        <p class="small muted">Online: Zentriq › Licensing › Activate online. Air-gapped: the customer uses this key on the offline activation page, or you issue the file from the license page.</p></div>
        <div class="dfoot"><button type="button" class="copy">Copy</button><button type="button" class="primary done">I have saved the key</button></div>`;
      document.body.appendChild(d);
      d.querySelector('.copy').addEventListener('click', () => navigator.clipboard?.writeText(key));
      d.querySelector('.done').addEventListener('click', () => { d.close(); d.remove(); resolve(); });
      d.addEventListener('cancel', (e) => e.preventDefault());
      d.showModal();
    });
  }

  async function licenseDetail(id) {
    const d = await api('/licenses/' + encodeURIComponent(id));
    const l = d.license, c = d.customer;
    const active = d.activations.filter((a) => a.status === 'active').length;
    view.innerHTML = `
      <div class="between"><div><a href="#/licenses" class="small">← Licenses</a><h1 class="mono">${esc(l.id)}</h1>
        <p class="muted">${esc(c.company)} · ${l.edition === 'advanced' ? 'Advanced' : 'Standard'} edition · ${typeText(l)} · ${statusChip(l.status)}</p></div>
        <div class="row">
          ${l.licenseType === 'perpetual' ? '<button id="a_support">Extend support</button>' : '<button id="a_renew">Renew</button>'}
          <button id="a_edit">Change entitlements</button><button id="a_offline">Offline activation</button><button id="a_rekey">New key</button>
          ${l.status === 'active' ? '<button id="a_suspend">Suspend</button>' : l.status === 'suspended' ? '<button id="a_resume">Resume</button>' : ''}
          ${l.status !== 'revoked' ? '<button id="a_revoke" class="danger">Revoke</button>' : ''}
        </div></div>
      <div class="grid2">
        <div class="card"><h2>Terms</h2><div class="kv">
          <div>Starts</div><div>${day(l.startsAt)}</div>
          <div>${l.licenseType === 'perpetual' ? 'License' : 'Subscription ends'}</div><div>${l.licenseType === 'perpetual' ? 'Perpetual (does not expire)' : day(l.expiresAt)}</div>
          <div>Support until</div><div>${day(l.supportUntil)}</div>
          <div>Grace after the end</div><div>${l.graceDays} days</div>
          <div>License key</div><div class="mono">${esc(l.keyHint)}</div>
          <div>Activations</div><div>${active} of ${l.maxActivations}</div>
        </div></div>
        <div class="card"><h2>Entitlements</h2><div class="kv">
          <div>IT devices</div><div>${num(l.devices)}</div>
          <div>Non-IT assets</div><div>${num(l.nonItPack)}</div>
          <div>IT accessories</div><div>Unlimited</div>
          <div>Cloud / VM instances</div><div>${num(l.cloudInstances)}</div>
          <div>Remote support technicians</div><div>${num(l.technicians)}</div>
          <div>Extra modules</div><div>${l.extraModules.length ? esc(l.extraModules.join(', ')) : '—'}</div>
        </div>${l.notes ? `<p class="small muted mt">${esc(l.notes)}</p>` : ''}</div>
      </div>
      <div class="card"><h2>Activations</h2>${d.activations.length ? `<div class="table-wrap"><table><thead><tr><th>Server</th><th>Mode</th><th>Version</th><th>Usage (last report)</th><th>Activated</th><th>Last check-in</th><th>Status</th><th></th></tr></thead><tbody>
        ${d.activations.map((a) => `<tr><td>${esc(a.machineName || '—')}<div class="small muted">${esc(a.organization)}</div><div class="small muted mono">${esc(a.installationId)}</div></td>
          <td>${esc(a.mode)}</td><td>${esc(a.productVersion || '—')}</td>
          <td class="small">${Object.entries(a.usage || {}).filter(([k]) => ['devices', 'nonit_assets', 'cloud_instances', 'technicians'].includes(k)).map(([k, v]) => `${esc(k)}: ${v}`).join('<br>') || '—'}</td>
          <td class="small">${when(a.activatedAt)}</td><td class="small">${a.mode === 'online' ? when(a.lastCheckinAt) : 'offline'}</td>
          <td>${a.status === 'active' ? '<span class="chip ok">active</span>' : `<span class="chip">deactivated</span><div class="small muted">${when(a.deactivatedAt)} ${esc(a.deactivatedBy)}</div>`}</td>
          <td>${a.status === 'active' ? `<button data-deact="${a.id}">Deactivate</button>` : ''}</td></tr>`).join('')}
        </tbody></table></div>` : '<p class="muted">Not activated yet.</p>'}</div>
      <div class="card"><h2>Issued license files</h2>${d.issued.length ? `<div class="table-wrap"><table><thead><tr><th>Issued</th><th>Reason</th><th>Lease until</th><th></th></tr></thead><tbody>
        ${d.issued.slice(0, 20).map((x) => `<tr><td class="small">${when(x.issuedAt)}</td><td>${esc(x.reason)}</td><td class="small">${x.leaseUntil ? day(x.leaseUntil) : 'none (offline)'}</td>
          <td><a href="/admin/api/documents/${x.id}">Download</a></td></tr>`).join('')}</tbody></table></div>` : '<p class="muted">None yet.</p>'}</div>
      <div class="card"><h2>History</h2><div class="table-wrap"><table><tbody>
        ${d.audit.map((x) => `<tr><td class="small muted">${when(x.at)}</td><td>${esc(x.action)}</td><td class="small">${esc(x.actor)}</td><td class="small mono">${esc(x.detail)}</td></tr>`).join('')}
      </tbody></table></div></div>`;

    const reload = () => licenseDetail(id);
    const post = (path, body) => api('/licenses/' + encodeURIComponent(id) + path, { method: 'POST', body: body || {} });
    const on = (bid, fn) => $(bid) && $(bid).addEventListener('click', fn);
    on('a_renew', async () => {
      const ok = await dialog('Renew subscription', `<p>The new term starts at the current end (${day(l.expiresAt)}), or today if it has passed.</p>
        <label for="r_y">Renew for</label><select id="r_y">${catalog.subscriptionYears.map((y) => `<option value="${y}">${y} year${y > 1 ? 's' : ''}</option>`).join('')}</select>
        <p class="small muted mt">Online servers pick up the renewal at their next check-in. Offline servers need a new license file.</p>`,
      [{ label: 'Cancel' }, { label: 'Renew', kind: 'primary', run: () => post('/renew', { years: intOrNull('r_y') }) }]);
      if (ok) reload();
    });
    on('a_support', async () => {
      const ok = await dialog('Extend support', `<p>Support currently runs until ${day(l.supportUntil)}. New Zentriq versions released before the end of support can be installed.</p>
        <label for="s_y">Add</label><select id="s_y">${catalog.supportYears.map((y) => `<option value="${y}">${y} year${y > 1 ? 's' : ''}</option>`).join('')}</select>`,
      [{ label: 'Cancel' }, { label: 'Extend support', kind: 'primary', run: () => post('/support', { years: intOrNull('s_y') }) }]);
      if (ok) reload();
    });
    on('a_edit', async () => {
      const ok = await dialog('Change entitlements', entitlementFields(l) + '<p class="small muted">Online servers pick up the change at their next check-in.</p>',
        [{ label: 'Cancel' }, { label: 'Save', kind: 'primary', run: () => api('/licenses/' + encodeURIComponent(id), { method: 'PUT', body: entitlementBody() }).then(() => true) }]);
      if (ok) reload();
    });
    on('a_rekey', async () => {
      const ok = await dialog('Issue a new license key', '<p>The current key stops working for new activations. Servers already activated keep running.</p>',
        [{ label: 'Cancel' }, { label: 'Issue new key', kind: 'primary', run: () => post('/rekey') }]);
      if (ok) { await showKey(ok.licenseKey, 'New license key'); reload(); }
    });
    on('a_offline', async () => {
      const ok = await dialog('Offline activation', `<p>For an air-gapped server: attach the activation request the customer sent (Licensing › Offline activation › Download request).</p>
        <div class="drop"><input id="o_file" type="file" accept=".json,application/json"></div>`,
      [{ label: 'Cancel' }, { label: 'Issue license file', kind: 'primary', run: async () => post('/offline', { request: await readFile($('o_file')) }) }]);
      if (ok) { download(ok.fileName, ok.license); reload(); }
    });
    const status = (s, title, text) => async () => {
      const ok = await dialog(title, `<p>${text}</p><label for="st_r">Reason (audit log)</label><input id="st_r">`,
        [{ label: 'Cancel' }, { label: title, kind: s === 'revoked' ? 'danger' : 'primary', run: () => post('/status', { status: s, reason: val('st_r') }).then(() => true) }]);
      if (ok) reload();
    };
    on('a_suspend', status('suspended', 'Suspend', 'Online servers fall back to the free tier at their next check-in until the license is resumed. Data is kept.'));
    on('a_resume', status('active', 'Resume', 'Online servers get the license back at their next check-in.'));
    on('a_revoke', status('revoked', 'Revoke', 'Permanent. Online servers fall back to the free tier at their next check-in, and the key no longer activates.'));
    view.querySelectorAll('[data-deact]').forEach((b) => b.addEventListener('click', async () => {
      const ok = await dialog('Deactivate server', '<p>Frees the activation so the key can be used on another server. If the server is still online, it falls back to the free tier at its next check-in.</p>',
        [{ label: 'Cancel' }, { label: 'Deactivate', kind: 'danger', run: () => api(`/activations/${b.dataset.deact}/deactivate`, { method: 'POST' }).then(() => true) }]);
      if (ok) reload();
    }));
  }

  // ---- audit -----------------------------------------------------------------------------------------------
  async function audit() {
    view.innerHTML = `<div class="between"><h1>Audit log</h1><input id="aq" placeholder="Filter by actor, action or target"></div><div class="card table-wrap" id="al"></div>`;
    const load = async () => {
      const rows = await api('/audit?q=' + encodeURIComponent(val('aq')));
      $('al').innerHTML = `<table><thead><tr><th>When (UTC)</th><th>Actor</th><th>Action</th><th>Target</th><th>Address</th><th>Detail</th></tr></thead><tbody>
        ${rows.map((x) => `<tr><td class="small">${when(x.at)}</td><td class="small">${esc(x.actor)}</td><td>${esc(x.action)}</td><td class="mono small">${esc(x.target)}</td><td class="small">${esc(x.ip)}</td><td class="mono small">${esc(x.detail)}</td></tr>`).join('')}
      </tbody></table>`;
    };
    $('aq').addEventListener('input', load);
    await load();
  }

  // ---- settings --------------------------------------------------------------------------------------------
  async function settings() {
    const admins = await api('/admins');
    view.innerHTML = `
      <h1>Settings</h1>
      <div class="card"><h2>My account</h2>
        <p>${esc(me.name)} · ${esc(me.email)} · Authenticator app: <b>${me.mfa ? 'on' : 'off'}</b></p>
        <div class="row"><button id="s_pw">Change passphrase</button>${me.mfa ? '<button id="s_mfa_off">Turn off authenticator app</button>' : '<button id="s_mfa" class="primary">Set up authenticator app</button>'}</div></div>
      <div class="card"><div class="between"><h2>Portal accounts</h2>${me.isOwner ? '<button id="s_new">Add account</button>' : ''}</div>
        <div class="table-wrap"><table><thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Authenticator</th><th>Last sign-in</th><th>Status</th><th></th></tr></thead><tbody>
        ${admins.map((a) => `<tr><td>${esc(a.name)}</td><td>${esc(a.email)}</td><td>${a.isOwner ? 'Owner' : 'Administrator'}</td><td>${a.mfa ? 'on' : 'off'}</td><td class="small">${when(a.lastLoginAt)}</td>
          <td>${a.disabled ? '<span class="chip bad">disabled</span>' : a.lockedUntil && new Date(a.lockedUntil) > new Date() ? '<span class="chip warn">locked</span>' : '<span class="chip ok">active</span>'}</td>
          <td class="row end">${me.isOwner && a.id !== me.id ? `<button data-a="${a.id}" data-act="${a.disabled ? 'enable' : 'disable'}">${a.disabled ? 'Enable' : 'Disable'}</button>
            ${a.mfa ? `<button data-a="${a.id}" data-act="reset-mfa">Reset authenticator</button>` : ''}<button data-a="${a.id}" data-act="unlock">Unlock</button>` : ''}</td></tr>`).join('')}
        </tbody></table></div></div>
      <div class="card"><h2>Signing key</h2>
        <p>License files are signed with key <code>${esc(catalog.signing.keyId)}</code>. Zentriq trusts it only when its public key is listed in
        <code>backend/Zentriq.Infrastructure/Resources/license-public-keys.json</code> (compiled into the product). Entry for that file:</p>
        <textarea class="mono" readonly rows="8">${esc(JSON.stringify(catalog.productKeyEntry, null, 2))}</textarea>
        <p class="small muted">Public address: ${esc(catalog.publicUrl)} · Online lease: ${catalog.leaseDays} days (then 30 days of grace).</p></div>
      <div class="card"><h2>Plans</h2><div class="grid2">
        <div><h3>Trial (14 days, all features)</h3><p class="small">IT devices ${catalog.trial.devices}, non-IT ${catalog.trial.nonit_assets}, cloud/VM ${catalog.trial.cloud_instances}, technicians ${catalog.trial.technicians}, accessories unlimited.</p></div>
        <div><h3>Free tier (Standard edition)</h3><p class="small">IT devices ${catalog.free.devices}, non-IT ${catalog.free.nonit_assets}, cloud/VM ${catalog.free.cloud_instances}, no remote support, accessories unlimited.</p></div>
      </div></div>`;
    const on = (bid, fn) => $(bid) && $(bid).addEventListener('click', fn);
    on('s_pw', () => dialog('Change passphrase', `<div><label for="p_c">Current passphrase</label><input id="p_c" type="password" autocomplete="current-password"></div>
      <div class="mt"><label for="p_n">New passphrase (12+ characters)</label><input id="p_n" type="password" autocomplete="new-password"></div>`,
      [{ label: 'Cancel' }, { label: 'Change', kind: 'primary', run: () => api('/me/password', { method: 'POST', body: { current: $('p_c').value, password: $('p_n').value } }).then(() => true) }]));
    on('s_mfa', async () => {
      const s = await api('/me/mfa/setup', { method: 'POST' });
      const ok = await dialog('Set up authenticator app', `<div class="row"><img class="qr" alt="QR code" src="data:image/svg+xml;base64,${btoa(s.svg)}">
        <div><p>Scan the code with an authenticator app, or enter this key:</p><p class="mono">${esc(s.secret)}</p>
        <label for="m_c">Code from the app</label><input id="m_c" inputmode="numeric" maxlength="6"></div></div>`,
      [{ label: 'Cancel' }, { label: 'Turn on', kind: 'primary', run: () => api('/me/mfa/enable', { method: 'POST', body: { secret: s.secret, code: val('m_c') } }).then(() => true) }]);
      if (ok) { me = await api('/me'); settings(); }
    });
    on('s_mfa_off', async () => {
      const ok = await dialog('Turn off authenticator app', '<label for="m_c">Code from the app</label><input id="m_c" inputmode="numeric" maxlength="6">',
        [{ label: 'Cancel' }, { label: 'Turn off', kind: 'danger', run: () => api('/me/mfa/disable', { method: 'POST', body: { code: val('m_c') } }).then(() => true) }]);
      if (ok) { me = await api('/me'); settings(); }
    });
    on('s_new', async () => {
      const ok = await dialog('Add portal account', `<div class="grid2"><div><label for="n_n">Name</label><input id="n_n"></div><div><label for="n_e">Email</label><input id="n_e" type="email"></div></div>
        <div class="mt"><label for="n_p">Initial passphrase (12+ characters; ask them to change it)</label><input id="n_p" type="password" autocomplete="new-password"></div>
        <label class="check mt"><input id="n_o" type="checkbox">Owner (can manage portal accounts)</label>`,
      [{ label: 'Cancel' }, { label: 'Add', kind: 'primary', run: () => api('/admins', { method: 'POST', body: { name: val('n_n'), email: val('n_e'), password: $('n_p').value, owner: $('n_o').checked } }) }]);
      if (ok) settings();
    });
    view.querySelectorAll('[data-a]').forEach((b) => b.addEventListener('click', async () => {
      await api(`/admins/${b.dataset.a}/${b.dataset.act}`, { method: 'POST' }).catch((e) => alertMsg(e.message));
      settings();
    }));
  }
  function alertMsg(text) { dialog('Not done', `<p>${esc(text)}</p>`, [{ label: 'Close' }]); }

  start();
})();
