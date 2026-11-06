# Console sign-in with Active Directory and Microsoft Entra ID

From build 1.1.1.113 people can sign in to the Zentriq console with their **Active Directory** account (LDAP / LDAPS)
or with **Microsoft Entra ID** (single sign-on, *Sign In With Microsoft*). Directory groups are mapped to Zentriq
roles. **Local Zentriq accounts** (e-mail and password, two-step verification, password reset by e-mail) keep working.

Console: *Users, Roles & Groups › Sign-in* (permission *users.manage*).

## Active Directory (LDAP / LDAPS)

| Setting | |
|---|---|
| Domain controller or domain | DNS name (or address) of a DC or of the domain |
| Port / LDAPS | 636 with TLS (recommended; the DC's certificate is checked) or 389 with Kerberos / NTLM signing and sealing |
| Domain for plain user names | `CONTOSO` or `contoso.com`: *jane* signs in as `CONTOSO\jane` |
| Search base | where accounts and groups are read (empty: the whole domain) |
| Create the account at the first sign-in | for members of mapped groups |

* Zentriq binds **as the person** with the password they typed (no service account is stored). An empty password is
  refused before anything is sent (it would be an anonymous bind).
* Groups are read with nesting (`member:1.2.840.113556.1.4.1941:`), by distinguished name.
* Disabled AD accounts cannot sign in; failed passwords count towards the Zentriq lockout.
* **Test with an account** (Sign-in tab) signs in without changing anything and lists the person's groups with a
  *Map to a Role* button.
* On the sign-in page the person chooses **Directory Account** and enters `DOMAIN\name`, `name@domain` or `name`.
  Accounts that already sign in with Active Directory are recognised by their e-mail address too.
* Zentriq two-step verification applies to directory accounts like local ones (and *MFA required* too).

## Microsoft Entra ID (OpenID Connect)

1. Entra admin center › *App registrations › New registration*, single tenant, platform **Web**, redirect URI as shown
   on the Sign-in tab: `https://<console address>/api/v1/auth/oidc/callback` (one per address you use).
2. *Token configuration › Add groups claim* (security groups, group ID) — or define **app roles** and assign them.
3. *Certificates & secrets › New client secret*; enter its value with the Directory (tenant) ID and the
   Application (client) ID.
4. People in more than 200 groups: add the Microsoft Graph **application** permission `GroupMember.Read.All` (admin
   consent) so Zentriq can read their groups.

* The sign-in page shows **Sign In With Microsoft**. Zentriq uses the authorization code flow with PKCE, a state
  value bound to the browser and a nonce; the ID token's RS256 signature (the tenant's published keys), issuer,
  tenant, audience and lifetime are checked. Only accounts of the configured tenant are accepted.
* Multi-factor authentication is Entra ID's (Conditional Access, security defaults); Zentriq does not ask for its own code.

## Groups and roles

* *Directory Groups and Roles* maps an AD group (distinguished name) or an Entra group (object ID) or app role
  (value) to a Zentriq role. Members get the role at every sign-in; when they leave the group they lose it at their
  next sign-in, and when a mapping is removed the role is taken away from everyone who had it through directory
  groups at once (their sessions reload their permissions).
* Roles given in Zentriq (directly or through a Zentriq group) add to the directory roles.
* A directory user without any role cannot sign in. Users who are not in a mapped group are not created.
* A directory account is never linked to a **local** account with the same e-mail address (the sign-in is refused
  with an explanation); an administrator can delete the local account so the directory account is created.
* *Users* shows how each person signs in (Local, Active Directory, Microsoft) and the roles from directory groups.

## Audit

`auth.login` (with the method `local`, `ldap` or `entra`, and the reason of failures), `user.create` for accounts
created at the first sign-in, `auth.signin.ldap`, `auth.signin.entra`, `auth.signin.test`, `auth.signin.mapping`,
`auth.signin.mapping_removed`.

## API

`GET /api/v1/auth/options` (anonymous: which methods are on), `POST /auth/login` (`directory: true` for an AD
account), `GET /auth/oidc/start`, `GET /auth/oidc/callback`; with *users.manage*: `GET /auth/sign-in`,
`PUT /auth/sign-in/ldap`, `PUT /auth/sign-in/entra`, `POST /auth/sign-in/ldap/test`, `POST /auth/sign-in/mappings`,
`DELETE /auth/sign-in/mappings/{id}`.

## Tests

Unit tests (LDAP filter escaping and user names, no anonymous bind, ID token checks: signature with a foreign key,
nonce, tenant, audience, refused code, PKCE), PostgreSQL test (account created for a mapped group, roles replaced at
each sign-in and removed when the person left, no account without a mapped group, local account not taken over) and
the Windows acceptance test T91. First released in build 1.1.1.113.
