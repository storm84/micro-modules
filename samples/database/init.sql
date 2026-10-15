CREATE TABLE counters (
    id text PRIMARY KEY,
    value bigint NOT NULL
);
INSERT INTO counters (id, value) VALUES ('demo', 0);
CREATE TABLE counter_operations (
    operation_id uuid PRIMARY KEY,
    amount bigint NOT NULL CHECK (amount BETWEEN 1 AND 1000),
    result_value bigint
);
