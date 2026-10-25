import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { Callout } from '../../components/ui';
import { errorText, WEEKDAYS, type ReportFormat, type ReportSchedule } from './types';

interface Target { id: string; name: string; schedule: ReportSchedule | null; recipients: string[]; format: ReportFormat }

/** Schedule a saved report for e-mail delivery (daily / weekly / monthly, server time zone). */
export function ScheduleDialog({ report, onClose, onSaved }: { report: Target; onClose: () => void; onSaved: () => void }) {
  const s = report.schedule;
  const [enabled, setEnabled] = useState(s?.enabled ?? true);
  const [frequency, setFrequency] = useState<ReportSchedule['frequency']>(s?.frequency ?? 'weekly');
  const [hour, setHour] = useState(s?.hour ?? 7);
  const [weekday, setWeekday] = useState(s?.weekday ?? 1);
  const [day, setDay] = useState(s?.day ?? 1);
  const [recipients, setRecipients] = useState(report.recipients.join(', '));
  const [format, setFormat] = useState(report.format);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await api.put(`/reports/${report.id}/schedule`, {
        schedule: { enabled, frequency, hour, weekday, day },
        recipients: recipients.split(/[,;\s]+/).map((x) => x.trim()).filter(Boolean),
        format,
      });
      onSaved();
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="sch-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="sch-title">Schedule "{report.name}"</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label className="row-check"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Deliver this report by e-mail on a schedule</label>
          <small className="muted">Each delivery covers the report's time period. Choose a moving period in the report (for example <i>Last week</i> or <i>Last month</i>) so every e-mail covers the latest period.</small>
          <div className="form-grid">
            <label>Frequency
              <select className="in" value={frequency} onChange={(e) => setFrequency(e.target.value as ReportSchedule['frequency'])} disabled={!enabled}>
                <option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option>
              </select>
            </label>
            {frequency === 'weekly' && <label>Day
              <select className="in" value={weekday} onChange={(e) => setWeekday(+e.target.value)} disabled={!enabled}>{WEEKDAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}</select>
            </label>}
            {frequency === 'monthly' && <label>Day of month
              <select className="in" value={day} onChange={(e) => setDay(+e.target.value)} disabled={!enabled}>{Array.from({ length: 28 }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select>
            </label>}
            <label>Time (server time)
              <select className="in" value={hour} onChange={(e) => setHour(+e.target.value)} disabled={!enabled}>{Array.from({ length: 24 }, (_, i) => <option key={i} value={i}>{String(i).padStart(2, '0')}:00</option>)}</select>
            </label>
            <label>Format
              <select className="in" value={format} onChange={(e) => setFormat(e.target.value as ReportFormat)}><option value="xlsx">Excel (.xlsx)</option><option value="pdf">PDF</option><option value="csv">CSV</option></select>
            </label>
          </div>
          <label>Recipients
            <textarea className="in" rows={3} value={recipients} onChange={(e) => setRecipients(e.target.value)} placeholder="it-team@example.com, manager@example.com" />
            <small className="muted">E-mail addresses separated by commas. Delivery uses the SMTP server in System Settings; the file is also kept in Run history for 30 days.</small>
          </label>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save schedule'}</button>
        </footer>
      </form>
    </div>
  );
}
