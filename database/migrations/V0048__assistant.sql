-- Zentriq Assistant (build 145): questions and answers, for the audit trail and for improving answers.
SET search_path = zentriq, public;

CREATE TABLE assistant_messages (
    id           bigserial PRIMARY KEY,
    user_id      uuid REFERENCES users(id) ON DELETE SET NULL,
    user_name    text NOT NULL,
    asked_at     timestamptz NOT NULL DEFAULT now(),
    question     text NOT NULL,
    answer       text,
    provider     text NOT NULL,
    tools        text[] NOT NULL DEFAULT '{}',
    sources      text[] NOT NULL DEFAULT '{}',
    duration_ms  integer,
    ok           boolean NOT NULL DEFAULT true,
    error        text,
    rating       smallint CHECK (rating IN (-1, 1))
);
CREATE INDEX ix_assistant_messages_asked ON assistant_messages (asked_at DESC);
CREATE INDEX ix_assistant_messages_user ON assistant_messages (user_id, asked_at DESC);
