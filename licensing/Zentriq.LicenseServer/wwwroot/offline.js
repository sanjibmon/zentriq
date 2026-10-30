(() => {
  const $ = (id) => document.getElementById(id);
  let request = null;
  $('file').addEventListener('change', async (e) => {
    request = null; $('fileinfo').textContent = '';
    const f = e.target.files[0];
    if (!f) return;
    if (f.size > 65536) { $('fileinfo').textContent = 'This file is too large to be an activation request.'; return; }
    try {
      const j = JSON.parse(await f.text());
      if (j.format !== 'zentriq-activation-request') throw new Error();
      request = j;
      $('fileinfo').textContent = `Server ${j.machineName || '?'} · ${j.organization || ''} · Zentriq ${j.productVersion || ''}`;
    } catch { $('fileinfo').textContent = 'This is not a Zentriq activation request file.'; }
  });
  $('f').addEventListener('submit', async (e) => {
    e.preventDefault();
    const out = $('out'); out.className = 'msg hidden'; $('done').classList.add('hidden');
    if (!request) { out.textContent = 'Choose the activation request file.'; out.className = 'msg bad'; return; }
    $('go').disabled = true;
    try {
      const r = await fetch('/api/v1/offline/activate', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ licenseKey: $('key').value, request }) });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { out.textContent = j.title || (r.status === 429 ? 'Too many attempts. Wait a minute and try again.' : `Activation failed (${r.status}).`); out.className = 'msg bad'; return; }
      const url = URL.createObjectURL(new Blob([j.license], { type: 'application/json' }));
      $('dl').href = url; $('dl').download = j.fileName;
      $('summary').textContent = `${j.customer} · ${j.edition === 'advanced' ? 'Advanced' : 'Standard'} edition. ${j.message}`;
      $('done').classList.remove('hidden');
    } catch { out.textContent = 'The license server could not be reached.'; out.className = 'msg bad'; }
    finally { $('go').disabled = false; }
  });
})();
