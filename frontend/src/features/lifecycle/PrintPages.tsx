import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CompanyLogo } from '../../components/CompanyLogo';
import { Icon } from '../../components/Icon';
import { ErrorBox, fmt, Loading } from '../../components/ui';
import type { LcAsset } from './types';

interface Label { id: string; tag: string | null; name: string | null; serial: string | null; model: string | null; class: string | null; url: string; qrSvg: string }

/** Asset labels with a QR code that opens the asset in Zentriq (print on A4 label sheets, 3 × 8). */
export function LabelsPage() {
  const { query } = useRouter();
  const ids = query.get('ids') ?? '';
  const labels = useApi(() => api.get<Label[]>('/lifecycle/labels', { ids }), [ids]);
  return (
    <div className="print-page">
      <div className="no-print toolbar-row">
        <Link className="btn" to="/lifecycle?tab=assets">← Asset Lifecycle</Link>
        <span className="muted">{labels.data?.length ?? 0} label(s). Print on A4 label sheets (3 × 8, 70 × 37 mm) or plain paper; scanning the code opens the asset.</span>
        <span style={{ marginLeft: 'auto' }} />
        <button className="btn primary" onClick={() => window.print()}><Icon name="report" size={13} /> Print</button>
      </div>
      <ErrorBox error={labels.error} />
      {!labels.data ? <Loading /> : (
        <div className="label-sheet">
          {labels.data.map((l) => (
            <div key={l.id} className="asset-label">
              <div className="qr" dangerouslySetInnerHTML={{ __html: l.qrSvg }} />
              <div className="txt">
                <div className="tag">{l.tag ?? 'NO TAG'}</div>
                <div className="nm">{l.name ?? l.model ?? ''}</div>
                {l.serial && <div className="sn">S/N {l.serial}</div>}
                <div className="own">Property of the IT department</div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** Handover (issue) or return form for everything one person holds, to print and sign. */
export function HandoverPage() {
  const { query } = useRouter();
  const { me } = useAuth();
  const person = query.get('person') ?? '';
  const kind = query.get('kind') === 'return' ? 'return' : 'handover';
  const h = useApi(() => api.get<{ assets: LcAsset[]; accessories: { item: string; category: string; quantity: number; serials: string[] | null }[] }>('/lifecycle/people/holdings', { person }), [person]);
  const today = new Date().toLocaleDateString(undefined, { dateStyle: 'long' });
  return (
    <div className="print-page handover">
      <div className="no-print toolbar-row">
        <Link className="btn" to={`/lifecycle?tab=people&person=${encodeURIComponent(person)}`}>← {person}</Link>
        <span style={{ marginLeft: 'auto' }} />
        <button className="btn primary" onClick={() => window.print()}><Icon name="report" size={13} /> Print / save as PDF</button>
      </div>
      <ErrorBox error={h.error} />
      {!h.data ? <Loading /> : <article className="form-doc">
        <header>
          <CompanyLogo height={36} />
          <div>
            <h1>{kind === 'return' ? 'IT asset return form' : 'IT asset handover form'}</h1>
            <p>{today}</p>
          </div>
        </header>
        <table className="doc-kv"><tbody>
          <tr><th>Employee</th><td>{person}</td><th>E-mail</th><td>{h.data.assets.find((a) => a.assignedEmail)?.assignedEmail ?? ''}</td></tr>
          <tr><th>Department</th><td>{h.data.assets.find((a) => a.department)?.department ?? ''}</td><th>{kind === 'return' ? 'Received by (IT)' : 'Issued by (IT)'}</th><td>{me?.displayName}</td></tr>
        </tbody></table>
        <h2>IT assets</h2>
        <table className="doc-t">
          <thead><tr><th>#</th><th>Asset tag</th><th>Device</th><th>Make / model</th><th>Serial number</th><th>Since</th>{kind === 'return' ? <th>Condition on return</th> : <th>Condition</th>}</tr></thead>
          <tbody>{h.data.assets.length === 0 ? <tr><td colSpan={7} className="muted">None</td></tr> : h.data.assets.map((a, i) => (
            <tr key={a.id}><td>{i + 1}</td><td className="mono">{a.assetTag ?? ''}</td><td>{a.name}</td><td>{[a.manufacturer, a.model].filter(Boolean).join(' ')}</td>
              <td className="mono">{a.serial ?? ''}</td><td>{fmt.day(a.assignedAt)}{a.expectedReturn ? ` (loan until ${fmt.day(a.expectedReturn)})` : ''}</td><td style={{ minWidth: 110 }} /></tr>))}
          </tbody>
        </table>
        <h2>IT accessories</h2>
        <table className="doc-t">
          <thead><tr><th>#</th><th>Accessory</th><th>Category</th><th>Quantity</th><th>Serial numbers</th><th>Condition</th></tr></thead>
          <tbody>{h.data.accessories.length === 0 ? <tr><td colSpan={6} className="muted">None</td></tr> : h.data.accessories.map((a, i) => (
            <tr key={a.item}><td>{i + 1}</td><td>{a.item}</td><td>{a.category}</td><td>{a.quantity}</td><td className="mono">{(a.serials ?? []).join(', ')}</td><td style={{ minWidth: 110 }} /></tr>))}
          </tbody>
        </table>
        <section className="terms">
          {kind === 'return'
            ? <p>The items above were returned to the IT department on the date shown. Data on returned devices is removed before they are given to anyone else. Missing or damaged items are noted in the condition column.</p>
            : <p>I have received the items above in working condition. I will use them for work, keep them safe, not install unauthorised software, report loss, theft or damage to IT at once, and return them when asked or when I leave the organisation.</p>}
        </section>
        <div className="signs">
          <div><span>Employee signature</span></div>
          <div><span>IT department signature</span></div>
          <div><span>Date</span></div>
        </div>
      </article>}
    </div>
  );
}
