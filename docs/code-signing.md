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

## Turn on signing in the GitHub build (DigiCert KeyLocker)

DigiCert delivers its code-signing certificates either on a USB token or in **DigiCert KeyLocker** (DigiCert's cloud
HSM). KeyLocker works on GitHub's build servers; the key never leaves DigiCert's HSM.

1. In **DigiCert ONE › KeyLocker** check that the code-signing certificate and its keypair are listed, and note the
   certificate's **SHA-1 fingerprint**.
2. In **DigiCert ONE › Admin profile** create an **API key** (shown once) and a **client authentication certificate**
   (download the `.p12` and keep its password). Restrict the API key to signing if your account offers it.
3. Base64-encode the `.p12` (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes('client.p12'))`).
4. In GitHub, *Settings › Secrets and variables › Actions*, add these **repository secrets**:

| Secret | Value |
|---|---|
| `SM_HOST` | the DigiCert ONE host shown in KeyLocker (e.g. `https://clientauth.one.digicert.com`) |
| `SM_API_KEY` | the API key |
| `SM_CLIENT_CERT_FILE_B64` | the base64 text of the client authentication `.p12` |
| `SM_CLIENT_CERT_PASSWORD` | its password |
| `SM_CODE_SIGNING_CERT_SHA1_HASH` | the code-signing certificate's SHA-1 fingerprint |

5. Push to `main`. The build installs DigiCert KeyLocker Tools, registers the KeyLocker KSP, syncs the certificate into
   the build user's certificate store (`smctl windows certsync`) and signs everything with `signtool /sha1 … /tr
   http://timestamp.digicert.com`; `release-manifest.json` records `"signingMode": "keylocker"`. The client
   authentication file is written to a temporary folder only for the build and removed at the end.

When both Azure Key Vault and KeyLocker secrets are set, Azure Key Vault is used.

## Signature details

Set two **repository variables** (*Settings › Secrets and variables › Actions › Variables*) so the signature details
show your company rather than a placeholder:

| Variable | Value |
|---|---|
| `ZENTRIQ_SIGN_DESCRIPTION` | product name, e.g. `Zentriq` |
| `ZENTRIQ_SIGN_URL` | your company web site (https) |

Without `ZENTRIQ_SIGN_URL` a signed build prints a warning.

## Protecting the signing secrets

* Signing secrets are only as safe as the branch that can use them. Turn on **branch protection** for `main`
  (pull requests, required checks, no force pushes) and restrict who can push.
* Better still, move the signing secrets into a GitHub **Environment** named `release` with *required reviewers*, so a
  signed release needs a person's approval.
* Rotate the API key and client authentication certificate when someone with access leaves, and at each yearly
  certificate renewal.

## Agent updates check the publisher

Once agents are signed, an agent accepts an update package only when it carries a valid signature of **the same
publisher** as the running agent (in addition to the SHA-256 the server sends). Keep the same company name on the
renewed certificate each year; a certificate issued to a different name stops automatic agent updates until the agent
is reinstalled.

## Linux agent packages

The `.deb`, `.rpm` and `.tar.gz` packages are not signed with a GPG key; the one-line installer checks their SHA-256
over the pinned server connection. Publishing them in a public repository would need a GPG signing key of your own.

## SmartScreen after signing

Signing immediately fixes "Unknown publisher" in the UAC prompt. SmartScreen reputation is earned per certificate:
the first downloads of a newly signed installer may still show "Windows protected your PC" (now naming your company)
until enough users have run it. Keep signing every release with the same certificate so reputation carries over.
You can also submit each release at https://www.microsoft.com/wdsi/filesubmission ("Software developer") to speed
this up. Installs pushed by SCCM/Intune/GPO or copied without the browser's "downloaded from the internet" mark are
not subject to SmartScreen.
