-- LineJod (จดจ่าย) schema. MariaDB / MySQL, utf8mb4.

CREATE TABLE users (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  line_user_id  VARCHAR(64)  NOT NULL,
  display_name  VARCHAR(255) NULL,
  is_friend     TINYINT(1)   NOT NULL DEFAULT 0,
  created_at    DATETIME(3)  NOT NULL,
  UNIQUE KEY ux_users_line (line_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

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

CREATE TABLE ledger_entries (
  id               BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id          BIGINT       NOT NULL,
  kind             VARCHAR(8)   NOT NULL,        -- EXPENSE | INCOME
  category         VARCHAR(16)  NOT NULL,
  amount_satang    BIGINT       NOT NULL,
  note             VARCHAR(120) NULL,
  occurred_on      DATE         NOT NULL,
  source           VARCHAR(8)   NOT NULL DEFAULT 'LIFF',   -- LIFF | CHAT | SLIP | SUB | REC
  subscription_id  BIGINT       NULL,
  recurring_id     BIGINT       NULL,
  account_id       BIGINT       NULL,
  created_at       DATETIME(3)  NOT NULL,
  KEY ix_ledger_user_day (user_id, occurred_on),
  UNIQUE KEY ux_ledger_sub_day (subscription_id, occurred_on),
  UNIQUE KEY ux_ledger_rec_day (recurring_id, occurred_on),
  CONSTRAINT fk_ledger_user FOREIGN KEY (user_id) REFERENCES users(id),
  CONSTRAINT fk_ledger_entry_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_subscriptions (
  id              BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id         BIGINT       NOT NULL,
  name            VARCHAR(60)  NOT NULL,
  category        VARCHAR(16)  NOT NULL DEFAULT 'fun',
  amount_satang   BIGINT       NOT NULL,
  cycle           VARCHAR(8)   NOT NULL,         -- MONTHLY | YEARLY
  billing_day     INT          NOT NULL,
  billing_month   INT          NULL,
  next_charge_on  DATE         NOT NULL,
  trial_ends_on   DATE         NULL,
  active          TINYINT(1)   NOT NULL DEFAULT 1,
  charge_notified_for  DATE    NULL,
  trial_notice_level   INT     NOT NULL DEFAULT 0,
  created_at      DATETIME(3)  NOT NULL,
  KEY ix_ledger_sub_due (active, next_charge_on),
  CONSTRAINT fk_ledger_sub_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_budgets (
  user_id       BIGINT      NOT NULL,
  category      VARCHAR(16) NOT NULL,
  limit_satang  BIGINT      NOT NULL,
  PRIMARY KEY (user_id, category),
  CONSTRAINT fk_ledger_budget_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE processed_events (                 -- webhook idempotency; purged by the daily job after 7 days
  webhook_event_id  VARCHAR(64) NOT NULL PRIMARY KEY,
  processed_at      DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
