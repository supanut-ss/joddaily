-- Accounts, transfers and recurring entries. For a database created from an older schema.sql; a fresh install already has all of this.

CREATE TABLE ledger_accounts (
  id             BIGINT IDENTITY(1,1) PRIMARY KEY,
  user_id        BIGINT        NOT NULL,
  name           NVARCHAR(40)  NOT NULL,
  type           NVARCHAR(8)   NOT NULL DEFAULT 'CASH',   -- CASH | BANK | CARD | EWALLET | OTHER
  opening_satang BIGINT        NOT NULL DEFAULT 0,
  is_default     BIT           NOT NULL DEFAULT 0,
  active         BIT           NOT NULL DEFAULT 1,
  created_at     DATETIME2(3)  NOT NULL,
  CONSTRAINT fk_ledger_account_user FOREIGN KEY (user_id) REFERENCES users(id)
);
CREATE INDEX ix_ledger_account_user ON ledger_accounts (user_id);

CREATE TABLE ledger_transfers (
  id              BIGINT IDENTITY(1,1) PRIMARY KEY,
  user_id         BIGINT        NOT NULL,
  from_account_id BIGINT        NOT NULL,
  to_account_id   BIGINT        NOT NULL,
  amount_satang   BIGINT        NOT NULL,
  occurred_on     DATE          NOT NULL,
  note            NVARCHAR(120) NULL,
  created_at      DATETIME2(3)  NOT NULL,
  CONSTRAINT fk_ledger_transfer_user FOREIGN KEY (user_id) REFERENCES users(id),
  CONSTRAINT fk_ledger_transfer_from FOREIGN KEY (from_account_id) REFERENCES ledger_accounts(id),
  CONSTRAINT fk_ledger_transfer_to   FOREIGN KEY (to_account_id)   REFERENCES ledger_accounts(id)
);
CREATE INDEX ix_ledger_transfer_user_day ON ledger_transfers (user_id, occurred_on);

CREATE TABLE ledger_recurring (
  id            BIGINT IDENTITY(1,1) PRIMARY KEY,
  user_id       BIGINT        NOT NULL,
  kind          NVARCHAR(8)   NOT NULL,        -- EXPENSE | INCOME
  category      NVARCHAR(16)  NOT NULL,
  amount_satang BIGINT        NOT NULL,
  note          NVARCHAR(120) NULL,
  account_id    BIGINT        NULL,
  frequency     NVARCHAR(8)   NOT NULL,        -- WEEKLY | MONTHLY | YEARLY
  day           INT           NOT NULL,        -- WEEKLY: ISO weekday 1-7; otherwise day of month
  month         INT           NULL,
  next_on       DATE          NOT NULL,
  end_on        DATE          NULL,
  active        BIT           NOT NULL DEFAULT 1,
  created_at    DATETIME2(3)  NOT NULL,
  CONSTRAINT fk_ledger_recurring_user    FOREIGN KEY (user_id)    REFERENCES users(id),
  CONSTRAINT fk_ledger_recurring_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id)
);
CREATE INDEX ix_ledger_recurring_due ON ledger_recurring (active, next_on);

ALTER TABLE ledger_entries ADD recurring_id BIGINT NULL;
ALTER TABLE ledger_entries ADD account_id BIGINT NULL;
GO
CREATE UNIQUE INDEX ux_ledger_rec_day ON ledger_entries (recurring_id, occurred_on) WHERE recurring_id IS NOT NULL;
ALTER TABLE ledger_entries ADD CONSTRAINT fk_ledger_entry_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id);
