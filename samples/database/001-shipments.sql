-- Safe to apply repeatedly, including to a retained counter-demo database.
CREATE TABLE IF NOT EXISTS parcel_shipments (
    id uuid PRIMARY KEY,
    payload jsonb NOT NULL
);
CREATE TABLE IF NOT EXISTS parcel_operations (
    operation_id uuid PRIMARY KEY,
    kind text NOT NULL,
    request jsonb NOT NULL,
    result jsonb
);
CREATE TABLE IF NOT EXISTS parcel_notification_outbox (
    id uuid PRIMARY KEY,
    payload jsonb NOT NULL,
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz,
    last_error text
);
CREATE INDEX IF NOT EXISTS parcel_outbox_pending
    ON parcel_notification_outbox (next_attempt_at) WHERE processed_at IS NULL;
