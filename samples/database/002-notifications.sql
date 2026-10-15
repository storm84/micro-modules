CREATE TABLE IF NOT EXISTS parcel_notification_inbox (
    id uuid PRIMARY KEY,
    shipment_id uuid NOT NULL,
    payload jsonb NOT NULL,
    delivered_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS parcel_inbox_shipment ON parcel_notification_inbox (shipment_id);
