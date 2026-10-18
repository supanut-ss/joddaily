-- LineJod (จดจ่าย) schema. MariaDB / MySQL, utf8mb4.

CREATE TABLE users (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  line_user_id  VARCHAR(64)  NOT NULL,
  display_name  VARCHAR(255) NULL,
  is_friend     TINYINT(1)   NOT NULL DEFAULT 0,
  created_at    DATETIME(3)  NOT NULL,
  UNIQUE KEY ux_users_line (line_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_entries (
  id               BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id          BIGINT       NOT NULL,
  kind             VARCHAR(8)   NOT NULL,        -- EXPENSE | INCOME
  category         VARCHAR(16)  NOT NULL,
  amount_satang    BIGINT       NOT NULL,
  note             VARCHAR(120) NULL,
  occurred_on      DATE         NOT NULL,
  source           VARCHAR(8)   NOT NULL DEFAULT 'LIFF',   -- LIFF | CHAT | SLIP | SUB
  subscription_id  BIGINT       NULL,
  created_at       DATETIME(3)  NOT NULL,
  KEY ix_ledger_user_day (user_id, occurred_on),
  UNIQUE KEY ux_ledger_sub_day (subscription_id, occurred_on),
  CONSTRAINT fk_ledger_user FOREIGN KEY (user_id) REFERENCES users(id)
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
