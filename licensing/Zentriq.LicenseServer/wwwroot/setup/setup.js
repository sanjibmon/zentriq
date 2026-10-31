(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const view = $('view');
  let st = null;

  async function call(path, body, method) {
    const init = { method: method || (body === undefined ? 'GET' : 'POST'), headers: { 'X-ZLS-Request': '1' }, credentials: 'same-origin' };
    if (body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
    const r = await fetch(path, init);
    const text = await r.text();
    const j = text ? JSON.parse(text) : null;
    if (!r.ok) throw new Error((j && (j.title || j.detail)) || `Request failed (${r.status}).`);
    return j;
  }
  const api = (p, b) => call('/setup/api' + p, b);
  const val = (id) => ($(id) ? $(id).value.trim() : '');
  function flash(id, text, kind = 'bad') { const el = $(id); el.textContent = text; el.className = 'msg ' + kind; }
  function busy(btn, on) { if (btn) btn.disabled = on; }
  async function act(btn, msgId, fn) {
    busy(btn, true);
    try { await fn(); } catch (e) { flash(msgId, e.message); } finally { busy(btn, false); }
  }
  function readFile(input, asBase64) {
    return new Promise((resolve, reject) => {
      const f = input.files && input.files[0];
      if (!f) return resolve(null);
      const r = new FileReader();
      r.onload = () => resolve(asBase64 ? String(r.result).split(',')[1] : String(r.result));
      r.onerror = () => reject(new Error('The file could not be read.'));
      if (asBase64) r.readAsDataURL(f); else r.readAsText(f);
    });
  }
  const chip = (done, text) => `<span class="chip ${done ? 'ok' : 'warn'}">${done ? '✓ ' : ''}${esc(text)}</span>`;

  async function load() {
    st = await api('/state');
    $('who').textContent = st.user ? `Signed in as ${st.user}` : '';
    render();
  }

  function render() {
    if (!st.ownerExists) return renderOwner();
    if (!st.signedIn) return renderSignIn();
    const key = st.key;
    const parts = [];
    parts.push(`<div class="card"><h1>${st.complete ? 'License server is ready' : 'Set up the license server'}</h1>
      <p class="muted">Version ${esc(st.version || '')} · data folder <code>${esc(st.dataFolder)}</code> (database, signing key, daily backups)</p>
      <div class="row">${chip(true, 'Owner account')} ${chip(!!key, 'Signing key')} ${chip(key && key.backedUp, 'Key backed up')} ${chip(st.complete, 'Finished')}</div></div>`);

    // Step 2: signing key
    if (!key) {
      parts.push(`<div class="card"><h2>2. Signing key</h2>
        <p>The signing key makes your licenses genuine. It is created here, on this server, encrypted with your passphrase, and never leaves it.
        The server unlocks it by itself after a restart.</p>
        <div class="row"><label class="check"><input type="radio" name="km" value="new" checked> Create a new key</label>
        <label class="check"><input type="radio" name="km" value="import"> Use a key I created before (.private.pem)</label></div>
        <div id="knew" class="mt">
          <div class="grid2"><div><label>Key id</label><input id="kid" value="${esc(st.suggestedKeyId)}"></div><div></div>
          <div><label>Passphrase (16+ characters, e.g. five random words)</label><input id="kp1" type="password" autocomplete="new-password"></div>
          <div><label>Repeat the passphrase</label><input id="kp2" type="password" autocomplete="new-password"></div></div>
          <p class="small muted mt">Write the passphrase down and keep it apart from the key backup. Without both, no further licenses can be issued with this key.</p>
        </div>
        <div id="kimp" class="mt hidden">
          <div class="grid2"><div><label>Encrypted private key (&lt;key id&gt;.private.pem)</label><input id="kfile" type="file" accept=".pem"></div>
          <div><label>Key id</label><input id="kid2" placeholder="zentriq-prod-2026-10"></div>
          <div><label>Its passphrase</label><input id="kp3" type="password" autocomplete="off"></div></div>
        </div>
        <div id="kmsg" class="msg hidden"></div>
        <div class="row end mt"><button class="primary" id="kgo">Create the key</button></div></div>`);
    } else {
      parts.push(`<div class="card"><h2>2. Signing key</h2>
        <div class="kv"><div>Key id</div><div><code>${esc(key.keyId)}</code></div><div>Fingerprint</div><div><code>${esc(key.fingerprint || '')}</code></div>
        <div>Status</div><div>${key.loaded ? '<span class="ok">Unlocked and self-tested</span>' : `<span class="bad">${esc(st.keyError || 'Not loaded')}</span>`}</div></div></div>`);

      // Step 3: backup and product entry
      parts.push(`<div class="card"><h2>3. Back up the key and send the public key</h2>
        <ol>
          <li><b>Download the key backup</b> (encrypted with your passphrase) and copy it to two offline places, e.g. two USB drives kept apart. Keep the passphrase separately.</li>
          <li><b>Download <code>${esc(key.keyId)}.product-entry.json</code></b> (the public key only) and send it to Zentriq so the product trusts your licenses.
            Confirm the fingerprint above by phone when you hand it over.</li>
        </ol>
        <div class="row"><a class="btn" href="/setup/api/key/backup" download>Download key backup</a>
          <a class="btn" href="/setup/api/key/product-entry" download>Download product-entry.json</a></div>
        ${key.backedUp ? '<p class="ok mt">✓ Backup confirmed.</p>' : `<label class="check mt"><input type="checkbox" id="bk"> I have stored the key backup and its passphrase safely.</label>
        <div id="bmsg" class="msg hidden"></div><div class="row end"><button id="bgo">Confirm</button></div>`}</div>`);

      // Step 4: access
      const a = st.access || {};
      const pub = a.mode === 'public';
      parts.push(`<div class="card"><h2>4. Who can reach the server</h2>
        <label class="check"><input type="radio" name="am" value="local" ${pub ? '' : 'checked'}> This computer only — for testing (<code>${esc(st.localUrl)}</code>)</label>
        <label class="check"><input type="radio" name="am" value="public" ${pub ? 'checked' : ''}> Customers over the internet (HTTPS)</label>
        <div id="apub" class="mt ${pub ? '' : 'hidden'}">
          <div class="grid2"><div><label>Internet name of this server</label><input id="ah" value="${esc(a.hostname || 'license.zentriq.com')}"></div><div></div></div>
          <p class="small muted">Point the name's DNS record to this server and allow ports 80 and 443 to it (the Windows firewall rule is already added).</p>
          <label class="check"><input type="radio" name="ac" value="auto" ${a.certificate === 'pfx' ? '' : 'checked'}> Get a free certificate automatically (Let's Encrypt; renewed by the server)</label>
          <label class="check"><input type="radio" name="ac" value="pfx" ${a.certificate === 'pfx' ? 'checked' : ''}> Use my certificate (.pfx with private key)</label>
          <div id="aauto" class="mt"><div class="grid2"><div><label>Email for certificate notices</label><input id="ae" value="${esc(a.email || '')}"></div><div></div></div>
            <label class="check mt"><input type="checkbox" id="at"> I accept the <a href="https://letsencrypt.org/repository/" target="_blank" rel="noopener">Let's Encrypt subscriber agreement</a>.</label></div>
          <div id="apfx" class="mt hidden"><div class="grid2"><div><label>Certificate file (.pfx)</label><input id="af" type="file" accept=".pfx,.p12"></div>
            <div><label>Its password</label><input id="ap" type="password" autocomplete="off"></div></div>
            ${a.pfxExpires ? `<p class="small muted">Current certificate valid until ${esc(String(a.pfxExpires).slice(0, 10))}.</p>` : ''}</div>
        </div>
        ${(st.accessProblems || []).map((m) => `<div class="msg bad">${esc(m)}</div>`).join('')}
        <p class="small muted mt">Now: ${pub ? `public at <code>${esc(st.publicUrl)}</code>` : 'this computer only'}. The portal on this computer stays at <code>${esc(st.localUrl)}/admin</code> either way.</p>
        <div id="amsg" class="msg hidden"></div>
        <div class="row end"><button id="ago">Save</button></div></div>`);

      // Step 5: finish
      parts.push(st.complete
        ? `<div class="card"><h2>All set</h2><p>Open the portal to add customers and licenses.</p>
           <p class="small muted">Database backups run daily into <code>${esc(st.dataFolder)}\\backups</code>${st.lastBackup ? ` (last ${esc(String(st.lastBackup).replace('T', ' ').slice(0, 16))} UTC)` : ''}.</p>
           <div id="fmsg" class="msg hidden"></div>
           <div class="row end"><button id="bnow">Back up the database now</button><a class="btn primary" href="/admin/">Open the license portal</a></div></div>`
        : `<div class="card"><h2>5. Finish</h2><div id="fmsg" class="msg hidden"></div>
           <div class="row end"><button class="primary" id="fgo" ${key.backedUp ? '' : 'disabled'}>Finish and open the license portal</button></div></div>`);
    }
    view.innerHTML = parts.join('');
    wire();
  }

  function wire() {
    document.querySelectorAll('input[name=km]').forEach((r) => r.addEventListener('change', () => {
      const imp = document.querySelector('input[name=km]:checked').value === 'import';
      $('knew').classList.toggle('hidden', imp); $('kimp').classList.toggle('hidden', !imp);
      $('kgo').textContent = imp ? 'Use this key' : 'Create the key';
    }));
    if ($('kfile')) $('kfile').addEventListener('change', () => {
      const f = $('kfile').files[0];
      if (f && !val('kid2')) $('kid2').value = f.name.replace(/\.private\.pem$/i, '').replace(/\.pem$/i, '');
    });
    if ($('kgo')) $('kgo').addEventListener('click', (e) => act(e.target, 'kmsg', async () => {
      if (document.querySelector('input[name=km]:checked').value === 'import') {
        const pem = await readFile($('kfile'));
        await api('/key/import', { keyId: val('kid2'), privateKeyPem: pem, passphrase: $('kp3').value });
      } else {
        if ($('kp1').value !== $('kp2').value) throw new Error('The two passphrases differ.');
        await api('/key/new', { keyId: val('kid'), passphrase: $('kp1').value });
      }
      await load();
    }));
    if ($('bgo')) $('bgo').addEventListener('click', (e) => act(e.target, 'bmsg', async () => {
      if (!$('bk').checked) throw new Error('Tick the box once the backup is stored.');
      await api('/key/backed-up', {}); await load();
    }));
    const accessMode = () => document.querySelector('input[name=am]:checked').value;
    const certMode = () => (document.querySelector('input[name=ac]:checked') || {}).value;
    const sync = () => {
      if (!$('apub')) return;
      $('apub').classList.toggle('hidden', accessMode() !== 'public');
      $('aauto').classList.toggle('hidden', certMode() !== 'auto');
      $('apfx').classList.toggle('hidden', certMode() !== 'pfx');
    };
    document.querySelectorAll('input[name=am], input[name=ac]').forEach((r) => r.addEventListener('change', sync));
    sync();
    if ($('ago')) $('ago').addEventListener('click', (e) => act(e.target, 'amsg', async () => {
      const body = { mode: accessMode() };
      if (body.mode === 'public') {
        Object.assign(body, { hostname: val('ah'), certificate: certMode(), email: val('ae'), acceptTerms: $('at').checked });
        if (body.certificate === 'pfx') { body.pfxBase64 = await readFile($('af'), true); body.pfxPassword = $('ap').value; }
      }
      const r = await api('/access', body);
      if (r.restart && r.canRestart) { flash('amsg', 'Saved. The server restarts to apply it…', 'ok'); await waitForRestart(); }
      else if (r.restart) flash('amsg', 'Saved. Restart the license server to apply it.', 'ok');
      else flash('amsg', 'Saved.', 'ok');
      await load();
    }));
    if ($('fgo')) $('fgo').addEventListener('click', (e) => act(e.target, 'fmsg', async () => {
      const r = await api('/finish', {}); location.href = r.portal;
    }));
    if ($('bnow')) $('bnow').addEventListener('click', (e) => act(e.target, 'fmsg', async () => {
      const r = await api('/backup', {}); flash('fmsg', 'Backup written: ' + r.file, 'ok');
    }));
  }

  async function waitForRestart() {
    await new Promise((r) => setTimeout(r, 4000));
    for (let i = 0; i < 60; i++) {
      try { const r = await fetch('/health', { cache: 'no-store' }); if (r.ok) return; } catch { /* restarting */ }
      await new Promise((r) => setTimeout(r, 1500));
    }
  }

  function renderOwner() {
    view.innerHTML = `<div class="card"><h1>Set up the license server</h1>
      <p>Four short steps: your owner account, the signing key, its backup, and who can reach the server.</p></div>
      <div class="card"><h2>1. Owner account of the license portal</h2>
      <div class="grid2"><div><label>Your email (sign-in)</label><input id="oe" type="email" autocomplete="username"></div>
      <div><label>Your name</label><input id="on"></div>
      <div><label>Passphrase (12+ characters)</label><input id="op1" type="password" autocomplete="new-password"></div>
      <div><label>Repeat it</label><input id="op2" type="password" autocomplete="new-password"></div></div>
      <div id="omsg" class="msg hidden"></div>
      <div class="row end mt"><button class="primary" id="ogo">Create the owner account</button></div></div>`;
    $('ogo').addEventListener('click', (e) => act(e.target, 'omsg', async () => {
      if ($('op1').value !== $('op2').value) throw new Error('The two passphrases differ.');
      await api('/owner', { email: val('oe'), name: val('on'), password: $('op1').value });
      await load();
    }));
  }

  function renderSignIn() {
    view.innerHTML = `<div class="card"><h1>Sign in</h1><p class="muted">Sign in with the owner account to continue the setup.</p>
      <div class="grid2"><div><label>Email</label><input id="se" type="email" autocomplete="username"></div>
      <div><label>Passphrase</label><input id="sp" type="password" autocomplete="current-password"></div>
      <div><label>Authenticator code (if turned on)</label><input id="sc" inputmode="numeric" autocomplete="one-time-code"></div></div>
      <div id="smsg" class="msg hidden"></div><div class="row end mt"><button class="primary" id="sgo">Sign in</button></div></div>`;
    $('sgo').addEventListener('click', (e) => act(e.target, 'smsg', async () => {
      await call('/admin/api/login', { email: val('se'), password: $('sp').value, code: val('sc') || null });
      await load();
    }));
  }

  load().catch((e) => { view.innerHTML = `<div class="msg bad">${esc(e.message)}</div>`; });
})();
