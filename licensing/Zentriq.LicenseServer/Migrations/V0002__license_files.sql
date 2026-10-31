-- License files for customers (activation certificates): signed, bound to no installation. Only the newest file of a
-- license activates; issuing a new file makes the earlier one unusable.
ALTER TABLE zls.licenses ADD COLUMN file_hash text NULL;
ALTER TABLE zls.licenses ADD COLUMN file_issued_at timestamptz NULL;
