-- Accounts, transfers and recurring entries. For a database created from an older schema.sql; a fresh install already has all of this.

CREATE TABLE ledger_accounts (
  id             BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id        BIGINT       NOT NULL,
  name           VARCHAR(40)  NOT NULL,
  type           VARCHAR(8)   NOT NULL DEFAULT 'CASH',   -- CASH | BANK | CARD | EWALLET | OTHER
  opening_satang BIGINT       NOT NULL DEFAULT 0,
  is_default     TINYINT(1)   NOT NULL DEFAULT 0,
  active         TINYINT(1)   NOT NULL DEFAULT 1,
  created_at     DATETIME(3)  NOT NULL,
  KEY ix_ledger_account_user (user_id),
  CONSTRAINT fk_ledger_account_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_transfers (
  id              BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id         BIGINT      NOT NULL,
  from_account_id BIGINT      NOT NULL,
  to_account_id   BIGINT      NOT NULL,
  amount_satang   BIGINT      NOT NULL,
  occurred_on     DATE        NOT NULL,
  note            VARCHAR(120) NULL,
  created_at      DATETIME(3) NOT NULL,
  KEY ix_ledger_transfer_user_day (user_id, occurred_on),
  CONSTRAINT fk_ledger_transfer_user FOREIGN KEY (user_id) REFERENCES users(id),
  CONSTRAINT fk_ledger_transfer_from FOREIGN KEY (from_account_id) REFERENCES ledger_accounts(id),
  CONSTRAINT fk_ledger_transfer_to   FOREIGN KEY (to_account_id)   REFERENCES ledger_accounts(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_recurring (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id       BIGINT       NOT NULL,
  kind          VARCHAR(8)   NOT NULL,        -- EXPENSE | INCOME
  category      VARCHAR(16)  NOT NULL,
  amount_satang BIGINT       NOT NULL,
  note          VARCHAR(120) NULL,
  account_id    BIGINT       NULL,
  frequency     VARCHAR(8)   NOT NULL,        -- WEEKLY | MONTHLY | YEARLY
  day           INT          NOT NULL,        -- WEEKLY: ISO weekday 1-7; otherwise day of month
  month         INT          NULL,
  next_on       DATE         NOT NULL,
  end_on        DATE         NULL,
  active        TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME(3)  NOT NULL,
  KEY ix_ledger_recurring_due (active, next_on),
  CONSTRAINT fk_ledger_recurring_user    FOREIGN KEY (user_id)    REFERENCES users(id),
  CONSTRAINT fk_ledger_recurring_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

ALTER TABLE ledger_entries
  ADD COLUMN recurring_id BIGINT NULL AFTER subscription_id,
  ADD COLUMN account_id   BIGINT NULL AFTER recurring_id,
  ADD UNIQUE KEY ux_ledger_rec_day (recurring_id, occurred_on),
  ADD CONSTRAINT fk_ledger_entry_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id);
