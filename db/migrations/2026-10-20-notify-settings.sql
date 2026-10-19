-- Per-user notification settings and the budget alerts already sent. For a database created from an older schema.sql.

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
