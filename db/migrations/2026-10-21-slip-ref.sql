-- Transfer reference of a slip, so the same slip is never recorded twice. For a database created from an older schema.sql.

ALTER TABLE ledger_entries ADD slip_ref NVARCHAR(40) NULL;
GO
CREATE UNIQUE INDEX ux_ledger_slip_ref ON ledger_entries (user_id, slip_ref) WHERE slip_ref IS NOT NULL;
