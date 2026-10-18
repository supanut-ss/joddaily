# LineJod (จดจ่าย) — income / expense ledger on LINE

ASP.NET Core 8 + MariaDB/MySQL. The API also serves the LIFF page (`src/LineJod.Api/wwwroot/index.html`) from the same host, so there is no CORS setup.
This project is separate from Pay_Yet (bill collection); it only reuses its stack and LINE login approach.

## What it does

- Record income / expenses from a LIFF page: on-page keypad, category icons, 10-second undo.
- Home (balance, monthly budgets with warnings, next subscription, latest records), history by day, report (category donut + daily bars).
- Subscriptions (monthly / yearly, optional free-trial end date). The daily job records each charge as an expense on its billing day, catching up missed days, never twice for the same day.
- **Accounts** (tab บัญชี): cash, bank, credit card, e-wallet, other. Each has an opening balance (negative for a card with debt) and a balance = opening + income - expense of its entries + transfers in - transfers out. The first account is the default one (used when an entry names no account). **Transfers** between accounts only move money: they are not income or expense, so reports ignore them. Archiving an account hides it and leaves its entries (they keep showing its name). Entries pick an account with chips in the form; history can be filtered by account; the CSV has an account column. Entries without an account are in no balance.
- **Recurring entries** (tab ประจำ → รายการประจำ): income or expense repeating weekly / monthly (day of month, clamped to the month length) / yearly, optional end date (for instalments), optional account. The daily job records each occurrence as a normal entry (source `ประจำ`), catches up missed days, never records the same day twice, and ends the item after its end date. Editing it later never records today's occurrence a second time. Subscriptions (with free trials and reminders) stay a separate list in the same tab.
- **Notifications per user** (bell icon on the home screen, or the chat command `แจ้งเตือน`): a master switch, the hour pushes are sent (default 09:00, Asia/Bangkok) and three kinds: subscription charge tomorrow / free trial ending, budget at 80% / exceeded (once per category per month for each level, again next month), and an opt-in "nothing logged today" nudge (only for people who logged something in the last 14 days; subscription and recurring entries made by the system do not count as logging). A user without settings has the defaults.
- **Search** (history screen): type a note word (`ค่าไฟ`), a category name (`อาหาร`) or an exact amount (`1250`); optional category and income / expense filters. Searches all months, newest 200 shown with the totals of what was found.
- **Export CSV**: history screen → download icon → choose month / this year / all. The app makes a 10-minute signed link that opens in the phone's own browser (LINE's in-app browser handles downloads badly). The file is UTF-8 with BOM (Thai opens correctly in Excel), columns: date, type, category, amount, note, source; notes starting with `= + - @` are defused so a spreadsheet never runs them as formulas.
- Preview without LINE: open `/?demo=1` (sample data in memory). `?tab=new|history|report|subs` jumps to a screen (also `tab=budget`, and `tab=new&kind=income`; the rich menu uses these links).

## Chat (LINE bot, 1:1 chat only)

| Type | Result |
|---|---|
| `กาแฟ 65` | expense, category guessed from the words |
| `+30000 เงินเดือน` · `รับ 2500 ฟรีแลนซ์` | income |
| `ข้าว 50 กาแฟ 45` | two entries, one card |
| `แท็กซี่ 120 เมื่อวาน` · `… 15/10` · `… 5/1/2569` | choose the day |
| `สมาชิก Netflix 419 ทุกวันที่ 5` · `สมาชิก Adobe 7600 รายปี 12/3` | add a subscription (same name = update) |
| `ทดลอง Canva 300 ถึง 24/10` | free trial: first charge on the end date |
| `สมาชิก` · `เลิก Spotify` | list / stop |
| `กาแฟ 65 @เงินสด` | put the entry in an account (`@` + name, case and spaces ignored, a unique part is enough); no `@` = default account |
| `บัญชี` | balances of all accounts and the total |
| `ประจำ ค่าเช่า 8000 ทุกวันที่ 1` · `ประจำ +30000 เงินเดือน ทุกวันที่ 25 @KBank` | recurring expense / income |
| `ประจำ ค่าขนม 500 ทุกสัปดาห์` (or `ทุกวันศุกร์`) · `ประจำ ประกัน 12000 รายปี 15/3` · `… ถึง 5/6/2027` | weekly / yearly / with an end date |
| `ประจำ` · `เลิกประจำ ค่าเช่า` | list / stop |
| `ค้นหา ค่าไฟ` · `ค้นหา อาหาร` · `ค้นหา 1250` | card with matches, totals and a link into the app |
| `ส่งออก` · `ส่งออก ปีนี้` · `ส่งออก ทั้งหมด` | card with a CSV download link (10 minutes) |
| `แจ้งเตือน` · `แจ้งเตือน ปิด` · `แจ้งเตือน 20` · `แจ้งเตือน งบ ปิด` · `แจ้งเตือน สมาชิก ปิด` · `แจ้งเตือน จด เปิด` | show / change the notification settings |
| `สรุป` · `ลบล่าสุด` · `ช่วยเหลือ` | month card / delete last chat entry / help |

Every saved entry answers with a Flex card with **เปลี่ยนหมวด** and **ยกเลิก** (undo works for 10 minutes). Ordinary talk without an amount gets a short hint. Group chats are ignored.

Pushes (job; each user on their own hour and switches): **the day before** a subscription charge, and **3 days and 1 day before** a free trial ends (the day-before trial notice replaces the charge notice). What was sent is stored per subscription, so re-running or running late never repeats a notice, and a failed push is retried on the next run.

## Not built yet

Reading slips (images), transfers by chat, reconciling an account balance with the bank.

## Setup

1. Create a DB and import [db/schema.sql](db/schema.sql) (an older database: also run the files in [db/migrations](db/migrations) in date order, e.g. `2026-10-19-accounts-recurring.sql`, `2026-10-20-notify-settings.sql`); set `Db:ServerVersion` if it is not MariaDB 11.8.
2. LINE Developers: a Messaging API channel (webhook URL `https://<host>/api/line/webhook`, *Use webhook* on, auto-reply and greeting off) and a LINE Login channel in the same provider with a LIFF app (size *Full*, scope `openid profile`, endpoint = the public URL of this site). Link the Login channel to the bot. Fill `appsettings.Production.json` (git-ignored): `ConnectionStrings:Default`, `Line:ChannelSecret`, `Line:ChannelAccessToken`, `Line:LoginChannelId`, `Liff:Id`, `Jobs:Key`, `App:PublicBaseUrl` (the site's public URL, `https://<host>`; needed for the CSV links).
3. Schedule `GET /jobs/daily?key=<Jobs:Key>` **every hour** (Plesk: a scheduled task with a *Fetch a URL* action, hourly). Each run records due subscription charges and recurring entries (safe to repeat), then sends every user the pushes whose hour has come. Run only once a day and users whose chosen hour is later than that run never get their pushes.
4. Rich menu (6 tiles: จดรายจ่าย · จดรายรับ · สรุปเดือนนี้ · ประวัติ · ค่าสมาชิก · งบประมาณ): after deploying, open `https://<host>/jobs/richmenu?key=<Jobs:Key>` once. It creates the menu, uploads `richmenu/richmenu.png`, makes it the default for everyone and removes the previous copy. To change the look edit `tools/richmenu/menu.html`, run `tools/richmenu/render.ps1` (headless Edge/Chrome) and call the URL again. Tiles open LIFF with `?tab=new|new&kind=income|history|subs|budget`; สรุปเดือนนี้ sends the chat command `สรุป`.

## Verified / not verified

Verified (185 unit tests on SQLite): chat parser (dates, several items, categories, subscription / trial forms), chat service (save, undo window, ownership, category change, subscription create / update / stop), webhook processor (new user, redelivery, groups ignored, postback, follow), signature check, rich menu (tile bounds, links, install order, failed upload cleanup), accounts (balances, default, archive, transfers, name lookup, ownership), recurring (weekly / monthly / yearly, catch-up, end date, idempotency, edit-after-record), the account / recurring / entry endpoints, search (note / category name / amount, filters, ownership), CSV (BOM, quoting, formula guard), signed export links (round trip, expiry, tampering) and the download / search endpoints, schema.sql and the migrations against the EF model (tables, columns, nullability, types), the daily job (key, counts, a failing step does not stop the others), notification settings (hour per user, master and per-kind switches, budget alert levels and monthly reset, nudge rules, chat command and API) and the notifier (once only, trial 3 / 1 day, late run, failed push retried, trial date change resets), billing dates, charge catch-up, month summary. The LIFF page was checked in demo mode in a browser.
Known limits: LIFF ID tokens last about an hour, so a page left open longer gets "login failed" until it is reopened from LINE; a CSV link works for anyone who has it for 10 minutes; the job key travels in the URL (Plesk's *Fetch a URL* cannot send headers), so keep it out of shared logs; a plain number typed in chat (`12345`) is recorded as an expense (undo is on the card).
Not verified: opening the CSV link from LINE on a phone (the `openExternalBrowser` / `liff.openWindow` hand-off), installing the menu on LINE and how it looks in the chat, real LINE delivery and **Flex rendering on a phone** (cards are built to the Flex spec but never shown by LINE), real LIFF login, the MariaDB schema and queries, API endpoints end to end.

```
dotnet test LineJod.slnx
```
