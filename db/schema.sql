-- JodDaily (จดจ่าย) schema. SQL Server (T-SQL); text columns are NVARCHAR so Thai is stored as is.
-- Unique indexes over nullable columns are filtered (WHERE ... IS NOT NULL): SQL Server treats NULLs as equal in a unique index.

CREATE TABLE users (
  id            BIGINT IDENTITY(1,1) PRIMARY KEY,
  line_user_id  NVARCHAR(64)  NOT NULL,
  display_name  NVARCHAR(255) NULL,
  is_friend     BIT           NOT NULL DEFAULT 0,
  created_at    DATETIME2(3)  NOT NULL
);
CREATE UNIQUE INDEX ux_users_line ON users (line_user_id);

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

CREATE TABLE ledger_entries (
  id               BIGINT IDENTITY(1,1) PRIMARY KEY,
  user_id          BIGINT        NOT NULL,
  kind             NVARCHAR(8)   NOT NULL,        -- EXPENSE | INCOME
  category         NVARCHAR(16)  NOT NULL,
  amount_satang    BIGINT        NOT NULL,
  note             NVARCHAR(120) NULL,
  occurred_on      DATE          NOT NULL,
  source           NVARCHAR(8)   NOT NULL DEFAULT 'LIFF',   -- LIFF | CHAT | SLIP | SUB | REC
  subscription_id  BIGINT        NULL,
  recurring_id     BIGINT        NULL,
  account_id       BIGINT        NULL,
  slip_ref         NVARCHAR(40)  NULL,            -- transfer reference from the slip QR
  created_at       DATETIME2(3)  NOT NULL,
  CONSTRAINT fk_ledger_user FOREIGN KEY (user_id) REFERENCES users(id),
  CONSTRAINT fk_ledger_entry_account FOREIGN KEY (account_id) REFERENCES ledger_accounts(id)
);
CREATE INDEX ix_ledger_user_day ON ledger_entries (user_id, occurred_on);
CREATE UNIQUE INDEX ux_ledger_sub_day ON ledger_entries (subscription_id, occurred_on) WHERE subscription_id IS NOT NULL;
CREATE UNIQUE INDEX ux_ledger_rec_day ON ledger_entries (recurring_id, occurred_on) WHERE recurring_id IS NOT NULL;
CREATE UNIQUE INDEX ux_ledger_slip_ref ON ledger_entries (user_id, slip_ref) WHERE slip_ref IS NOT NULL;

CREATE TABLE ledger_subscriptions (
  id              BIGINT IDENTITY(1,1) PRIMARY KEY,
  user_id         BIGINT        NOT NULL,
  name            NVARCHAR(60)  NOT NULL,
  category        NVARCHAR(16)  NOT NULL DEFAULT 'fun',
  amount_satang   BIGINT        NOT NULL,
  cycle           NVARCHAR(8)   NOT NULL,         -- MONTHLY | YEARLY
  billing_day     INT           NOT NULL,
  billing_month   INT           NULL,
  next_charge_on  DATE          NOT NULL,
  trial_ends_on   DATE          NULL,
  active          BIT           NOT NULL DEFAULT 1,
  charge_notified_for  DATE     NULL,
  trial_notice_level   INT      NOT NULL DEFAULT 0,
  created_at      DATETIME2(3)  NOT NULL,
  CONSTRAINT fk_ledger_sub_user FOREIGN KEY (user_id) REFERENCES users(id)
);
CREATE INDEX ix_ledger_sub_due ON ledger_subscriptions (active, next_charge_on);

CREATE TABLE ledger_budgets (
  user_id       BIGINT       NOT NULL,
  category      NVARCHAR(16) NOT NULL,
  limit_satang  BIGINT       NOT NULL,
  PRIMARY KEY (user_id, category),
  CONSTRAINT fk_ledger_budget_user FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE ledger_settings (
  user_id              BIGINT      NOT NULL PRIMARY KEY,
  notify_enabled       BIT         NOT NULL DEFAULT 1,
  notify_hour          INT         NOT NULL DEFAULT 9,     -- 0-23, Asia/Bangkok
  notify_subscriptions BIT         NOT NULL DEFAULT 1,
  notify_budget        BIT         NOT NULL DEFAULT 1,
  notify_daily         BIT         NOT NULL DEFAULT 0,
  last_nudge_on        DATE        NULL,
  CONSTRAINT fk_ledger_settings_user FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE ledger_budget_alerts (
  user_id  BIGINT       NOT NULL,
  category NVARCHAR(16) NOT NULL,
  month    NCHAR(7)     NOT NULL,                             -- yyyy-MM
  level    INT          NOT NULL,                             -- 1 = reached 80%, 2 = exceeded
  PRIMARY KEY (user_id, category, month),
  CONSTRAINT fk_ledger_budget_alert_user FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE processed_events (                 -- webhook idempotency; purged by the daily job after 7 days
  webhook_event_id  NVARCHAR(64) NOT NULL PRIMARY KEY,
  processed_at      DATETIME2(3) NOT NULL
);
