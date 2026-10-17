import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { CopyBox } from '../../components/CopyBox';
import { Icon } from '../../components/Icon';
import { Panel, Pill } from '../../components/ui';

interface TrustInfo {
  mode: 'local-ca' | 'customer' | 'self-signed';
  ca: { subject: string; thumbprint: string; expiresAt: string } | null;
  certificate: { subject: string; issuer: string; thumbprint: string; expiresAt: string; names: string[]; addresses: string[] } | null;
}

/**
 * How technician PCs come to trust this server: the Zentriq Local CA, installed once, covers the console and remote
 * support at every name and address the server has now or gets later (the certificate is re-issued automatically).
 */
export function TrustPanel() {
  const trust = useApi(() => api.get<TrustInfo>('/trust'), []);
  const t = trust.data;
  return (
    <Panel title="Trust this server">
      {!t ? <p className="muted">Loading…</p> : t.mode === 'customer' ? (
        <p style={{ margin: 0 }}>The console uses your organization's certificate (<span className="mono">{t.certificate?.subject}</span>). Remote support uses the same one when its key can be exported; devices and browsers that trust your CA need nothing else.</p>
      ) : (
        <div className="form-v">
          <p style={{ margin: 0 }}>
            Zentriq issues its own certificate from the <b>Zentriq Local CA</b> for every name and IP address this server has, and issues a new one by itself when the
            server gets a new address. Install the CA <b>once</b> on technician PCs and the console and remote sessions open without certificate warnings, whatever
            the server's address.
          </p>
          <div><a className="btn" href="/api/v1/trust/ca.cer" download="Zentriq-Local-CA.cer"><Icon name="down" size={13} /> Download Zentriq Local CA certificate</a></div>
          <CopyBox label="Install on a PC (PowerShell as administrator)" value="Import-Certificate -FilePath .\Zentriq-Local-CA.cer -CertStoreLocation Cert:\LocalMachine\Root" />
          <small className="muted">For many PCs, publish it with Group Policy: Computer Configuration › Policies › Windows Settings › Security Settings › Public Key Policies › Trusted Root Certification Authorities › Import. Firefox: enable <span className="mono">security.enterprise_roots.enabled</span>.</small>
          {t.certificate && (
            <dl className="kv-grid" style={{ margin: 0 }}>
              <div><dt>Covers names</dt><dd className="mono">{t.certificate.names.join(', ') || '—'}</dd></div>
              <div><dt>Covers addresses</dt><dd className="mono">{t.certificate.addresses.join(', ') || '—'}</dd></div>
              <div><dt>Certificate</dt><dd>{t.mode === 'local-ca' ? <Pill tone="ok">From Zentriq Local CA</Pill> : <Pill tone="warn">Self-signed — run Repair</Pill>} <span className="faint">valid to {new Date(t.certificate.expiresAt).toLocaleDateString()}</span></dd></div>
              {t.ca && <div><dt>CA thumbprint</dt><dd className="mono" style={{ fontSize: 12 }}>{t.ca.thumbprint}</dd></div>}
            </dl>
          )}
        </div>
      )}
    </Panel>
  );
}
