# LineJod (จดจ่าย) — income / expense ledger on LINE

ASP.NET Core 8 + MariaDB/MySQL. The API also serves the LIFF page (`src/LineJod.Api/wwwroot/index.html`) from the same host, so there is no CORS setup.
This project is separate from Pay_Yet (bill collection); it only reuses its stack and LINE login approach.

## What it does

- Record income / expenses from a LIFF page: on-page keypad, category icons, 10-second undo.
- Home (balance, monthly budgets with warnings, next subscription, latest records), history by day, report (category donut + daily bars).
- Subscriptions (monthly / yearly, optional free-trial end date). The daily job records each charge as an expense on its billing day, catching up missed days, never twice for the same day.
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
| `สรุป` · `ลบล่าสุด` · `ช่วยเหลือ` | month card / delete last chat entry / help |

Every saved entry answers with a Flex card with **เปลี่ยนหมวด** and **ยกเลิก** (undo works for 10 minutes). Ordinary talk without an amount gets a short hint. Group chats are ignored.

Daily push (job): **the day before** a subscription charge, and **3 days and 1 day before** a free trial ends (the day-before trial notice replaces the charge notice). What was sent is stored per subscription, so re-running or running late never repeats a notice, and a failed push is retried on the next run.

## Not built yet

Reading slips (images), notification time per user.

## Setup

1. Create a DB and import [db/schema.sql](db/schema.sql); set `Db:ServerVersion` if it is not MariaDB 11.8.
2. LINE Developers: a Messaging API channel (webhook URL `https://<host>/api/line/webhook`, *Use webhook* on, auto-reply and greeting off) and a LINE Login channel in the same provider with a LIFF app (size *Full*, scope `openid profile`, endpoint = the public URL of this site). Link the Login channel to the bot. Fill `appsettings.Production.json` (git-ignored): `ConnectionStrings:Default`, `Line:ChannelSecret`, `Line:ChannelAccessToken`, `Line:LoginChannelId`, `Liff:Id`, `Jobs:Key`.
3. Schedule `GET /jobs/daily?key=<Jobs:Key>` once a day at about 09:00 Asia/Bangkok (records due subscription charges, then sends the notices).
4. Rich menu (6 tiles: จดรายจ่าย · จดรายรับ · สรุปเดือนนี้ · ประวัติ · ค่าสมาชิก · งบประมาณ): after deploying, open `https://<host>/jobs/richmenu?key=<Jobs:Key>` once. It creates the menu, uploads `richmenu/richmenu.png`, makes it the default for everyone and removes the previous copy. To change the look edit `tools/richmenu/menu.html`, run `tools/richmenu/render.ps1` (headless Edge/Chrome) and call the URL again. Tiles open LIFF with `?tab=new|new&kind=income|history|subs|budget`; สรุปเดือนนี้ sends the chat command `สรุป`.

## Verified / not verified

Verified (60 unit tests on SQLite): chat parser (dates, several items, categories, subscription / trial forms), chat service (save, undo window, ownership, category change, subscription create / update / stop), webhook processor (new user, redelivery, groups ignored, postback, follow), signature check, rich menu (tile bounds, links, install order, failed upload cleanup), notifier (once only, trial 3 / 1 day, late run, failed push retried, trial date change resets), billing dates, charge catch-up, month summary. The LIFF page was checked in demo mode in a browser.
Not verified: installing the menu on LINE and how it looks in the chat, real LINE delivery and **Flex rendering on a phone** (cards are built to the Flex spec but never shown by LINE), real LIFF login, the MariaDB schema and queries, API endpoints end to end.

```
dotnet test LineJod.slnx
```
