import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Loading, Panel, Pill } from '../../components/ui';
import { DetailsDialog, StageDialog, useLcMeta, type StageMode } from './LifecycleDialogs';
import { CONTRACT_KIND, KIND_LABEL, money, STAGE_ORDER, STAGE_TONE, stageLabel, statusLabel, type LcDetail, type LcMeta } from './types';

function KV({ items }: { items: [string, ReactNode][] }) {
  return <dl className="kv-grid">{items.map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v ?? '—'}</dd></div>)}</dl>;
}

/** The lifecycle path drawn as steps, the current stage highlighted. */
export function StageTrack({ stage, meta }: { stage: string; meta: LcMeta }) {
  const main = STAGE_ORDER.filter((s) => s !== 'missing' && s !== 'in_maintenance');
  const at = main.indexOf(stage as (typeof main)[number]);
  const side = stage === 'missing' || stage === 'in_maintenance';
  return (
    <ol className="lc-track" aria-label="Lifecycle stage">
      {main.map((s, i) => (
        <li key={s} className={`${s === stage ? 'on' : ''}${!side && at >= 0 && i < at ? ' done' : ''}${side && s === 'in_use' ? ' side' : ''}`}>
          <span className="dot" />{stageLabel(meta, s)}
          {side && s === 'in_use' && <em>{stageLabel(meta, stage)}</em>}
        </li>
      ))}
    </ol>
  );
}

/** Lifecycle of one IT asset: stage, custody, purchase and depreciation, warranty, contracts, disposal and history. */
export function LifecyclePanel({ assetId, onMessage }: { assetId: string; onMessage?: (m: string) => void }) {
  const { can } = useAuth();
  const canEdit = can('assets.manage');
  const [tick, setTick] = useState(0);
  const detail = useApi(() => api.get<LcDetail>(`/lifecycle/assets/${assetId}`), [assetId, tick]);
  const meta = useLcMeta();
  const [dialog, setDialog] = useState<StageMode | 'details' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [tagBusy, setTagBusy] = useState(false);
  const done = (m: string) => { setDialog(null); setMsg(m); onMessage?.(m); setTick((x) => x + 1); };
  if (detail.error) return <ErrorBox error={detail.error} />;
  const d = detail.data;
  const m = meta.data;
  if (!d || !m) return <Loading />;
  const a = d.asset;
  const cur = a.currency ?? m.currency;
  const active = a.stage !== 'disposed';
  const canAssign = d.next.includes('in_use') || a.stage === 'in_use';
  const canReturn = (a.stage === 'in_use' || a.stage === 'in_maintenance') && d.next.includes('in_stock');
  async function tag() {
    setTagBusy(true);
    try { const r = await api.post<{ tags: Record<string, string> }>('/lifecycle/tags', { assetIds: [assetId] }); done(`Asset tag ${r.tags[assetId] ?? ''} given.`); }
    catch (e) { setMsg(e instanceof Error ? e.message : String(e)); } finally { setTagBusy(false); }
  }
  return (
    <>
      {msg && <Callout icon="info">{msg}</Callout>}
      {a.awaitingFirstSighting && <Callout tone="info" icon="info">Added to the register before Zentriq saw it. It moves to <b>In use</b> by itself when an agent or a scan first finds a device with serial number <span className="mono">{a.serial ?? '—'}</span>.</Callout>}
      {a.stage === 'missing' && a.status === 'not_seen' && <Callout tone="warn" icon="alert">Not seen by Zentriq for {m.notSeenDays}+ days. Check with the custodian, then mark it In use, Lost or Stolen. It goes back to In use by itself when it is seen again.</Callout>}
      {d.events.some((e) => e.kind === 'seen_again') && (a.stage === 'retired' || a.stage === 'disposed') && <Callout tone="crit" icon="alert">This {stageLabel(m, a.stage).toLowerCase()} device was seen on the network again ({fmt.date(d.events.find((e) => e.kind === 'seen_again')?.at)}). Check where it is.</Callout>}
      <Panel title="Lifecycle" sub={<>{stageLabel(m, a.stage)} · {statusLabel(m, a.status)} since {fmt.day(a.stageChangedAt)}</>}
        right={canEdit && <div className="acts" style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          {active && canAssign && <button className="btn sm primary" onClick={() => setDialog('assign')}><Icon name="users" size={13} /> {a.assignedTo ? 'Reassign' : 'Assign'}</button>}
          {active && canReturn && <button className="btn sm" onClick={() => setDialog('return')}><Icon name="down" size={13} /> Check in</button>}
          {active && d.next.length > 0 && <button className="btn sm" onClick={() => setDialog('stage')}><Icon name="cycle" size={13} /> Change stage</button>}
          {d.next.includes('disposed') && <button className="btn sm danger" onClick={() => setDialog('dispose')}><Icon name="trash" size={13} /> Dispose</button>}
          <button className="btn sm" onClick={() => setDialog('details')}><Icon name="edit" size={13} /> Details</button>
        </div>}>
        <StageTrack stage={a.stage} meta={m} />
        <div className="grid-2" style={{ marginTop: 12 }}>
          <KV items={[
            ['Asset tag', a.assetTag ? <span className="mono"><b>{a.assetTag}</b></span> : canEdit ? <button className="btn sm" disabled={tagBusy} onClick={() => void tag()}>Give the next tag</button> : '—'],
            ['Stage', <><Pill tone={STAGE_TONE[a.stage]}>{stageLabel(m, a.stage)}</Pill> {statusLabel(m, a.status)}</>],
            ['Reason', a.stageReason],
            ['Assigned to', a.assignedTo ? <><Link to={`/lifecycle?tab=people&person=${encodeURIComponent(a.assignedTo)}`}>{a.assignedTo}</Link>{a.assignedEmail ? <span className="faint"> · {a.assignedEmail}</span> : null}</> : null],
            ['Since', a.assignedAt ? fmt.day(a.assignedAt) : null],
            ...(a.expectedReturn ? [['Loan ends', <span style={{ color: a.expectedReturn < new Date().toISOString().slice(0, 10) ? 'var(--crit)' : undefined }}>{fmt.day(a.expectedReturn)}</span>] as [string, ReactNode]] : []),
            ['Department / location', [a.department, a.location].filter(Boolean).join(' · ') || null],
            ['Class', a.classLabel], ['Age', a.ageMonths != null ? `${Math.floor(a.ageMonths / 12)} y ${a.ageMonths % 12} m` : null],
            ['Refresh', a.refreshYears > 0 ? <>{a.refreshDue ? <Pill tone="warn">Due</Pill> : 'Not due'} <span className="faint">(policy {a.refreshYears} years)</span></> : 'No refresh policy'],
            ...(d.osEndOfSupport ? [['Operating system', <><Pill tone="warn">Out of support</Pill> {a.osName}</>] as [string, ReactNode]] : []),
          ]} />
          <KV items={[
            ['Acquisition', fmt.label(a.acquisition)],
            ['Purchased', [a.purchaseDate ? fmt.day(a.purchaseDate) : null, a.vendor].filter(Boolean).join(' · ') || null],
            ['Cost', money(a.purchaseCost, cur)],
            ['Book value today', a.purchaseCost != null ? <><b>{money(a.bookValue, cur)}</b> <span className="faint">{m.depreciationMethods.find((x) => x.id === a.depreciationMethod)?.label}{a.usefulLifeMonths ? `, ${a.usefulLifeMonths} months` : ''}{a.depreciationCustom ? ' (own)' : ''}</span></> : <span className="muted">Enter the cost (Details)</span>],
            ['Invoice / PO', [a.invoiceNumber, a.poNumber].filter(Boolean).join(' · ') || null],
            ...(a.lessor || a.leaseEnd ? [['Lease', `${a.lessor ?? ''} ${a.leaseEnd ? `until ${fmt.day(a.leaseEnd)}` : ''} ${a.leaseMonthlyCost != null ? `· ${money(a.leaseMonthlyCost, cur)}/month` : ''}`] as [string, ReactNode]] : []),
            ['Warranty', a.warrantyEnd ? <>{a.warrantyDays != null && a.warrantyDays < 0 ? <Pill tone="crit">Expired</Pill> : a.warrantyDays != null && a.warrantyDays <= 90 ? <Pill tone="warn">{a.warrantyDays} days left</Pill> : <Pill tone="ok">Active</Pill>} until {fmt.day(a.warrantyEnd)} <span className="faint">{[a.warrantyProvider, a.warrantyType].filter(Boolean).join(' · ')}</span></> : <span className="muted">Not recorded</span>],
            ['Contracts', d.contracts.length ? d.contracts.map((c) => `${c.name} (${CONTRACT_KIND[c.kind] ?? c.kind}${c.endDate ? `, until ${c.endDate}` : ''})`).join('; ') : null],
            ['Notes', a.notes],
          ]} />
        </div>
        {a.stage === 'disposed' && <div style={{ marginTop: 10 }}><KV items={[
          ['Disposed on', fmt.day(a.disposedOn)], ['Method', statusLabel(m, a.disposalMethod)], ['Vendor', a.disposalVendor], ['Recovered value', money(a.disposalValue, cur)],
          ['Data sanitisation', m.sanitizationMethods.find((x) => x.id === a.sanitizationMethod)?.label ?? a.sanitizationMethod],
          ['Data removed by', [a.sanitizedBy, a.sanitizedOn ? fmt.day(a.sanitizedOn) : null].filter(Boolean).join(' · ') || null], ['Certificate', a.certificateRef],
        ]} /></div>}
      </Panel>
      {d.schedule && d.schedule.length > 0 && <Panel title="Depreciation schedule" sub={`${m.depreciationMethods.find((x) => x.id === a.depreciationMethod)?.label ?? ''} · value at the end of each year`} flush>
        <DataTable rows={d.schedule.map((r) => ({ ...r, id: r.year }))} columns={[
          { key: 'y', label: 'Year', render: (r) => r.year },
          { key: 'o', label: 'Opening', render: (r) => money(r.opening, cur) },
          { key: 'd', label: 'Depreciation', render: (r) => money(r.depreciation, cur) },
          { key: 'c', label: 'Closing book value', render: (r) => <b>{money(r.closing, cur)}</b> },
        ]} />
      </Panel>}
      <Panel title="Lifecycle history" flush>
        <DataTable rows={d.events} empty="No changes yet." columns={[
          { key: 'at', label: 'When', render: (e) => fmt.date(e.at) },
          { key: 'k', label: 'Change', render: (e) => <>{KIND_LABEL[e.kind] ?? e.kind}{e.toStage && e.fromStage !== e.toStage ? <span className="faint"> · {stageLabel(m, e.fromStage)} → {stageLabel(m, e.toStage)}</span> : e.toStatus && e.toStatus !== e.fromStatus ? <span className="faint"> · {statusLabel(m, e.toStatus)}</span> : null}</> },
          { key: 'p', label: 'Person', render: (e) => e.person ?? '—' },
          { key: 'r', label: 'Reason', className: 'wrap', render: (e) => e.reason ?? '—' },
          { key: 'b', label: 'By', render: (e) => e.actor ?? '—' },
        ]} />
      </Panel>
      {dialog === 'details' && <DetailsDialog asset={a} meta={m} onClose={() => setDialog(null)} onDone={done} />}
      {dialog && dialog !== 'details' && <StageDialog asset={a} meta={m} mode={dialog} onClose={() => setDialog(null)} onDone={done} />}
    </>
  );
}
