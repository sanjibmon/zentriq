import { useRef, useState } from 'react';
import { api, ApiError } from '../../api/client';
import type { LicensingStatus } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';

export function LicensingPage() {
  const { can, refresh } = useAuth();
  const st = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const d = st.data;

  async function exportRequest() {
    setMsg(null);
    try {
      await api.download('/licensing/activation-request', 'zentriq-activation-request.json');
      setMsg({ tone: 'ok', text: 'Activation request downloaded. Send it to Zentriq to receive your license file (.zlic).' });
    } catch (e) {
      setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) });
    }
  }

  async function importLicense(file: File) {
    setMsg(null);
    try {
      const document = await file.text();
      await api.post('/licensing/import', { document });
      setMsg({ tone: 'ok', text: 'License imported and activated.' });
      st.reload();
      await refresh();
    } catch (e) {
      setMsg({ tone: 'crit', text: e instanceof ApiError ? `License rejected: ${e.message}` : String(e) });
    } finally {
      if (fileRef.current) fileRef.current.value = '';
    }
  }

  return (
    <>
      <PageHead title="Licensing"
        actions={can('licensing.manage') && (
          <>
            <button className="btn" onClick={() => void exportRequest()}>Export activation request</button>
            <button className="btn primary" onClick={() => fileRef.current?.click()}>Import license…</button>
            <input ref={fileRef} type="file" accept=".zlic,.json,application/json" hidden onChange={(e) => e.target.files?.[0] && void importLicense(e.target.files[0])} />
          </>
        )} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
      <ErrorBox error={st.error} />
      {!d ? <Loading /> : (
        <>
          <Kpis items={[
            { label: 'State', value: <Pill tone={statusTone(d.mode)}>{fmt.label(d.mode)}</Pill> },
            { label: d.mode.startsWith('Trial') ? 'Trial ends' : 'Expires', value: fmt.day(d.expiresAt) },
            { label: 'Days remaining', value: d.daysRemaining ?? '—' },
            { label: 'Modules', value: d.modules.includes('*') ? 'All' : d.modules.length },
          ]} />
          <div className="grid-2">
            <Panel title="Workload usage" sub="Active assets seen in the last 30 days" flush>
              <DataTable rows={d.usage.map((u) => ({ ...u, id: u.workload }))} columns={[
                { key: 'w', label: 'Workload', render: (r) => (r.workload === 'devices' ? <b>All devices (total)</b> : fmt.label(r.workload)) },
                { key: 'l', label: 'Entitled', render: (r) => (r.limit == null ? 'Unlimited' : r.limit.toLocaleString()) },
                { key: 'b', label: 'Used', render: (r) => r.billable.toLocaleString() },
                { key: 'a', label: 'Available', render: (r) => (r.limit == null ? '—' : Math.max(0, r.limit - r.billable).toLocaleString()) },
                { key: 'p', label: 'Utilization', render: (r) => r.limit ? <div className="progress" title={`${Math.round((r.billable / r.limit) * 100)}%`}><i style={{ width: `${Math.min(100, (r.billable / r.limit) * 100)}%` }} /></div> : '—' },
              ]} />
            </Panel>
            <Panel title="Details">
              <dl className="dl2">
                <dt>Summary</dt><dd>{d.summary}</dd>
                <dt>Installation ID</dt><dd className="mono">{d.installationId ?? '—'}</dd>
                {d.trial && <><dt>Trial started</dt><dd>{fmt.date(d.trial.startedAt)}</dd><dt>Trial ends</dt><dd>{fmt.date(d.trial.expiresAt)}</dd></>}
                {d.license && <>
                  <dt>License</dt><dd>{d.license.licenseId}</dd>
                  <dt>Customer</dt><dd>{d.license.customerName}</dd>
                  <dt>Valid until</dt><dd>{fmt.day(d.license.expiresAt)} (+{d.license.graceDays} days grace)</dd>
                </>}
                {d.mode.startsWith('Trial') && <><dt>Trial edition</dt><dd>All modules, up to {d.limits.devices ?? 30} devices in total</dd></>}
              </dl>
            </Panel>
            {d.licensee && (
              <Panel title="Licensed to">
                <dl className="dl2">
                  <dt>Company</dt><dd><b>{d.licensee.company}</b></dd>
                  {d.licensee.contactName && <><dt>Contact</dt><dd>{d.licensee.contactName}</dd></>}
                  {d.licensee.contactEmail && <><dt>Email</dt><dd>{d.licensee.contactEmail}</dd></>}
                  {d.licensee.contactPhone && <><dt>Phone</dt><dd>{d.licensee.contactPhone}</dd></>}
                  {d.licensee.address && <><dt>Address</dt><dd>{d.licensee.address}{d.licensee.country ? `, ${d.licensee.country}` : ''}</dd></>}
                  {d.licensee.taxId && <><dt>Tax ID</dt><dd className="mono">{d.licensee.taxId}</dd></>}
                </dl>
                <small className="muted">From the signed license. Edit your company profile in System Settings.</small>
              </Panel>
            )}
          </div>
        </>
      )}
    </>
  );
}
