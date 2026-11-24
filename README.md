# Self-Study Log

A vibe-coded app that lets you plan your task around Google Calendar, track your mood and productivity tasks before and after the task to study your productivity-related behavior.

Time your work, rate mood and energy before and after, track habits, plan chunks against your calendar, and see what your own data says about how you work. It runs anywhere a browser runs and depends on no AI service.

## What is in this folder

| Path | What it is |
| :-- | :-- |
| `web/` | The app: plain HTML and JavaScript, no build step. Installable on a phone or computer (Add to Home screen / Install app). |
| `web/config.js` | The only settings file: your Firebase project and Google client ID. Empty means this-device-only, no calendar. |
| `web/platform.js` | Storage, Google Calendar, file saving and the taskbar timer link. The app talks only to this. |
| `pill/TaskbarPill.exe` | Windows companion (20 KB): serves the app at `http://localhost:47613` and shows the running session's clock on the taskbar. |
| `pill/TaskbarPill.cs`, `pill/build.cmd` | Its source, and the build script (uses the C# compiler that ships with Windows). |
| `firebase.json`, `firestore.rules` | Hosting and database rules for deploying with Firebase. |
| `migration/self-study-log-import.json` | Your log as it was in the Claude artifact, ready to import. Personal data: keep it private. |

## Updating the app

The code lives in a private GitHub repository. Edit the files, commit and push to `main`: GitHub Actions deploys `web/` to Firebase Hosting within a minute or two (`.github/workflows/`). `http://localhost:47613` shows local edits straight away, before any push. Database rules (`firestore.rules`) are not deployed by GitHub; after changing them run `firebase deploy --only firestore:rules`.

Not in the repository: `migration/` (personal data) and `pill/TaskbarPill.exe` (run `pill\build.cmd` to build it from `TaskbarPill.cs`).

## 1. Use it now on this PC (no accounts needed)

1. Double-click `pill\TaskbarPill.exe`. It opens the log at `http://localhost:47613` in your default browser and waits in the background.
2. In the log: **Setup → Your data → Import a JSON export**, and choose `migration\self-study-log-import.json`.
3. Start a session: the clock appears on the taskbar, just left of the tray icons. Click it to switch between time passed and time left; right-click it to quit. It hides when no session runs or a full-screen app is in front.

In this mode the log lives in that one browser on this PC. Pick one browser and keep using it, or set up step 2.

To start the taskbar timer with Windows: press `Win + R`, type `shell:startup`, and put a shortcut to `TaskbarPill.exe` there with `--quiet` added to its target (it then starts without opening the browser).

## 2. Sync phone and computer (Firebase, free tier)

About ten minutes, once. Your data stays in your own Google account's Firebase project.

1. Go to <https://console.firebase.google.com>, **Add project** (Analytics not needed).
2. **Build → Firestore Database → Create database** (production mode, a region near you).
3. **Build → Authentication → Get started → Google → Enable**.
4. **Project settings → Your apps → Web app (`</>`)**, register it, and copy the `firebaseConfig` object into `web/config.js` as `firebase: { ... }`.
5. Deploy (needs Node.js, already on this PC):
   ```
   npm install -g firebase-tools
   firebase login
   firebase use --add          (pick the project)
   firebase deploy
   ```
   This publishes the app to `https://<project-id>.web.app` and applies `firestore.rules`, so only you can read your log.
6. Add `https://<project-id>.web.app` as a line in `pill\pill-origins.txt` (create the file), so the deployed app on this PC can drive the taskbar timer.
7. Open the deployed address on your phone and computer, sign in with Google, and import the migration file once.

Free-tier limits (Spark plan): 1 GB stored, 50,000 reads and 20,000 writes a day, 10 GB hosting transfer a month. One person's log uses a tiny fraction.

## 3. Google Calendar (time bank, placeholders, "time is up" alert)

1. Open <https://console.cloud.google.com> and select the same project (Firebase projects are Google Cloud projects).
2. **APIs & Services → Library → Google Calendar API → Enable**.
3. **OAuth consent screen**: External, app name "Self-Study Log", your email; leave it in **Testing** and add yourself under **Test users**.
4. **Credentials → Create credentials → OAuth client ID → Web application**. Authorised JavaScript origins: `http://localhost:47613` and `https://<project-id>.web.app`.
5. Paste the client ID into `web/config.js` as `googleClientId`, and deploy again.
6. In the log: **Plan → Connect Google Calendar**, then **Setup → Choose calendars**.

Google gives the app one-hour access at a time. When it runs out, the calendar figures stay on screen and your next click in the app renews access (a Google window opens and closes by itself).

### Long-lasting connection (recommended): your own Apps Script bridge

A small script in your own Google account talks to Calendar for the log. Apps Script keeps its permission until you remove it, so the log stays connected for months on every device, with nothing to renew. Free, no card.

1. In the log: **Setup → Google Calendar connection → Copy the script**. It contains a secret key made for your log; the key is kept in your private data (`users/<you>/private/calendarBridge`), never in the published files.
2. Open <https://script.google.com/home/projects/create>, select everything in the editor, paste, save.
3. Next to **Services**, press **+**, choose **Google Calendar API**, **Add**.
4. **Deploy → New deployment →** gear icon **→ Web app**. Execute as **Me**, Who has access **Anyone**. **Deploy**, then allow access (if Google says the app is unverified: **Advanced → Go to …**; it is your own script).
5. Copy the **Web app URL** (ends in `/exec`), paste it into the log and press **Save and test**.

"Anyone" means anyone with the URL *and* the key: requests without the key are refused. To change the script later, use **Deploy → Manage deployments → edit → New version**, so the URL stays the same. To stop it, press **Remove** in the log and delete the project at script.google.com.

## Deploying for other people later

- Hosting and data scale on Firebase's pay-as-you-go plan; each user's data stays under their own sign-in.
- Calendar access for people outside your test-user list needs Google's OAuth verification for the calendar scopes.
- `TaskbarPill.exe` is optional and Windows-only; phones and Macs use the app alone.
