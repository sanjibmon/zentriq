# Code signing: removing "Unknown publisher"

Windows shows **"Unknown publisher"** (UAC prompt) and **"Windows protected your PC"** (SmartScreen) for unsigned
installers. Signing every Zentriq binary, `Zentriq.msi` and `Zentriq-Setup-x64.exe` with a publicly trusted
code-signing certificate replaces this with **"Verified publisher: <your company>"**. The build already does the
signing (including the Burn engine detach/reattach step) as soon as a certificate is configured.

## What to buy (2026 rules)

| Option | Fit for Zentriq (company in India) |
|---|---|
| **OV code-signing certificate** issued to your registered company (DigiCert, Sectigo, GlobalSign, SSL.com, …) | **Recommended.** Identity validation of the company (registration documents, phone verification) takes a few days. |
| EV certificate | Not needed: since 2024 EV no longer bypasses SmartScreen; it behaves like OV. |
| Azure Artifact Signing (Trusted Signing, ~USD 10/month) | Not available: organizations in the US, Canada, EU and UK only. |
| Self-signed certificate | Only for internal testing; customers would still see warnings. |

Since June 2023 the private key must be on certified hardware (USB token or HSM), and since March 2026 certificates
are valid for at most 460 days, so plan a yearly renewal. Order the certificate with one of:

* **Cloud HSM – recommended for automated builds:** Azure Key Vault **Premium** (HSM-backed key). Create the key/CSR
  in Key Vault (*Certificates → Generate/Import → non-integrated CA, key type RSA-HSM 3072*), have the CA issue the
  certificate against that CSR (CAs ask for the Key Vault key-attestation), then merge the issued certificate into Key
  Vault. Several CAs also offer their own cloud signing (DigiCert KeyLocker, SSL.com eSigner, GlobalSign); those work
  too but need their own CLI instead of AzureSignTool.
* **USB token** shipped by the CA: simplest, but signing must then run on the PC where the token is plugged in
  (`./package-windows.ps1 -SignCertThumbprint <thumbprint>`), not on GitHub's servers.

## Turn on signing in the GitHub build (Azure Key Vault)

1. In Microsoft Entra ID create an app registration (e.g. `zentriq-ci-signing`) and a client secret.
2. On the Key Vault, give that app the roles **Key Vault Crypto User** and **Key Vault Certificate User** (sign only; it cannot export the key).
3. In GitHub: *sanjibmon/zentriq → Settings → Secrets and variables → Actions → New repository secret*:

| Secret | Value |
|---|---|
| `ZENTRIQ_AKV_URL` | `https://<vault-name>.vault.azure.net` |
| `ZENTRIQ_AKV_CERT` | certificate name in the vault |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_CLIENT_ID` | app (client) ID |
| `AZURE_CLIENT_SECRET` | the client secret |

4. Push to `main` (or *Actions → windows-release → Run workflow*). The build signs all Zentriq `.exe/.dll` files, the
   custom-action DLL, `Zentriq.msi`, the Burn engine and `Zentriq-Setup-x64.exe` with an RFC 3161 timestamp, then
   verifies every signature; `release-manifest.json` records `"signed": true`.

## SmartScreen after signing

Signing immediately fixes "Unknown publisher" in the UAC prompt. SmartScreen reputation is earned per certificate:
the first downloads of a newly signed installer may still show "Windows protected your PC" (now naming your company)
until enough users have run it. Keep signing every release with the same certificate so reputation carries over.
You can also submit each release at https://www.microsoft.com/wdsi/filesubmission ("Software developer") to speed
this up. Installs pushed by SCCM/Intune/GPO or copied without the browser's "downloaded from the internet" mark are
not subject to SmartScreen.
