# Deploying LineJod on the shared Plesk host

Two separate deployments, like Pay_Yet's:

| Part | What | Where (planned) |
|---|---|---|
| **API** | the ASP.NET Core app: LINE webhook, the LIFF page's API, the hourly job, CSV download. **No frontend files inside.** | the same site as Pay_Yet's API (`api.drivetodev.online`), in its own folder `linejod` as a sub-application: `https://api.drivetodev.online/linejod` |
| **Frontend** | the LIFF page (`web/`, static: `index.html`, `config.js`, icons) | a static site of its own: **`https://linejod.drivetodev.online`** (Plesk subdomain `linejod` of `drivetodev.online`, FTP folder `linejod.drivetodev.online`), like Pay_Yet's `payyet.drivetodev.online` |

LineJod shares nothing with Pay_Yet except the host: its own folder, database, DB user and LINE channels. The browser calls the API from the frontend site cross-origin; the API allows that one origin (`App:WebBaseUrl`).

Below: `<api>` = `https://api.drivetodev.online/linejod`, `<web>` = `https://linejod.drivetodev.online`.

## To confirm first: a sub-application under the API site

Everything about the host below comes from Pay_Yet's deployment notes; **the sub-application was not tried**.

- Pay_Yet's site root is an ASP.NET Core app whose `web.config` has `inheritInChildApplications="false"`, which is what allows child applications. A plain sub-*folder* is not enough: IIS would hand `/linejod/...` to Pay_Yet's app, which answers 404. The folder must be an IIS **application** (Plesk → the site → *Virtual Directories* / *Applications*, or IIS Manager if you have it).
- A sub-path under the apex site failed for Pay_Yet with 500.19 (handlers section locked). The API site has a different config, so it may work; if it does not, use **a subdomain for the API too** (e.g. `linejod-api.drivetodev.online`, its own folder). The app supports both: only `App:PathBase` (`/linejod` or empty) and the URLs below change.

## One-time setup

1. **Frontend site:** Plesk → Websites & Domains → add the subdomain `linejod` to `drivetodev.online` (document root = its own folder `linejod.drivetodev.online`), no PHP/ASP.NET needed, **SSL/TLS → Let's Encrypt** and "Redirect from HTTP to HTTPS".
2. **API:** create the folder `linejod` inside the API site's folder and make it an application (see above). The API site already has its certificate.
3. **Database:** Plesk → Databases → add a database (e.g. `thaipes_linejod`) with **its own user**. Import [../db/schema.sql](../db/schema.sql) with phpMyAdmin. (A database made from an older schema.sql: run the files in [../db/migrations](../db/migrations) in date order instead.)
4. **LINE Developers** (separate from Pay_Yet's channels, so the two bots never mix):
   - a **Messaging API channel** for the LineJod bot: webhook URL `<api>/api/line/webhook`, *Use webhook* on, **Verify** once the API is up; auto-reply and greeting messages off. Note the *Channel secret* and a *long-lived Channel access token*.
   - a **LINE Login channel** with a **LIFF app**: size *Full*, scope `openid profile`, **endpoint URL = the frontend `<web>/`** (with the slash), linked to the LineJod bot. A Login channel links to one bot, so Pay_Yet's Login channel cannot be reused. Note the *LIFF ID* and the Login *Channel ID*.
5. **API secrets file:** copy [../deploy/appsettings.Production.example.json](../deploy/appsettings.Production.example.json) to `appsettings.Production.json` (git-ignored), fill it in and keep it out of chat and version control.

| Setting | Value |
|---|---|
| `ConnectionStrings:Default` | `Server=localhost;Port=3306;Database=<db>;User=<user>;Password=<password>;CharSet=utf8mb4` |
| `Db:ServerVersion` | `11.8.8-mariadb` (what the host runs) |
| `Line:ChannelSecret`, `Line:ChannelAccessToken` | from the Messaging API channel |
| `Line:LoginChannelId`, `Liff:Id` | from the Login channel and its LIFF app |
| `Jobs:Key` | a long random string (it guards `/jobs/*`) |
| `App:PublicBaseUrl` | `<api>` (no trailing slash; used in CSV download links) |
| `App:PathBase` | `/linejod` for the sub-application; empty if the API has a site of its own |
| `App:WebBaseUrl` | `https://linejod.drivetodev.online`: the only origin allowed to call the API from a browser (several: comma-separated; no path, no trailing slash needed) |

## Build and upload

**API**

```powershell
.\tools\publish.ps1            # tests, then win-x86 self-contained publish into .\publish (nothing is uploaded)
```

(win-x86 self-contained because the shared app pool is 32-bit and has no x86 runtime installed.)

1. Put the application offline while uploading: upload `app_offline.htm` into `linejod\` (IIS answers every request with it).
2. Upload the **contents** of `publish\` into `linejod\`, then `appsettings.Production.json` next to `LineJod.Api.exe`.
3. Delete `app_offline.htm`.
4. First time only: set `stdoutLogEnabled="true"` in `web.config` so startup errors land in `logs\stdout*`; turn it off when it runs.

**Frontend**

```powershell
.\tools\publish-web.ps1                  # copies web\ into .\publish-web and writes config.js with the API address (default https://api.drivetodev.online/linejod/)
```

Upload the contents of `publish-web\` to the folder `linejod.drivetodev.online` (FTP root of the subscription). To point at another API later (say the fallback subdomain), run it with `-ApiUrl <url>` or edit only `config.js` on the server.

There is no upload script for LineJod yet (Pay_Yet's uploads by FTP with a guard that refuses any folder but its own). Adding one is small once the folders exist.

## After it is up

1. `<api>/` → `{"app":"LineJod API","ok":true}`; `<api>/health` → `{"ok":true}` (503 = the database cannot be reached); `<api>/api/config` → shows your `liffId`.
2. `<web>/?demo=1` opens the page with sample data (no LINE needed).
3. LINE Developers → the webhook → **Verify**.
4. Rich menu, once: open `<api>/jobs/richmenu?key=<Jobs:Key>` → `Rich menu installed`.
5. Plesk → Scheduled Tasks → *Fetch a URL*, **every hour**: `<api>/jobs/daily?key=<Jobs:Key>` (hourly so each user's own notification hour is honoured).
6. Smoke test: add the bot as a friend → `ช่วยเหลือ` → `กาแฟ 65` → rich menu → จดรายจ่าย (the LIFF page) → `สรุป` → `ส่งออก` and open the link on the phone.

## Troubleshooting

| Symptom | Likely cause / action |
|---|---|
| `<api>/linejod/` answers 404 from Pay_Yet | the `linejod` folder is not an IIS application, so Pay_Yet's app handles it; convert it, or use a subdomain for the API |
| Health check cannot connect / TLS error | no valid certificate on that site yet |
| 500.19 "Lock violation" | IIS locks a config section there: note which element in `web.config`; fall back to a subdomain for the API |
| 500.30 / 502.5 / 500.0 | read `logs\stdout*` (turn stdout logging on); check `appsettings.Production.json` exists and is valid JSON |
| `/health` returns 503 | `ConnectionStrings:Default` (host, database, user, password) or the user's privileges |
| The page loads but every call fails ("เชื่อมต่อไม่ได้") | `config.js` points at the wrong API address, or `App:WebBaseUrl` does not match the page's origin exactly (scheme, host, port); the browser console says "CORS" |
| LIFF says login failed | `Line:LoginChannelId` is not the channel that owns the LIFF app, or the page was left open over an hour (reopen from LINE) |
| Webhook **Verify** fails | wrong Channel secret, API not running, certificate not valid, or a typo in the URL (remember the `/linejod`) |
| Bot never answers | *Use webhook* is off, auto-reply is on, or the webhook URL is wrong (check with Verify) |
| `/jobs/daily` returns 500 | the body lists which step failed (`failed`); the other steps still ran; see the app log |
| No pushes arrive | the scheduled task is not hourly, the key is wrong (expect 401), or the user's notification hour has not come yet |
| CSV link says expired | links last 10 minutes; ask `ส่งออก` again. Check `App:PublicBaseUrl` if the link points to the wrong place |

## Security

- Own database user for LineJod with rights on its database only.
- Keep `appsettings.Production.json` out of git and out of the package (`publish.ps1` removes any copy from it).
- Anyone with the job key can trigger `/jobs/*`; rotate it in the file and in the scheduled task.
- Other sites in the same Plesk subscription share the app-pool identity and may be able to read these folders; do not put anything here you would not accept that for.
- The API only answers browsers from `App:WebBaseUrl`; the data is still protected by the LINE login (ID token) on every call, not by CORS.
