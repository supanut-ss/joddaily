# Deploying LineJod on the shared Plesk host

LineJod is its own app with its own folder, database and LINE channels. It can sit on the same Plesk host (subscription) as Pay_Yet and does not share anything with it. What follows is what is known about that host from Pay_Yet's deployment; the parts marked *to confirm* were not tried for LineJod.

## Where it lives

| Item | Value |
|---|---|
| Host | Plesk on Windows, IIS 10, ASP.NET Core Module v2 |
| Site | **its own subdomain site** with its own FTP folder, e.g. `linejod.drivetodev.online` (folder of the same name). The name is a choice: any new subdomain works. |
| Why not a sub-folder of Pay_Yet's site | on this host a sub-path under an existing site failed for Pay_Yet (`drivetodev.online/lineapi` → IIS 500.19, handlers section locked). A sub-folder is supported by the app (`App:PathBase`, tested locally) but not by this IIS, so use a subdomain. |
| Build | `win-x86` **self-contained**, out-of-process: the shared app pool is 32-bit and has no x86 runtime installed |
| Database | its own MariaDB database and its own DB user (not Pay_Yet's) |
| URL | the app serves the LIFF page, the API and the webhook from the same site, so there is a single URL |

Below `<host>` is that URL's host, e.g. `linejod.drivetodev.online`.

## One-time setup

1. **Plesk → Websites & Domains → Add Subdomain** `linejod` (name of your choice). Folder = the subdomain's name. Leave ASP.NET enabled, no PHP.
2. **SSL/TLS → Let's Encrypt** for it, and enable "Redirect from HTTP to HTTPS". LINE rejects invalid certificates.
3. **Databases → Add database** (e.g. `thaipes_linejod`) with its own user. Import [../db/schema.sql](../db/schema.sql) with phpMyAdmin. (A database made from an older schema.sql: run the files in [../db/migrations](../db/migrations) in date order instead.)
4. **LINE Developers** (separate from Pay_Yet's channels, so the two bots never mix):
   - a **Messaging API channel** for the LineJod bot: webhook URL `https://<host>/api/line/webhook`, *Use webhook* on, then **Verify** (after the app is up); auto-reply and greeting messages off. Note the *Channel secret* and a *long-lived Channel access token*.
   - a **LINE Login channel** with a **LIFF app**: size *Full*, scope `openid profile`, endpoint URL `https://<host>/` (with the slash), and link it to the LineJod bot. A Login channel links to one bot, so Pay_Yet's Login channel cannot be reused for this. Note the *LIFF ID* and the Login *Channel ID*.
5. **Secrets file**: copy [../deploy/appsettings.Production.example.json](../deploy/appsettings.Production.example.json) to `appsettings.Production.json` (git-ignored), fill in the values (below) and keep it out of chat and version control.

| Setting | Value |
|---|---|
| `ConnectionStrings:Default` | `Server=localhost;Port=3306;Database=<db>;User=<user>;Password=<password>;CharSet=utf8mb4` |
| `Db:ServerVersion` | `11.8.8-mariadb` (what the host runs) |
| `Line:ChannelSecret`, `Line:ChannelAccessToken` | from the Messaging API channel |
| `Line:LoginChannelId`, `Liff:Id` | from the Login channel and its LIFF app |
| `Jobs:Key` | a long random string (it guards `/jobs/*`) |
| `App:PublicBaseUrl` | `https://<host>` (no trailing slash; used in CSV download links) |
| `App:PathBase` | `""` |

## Build and upload

```powershell
.\tools\publish.ps1            # tests, then win-x86 self-contained publish into .\publish (nothing is uploaded)
```

1. Put the site offline while uploading: upload a file named `app_offline.htm` into the site folder (IIS then answers every request with it).
2. Upload the **contents** of `publish\` (FTP or Plesk File Manager) into the site folder, then `appsettings.Production.json` next to `LineJod.Api.exe`.
3. Delete `app_offline.htm`.
4. First time only: in `web.config` set `stdoutLogEnabled="true"` so startup errors land in `logs\stdout*`; turn it off again when it runs.

There is no deploy script for this host yet (Pay_Yet's uploads by FTP with a guard that refuses any folder but its own). Adding one for LineJod is a small job once the subdomain and its FTP folder exist.

## After it is up

1. `https://<host>/health` → `{"ok":true}` (503 = the database cannot be reached).
2. `https://<host>/api/config` → shows your `liffId`.
3. LINE Developers → the webhook → **Verify**.
4. Rich menu, once: open `https://<host>/jobs/richmenu?key=<Jobs:Key>` → `Rich menu installed`.
5. Plesk → Scheduled Tasks → *Fetch a URL*, **every hour**: `https://<host>/jobs/daily?key=<Jobs:Key>`. (Hourly, so each user's own notification hour is honoured.)
6. Smoke test: add the bot as a friend → `ช่วยเหลือ` → `กาแฟ 65` → open the rich menu → จดรายจ่าย (the LIFF page) → `สรุป` → `ส่งออก` and open the link on the phone.

## Troubleshooting

| Symptom | Likely cause / action |
|---|---|
| Health check cannot connect / TLS error | no valid certificate yet: issue Let's Encrypt for the subdomain |
| 500.19 "Lock violation" | IIS locks a config section for this site: note which element in `web.config` and tell me |
| 500.30 / 502.5 / 500.0 | read `logs\stdout*` (turn stdout logging on); check `appsettings.Production.json` exists and is valid JSON |
| `/health` returns 503 | `ConnectionStrings:Default` (host, database, user, password) or the user's privileges |
| Webhook **Verify** fails | wrong Channel secret, app not running, certificate not valid, or a typo in the URL |
| LIFF says login failed | `Line:LoginChannelId` is not the channel that owns the LIFF app, or the page was left open over an hour (reopen from LINE) |
| Bot never answers | *Use webhook* is off, auto-reply is on, or the webhook URL is wrong (check with Verify) |
| `/jobs/daily` returns 500 | the body lists which step failed (`failed`); the other steps still ran; see the app log |
| No pushes arrive | the scheduled task is not hourly, the key is wrong (expect 401), or the user's notification hour has not come yet |
| CSV link says expired | links last 10 minutes; ask `ส่งออก` again. Check `App:PublicBaseUrl` if the link points to the wrong host |

## Security

- Own database user for LineJod with rights on its database only.
- Keep `appsettings.Production.json` out of git and out of the publish package (`publish.ps1` removes any copy from the package).
- Anyone with the job key can trigger `/jobs/*`; rotate the key by changing it in the file and in the scheduled task.
- Other sites in the same Plesk subscription share the app-pool identity and may be able to read this folder; do not put anything here you would not accept that for.
