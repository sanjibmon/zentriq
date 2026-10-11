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
      <PageHead title="Licensing" desc="Trial status, workload usage and offline license activation. No internet connection is required."
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
                { key: 'w', label: 'Workload', render: (r) => fmt.label(r.workload) },
                { key: 'b', label: 'Billable', render: (r) => r.billable.toLocaleString() },
                { key: 'l', label: 'Entitled', render: (r) => (r.limit == null ? 'Unlimited' : r.limit.toLocaleString()) },
                { key: 'p', label: 'Use', render: (r) => r.limit ? <div className="progress" title={`${Math.round((r.billable / r.limit) * 100)}%`}><i style={{ width: `${Math.min(100, (r.billable / r.limit) * 100)}%` }} /></div> : '—' },
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
              </dl>
            </Panel>
          </div>
        </>
      )}
    </>
  );
}
