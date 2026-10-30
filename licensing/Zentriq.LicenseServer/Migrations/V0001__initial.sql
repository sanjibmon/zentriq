-- Zentriq license server: customers, licenses (keys stored as hashes), activations, issued license files, audit.
CREATE SCHEMA IF NOT EXISTS zls;

CREATE TABLE zls.admins (
    id            uuid PRIMARY KEY,
    email         text NOT NULL,
    name          text NOT NULL,
    pw_hash       text NOT NULL,
    totp_secret   bytea NULL,
    is_owner      boolean NOT NULL DEFAULT false,
    disabled      boolean NOT NULL DEFAULT false,
    failed_logins int NOT NULL DEFAULT 0,
    locked_until  timestamptz NULL,
    last_login_at timestamptz NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX admins_email_ux ON zls.admins (lower(email));

CREATE TABLE zls.customers (
    id            uuid PRIMARY KEY,
    company       text NOT NULL,
    contact_name  text NULL,
    contact_email text NULL,
    contact_phone text NULL,
    address       text NULL,
    country       text NULL,
    tax_id        text NULL,
    notes         text NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX customers_company_ux ON zls.customers (lower(company));

CREATE TABLE zls.licenses (
    id              text PRIMARY KEY,                       -- licenseId carried in the signed file, e.g. ZQ-2026-7K3QF9
    customer_id     uuid NOT NULL REFERENCES zls.customers (id),
    key_hash        text NOT NULL UNIQUE,                   -- LicenseKeys.Hash(normalized key); the key itself is shown once
    key_hint        text NOT NULL,
    edition         text NOT NULL CHECK (edition IN ('standard', 'advanced')),
    license_type    text NOT NULL CHECK (license_type IN ('subscription', 'perpetual')),
    term_years      int NULL CHECK (term_years BETWEEN 1 AND 10),
    starts_at       timestamptz NOT NULL,
    expires_at      timestamptz NOT NULL,
    support_until   timestamptz NOT NULL,
    devices         int NOT NULL CHECK (devices >= 0),       -- IT devices (endpoints, servers, network devices …)
    nonit_pack      int NULL CHECK (nonit_pack > 0),         -- NULL = unlimited
    cloud_instances int NOT NULL DEFAULT 0 CHECK (cloud_instances >= 0),
    technicians     int NOT NULL DEFAULT 0 CHECK (technicians >= 0),
    extra_modules   text[] NOT NULL DEFAULT '{}',
    max_activations int NOT NULL DEFAULT 1 CHECK (max_activations BETWEEN 1 AND 100),
    grace_days      int NOT NULL DEFAULT 30 CHECK (grace_days BETWEEN 0 AND 90),
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'revoked')),
    notes           text NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX licenses_customer_ix ON zls.licenses (customer_id);

CREATE TABLE zls.activations (
    id              uuid PRIMARY KEY,
    license_id      text NOT NULL REFERENCES zls.licenses (id),
    installation_id uuid NOT NULL,
    mode            text NOT NULL CHECK (mode IN ('online', 'offline')),
    token_hash      text NULL,                               -- SHA-256 of the bearer token (online only)
    request_id      uuid NULL,
    machine_name    text NULL,
    organization    text NULL,
    product_version text NULL,
    release_date    timestamptz NULL,
    usage           jsonb NOT NULL DEFAULT '{}',
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'deactivated')),
    activated_at    timestamptz NOT NULL DEFAULT now(),
    last_checkin_at timestamptz NULL,
    deactivated_at  timestamptz NULL,
    deactivated_by  text NULL
);
CREATE UNIQUE INDEX activations_active_ux ON zls.activations (license_id, installation_id) WHERE status = 'active';
CREATE INDEX activations_token_ix ON zls.activations (token_hash) WHERE token_hash IS NOT NULL;

CREATE TABLE zls.issued_documents (
    id            uuid PRIMARY KEY,
    license_id    text NOT NULL REFERENCES zls.licenses (id),
    activation_id uuid NULL REFERENCES zls.activations (id),
    issued_at     timestamptz NOT NULL,
    lease_until   timestamptz NULL,
    reason        text NOT NULL,
    document      text NOT NULL
);
CREATE INDEX issued_documents_license_ix ON zls.issued_documents (license_id, issued_at DESC);

CREATE TABLE zls.audit_log (
    id         bigserial PRIMARY KEY,
    at         timestamptz NOT NULL DEFAULT now(),
    actor      text NOT NULL,
    action     text NOT NULL,
    target     text NULL,
    ip         text NULL,
    detail     jsonb NULL
);
CREATE INDEX audit_log_at_ix ON zls.audit_log (at DESC);
CREATE INDEX audit_log_target_ix ON zls.audit_log (target);
