# LineJod (จดจ่าย) — income / expense ledger on LINE

ASP.NET Core 8 + MariaDB/MySQL. The API also serves the LIFF page (`src/LineJod.Api/wwwroot/index.html`) from the same host, so there is no CORS setup.
This project is separate from Pay_Yet (bill collection); it only reuses its stack and LINE login approach.

## What it does

- Record income / expenses from a LIFF page: on-page keypad, category icons, 10-second undo.
- Home (balance, monthly budgets with warnings, next subscription, latest records), history by day, report (category donut + daily bars).
- Subscriptions (monthly / yearly, optional free-trial end date). The daily job records each charge as an expense on its billing day, catching up missed days, never twice for the same day.
- Preview without LINE: open `/?demo=1` (sample data in memory). `?tab=new|history|report|subs` jumps to a screen (use these as rich-menu links).

## Not built yet

Chat commands (`กาแฟ 65`, `+30000 เงินเดือน`, `สมาชิก Netflix 419 ทุกวันที่ 5`, slips), Flex confirmation cards, push reminders before a charge / trial end, rich menu.

## Setup

1. Create a DB and import [db/schema.sql](db/schema.sql); set `Db:ServerVersion` if it is not MariaDB 11.8.
2. LINE Developers: a LINE Login channel with a LIFF app (size *Full*, scope `openid profile`, endpoint = the public URL of this site). Fill `appsettings.Production.json` (git-ignored): `ConnectionStrings:Default`, `Line:LoginChannelId`, `Liff:Id`, `Jobs:Key`.
3. Schedule `GET /jobs/daily?key=<Jobs:Key>` once a day (after midnight, Asia/Bangkok).

## Verified / not verified

Verified: build, 5 unit tests (billing dates, trial, yearly → monthly, charge catch-up + idempotency, monthly summary) on SQLite, and the LIFF page in demo mode in a browser (mobile width, light and dark).
Not verified: real LINE login / LIFF, the MariaDB schema and queries, the API endpoints end to end.

```
dotnet test LineJod.slnx
```
