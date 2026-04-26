# Netflix Household Auto Confirmator

A small service that watches a configured Gmail/IMAP label for Netflix Household update emails and automatically completes the Netflix confirmation flow in a headless browser.

This fork is optimized for small Ubuntu/DigitalOcean Droplets using Chromium headless and a systemd service.

## Features

- Connects to Gmail/IMAP and scans only the configured Gmail label/folder.
- Finds Netflix Household emails with subject `Important: How to update your Netflix household`.
- Extracts the Netflix confirmation URL from the email body.
- Opens the URL with Selenium + Chromium headless.
- Handles the full two-step Netflix flow:
  - `Yes, this was me`
  - `Confirm update`
- Logs the real confirmation state:
  - `Confirmed`
  - `AlreadyConfirmed`
  - `LinkExpired`
  - `RequiresSignIn`
  - `NetflixError`
  - `AwaitingFinalConfirmation`
- Captures profile, device, and request time from Netflix pages or email content.
- Distinguishes new messages in the same Gmail thread using Netflix's request timestamp, for example `26 April 7:57 pm GMT+7`.
- Redacts sensitive Netflix URL query tokens in logs. Logs only the URL domain/path.

## How It Works

1. The service starts Chromium in headless mode.
2. The service logs into the configured IMAP account.
3. Every `pollIntervalSeconds`, it searches the configured IMAP folder/label only.
4. It finds the newest Netflix Household email inside the configured label and `maxEmailAge` window.
5. It reads the Netflix request timestamp from the email body, for example:

```text
We received a request to update the Netflix household for your account on 26 April 7:57 pm GMT+7.
```

6. It uses that timestamp to distinguish emails that Gmail groups into the same conversation thread.
7. It opens the Netflix link, clicks the required buttons, and writes the final result to logs.

## Requirements

- Ubuntu 24.04 LTS or similar.
- .NET SDK 10.0.
- Chromium + ChromeDriver.
- A Gmail/IMAP account that receives the Netflix Household emails.
- Gmail 2-Step Verification enabled with an App Password.

Recommended Droplet size:

- Minimum: `1 vCPU / 1 GB RAM` with 2 GB swap.
- Stable recommendation: `1 vCPU / 2 GB RAM`.
- If this is the only script on the server, 2 GB RAM is comfortably enough.

## Gmail IMAP

For Gmail:

```text
IMAP server: imap.gmail.com
IMAP port: 993
Username: your Gmail address
Password: Google App Password, not your main Gmail password
```

Create a Gmail App Password:

1. Open your Google Account settings.
2. Enable `2-Step Verification`.
3. Open `App passwords`.
4. Create an app password, for example named `Netflix bot`.
5. Use the generated 16-character password as the IMAP password.

## Deploy On A Droplet

Clone the repository:

```bash
git clone https://github.com/ntmq-j/netflix-household-auto.git
cd ~/netflix-household-auto
```

Run deployment:

```bash
chmod +x deploy-droplet.sh
sudo ./deploy-droplet.sh \
  --imap-server imap.gmail.com \
  --imap-port 993 \
  --imap-username your-gmail@gmail.com \
  --imap-folder NetflixHousehold \
  --imap-password 'your-google-app-password' \
  --max-email-age 1800 \
  --max-search-results 20 \
  --page-load-timeout 90 \
  --poll-interval 10 \
  --error-retry-delay 120 \
  --debug-mode false
```

If you do not want the password to appear in shell history, omit `--imap-password`. The script will prompt for it securely:

```bash
sudo ./deploy-droplet.sh \
  --imap-server imap.gmail.com \
  --imap-port 993 \
  --imap-username your-gmail@gmail.com \
  --imap-folder NetflixHousehold
```

## Update An Existing Droplet Install

Use this update flow so your existing `/opt/netflix-household-confirmator/appsettings.json` is not overwritten:

```bash
sudo systemctl stop netflix-household-confirmator.service

cd ~/netflix-household-auto
git pull --ff-only

dotnet publish -c Release -o .publish

sudo rsync -av --delete --exclude='appsettings.json' .publish/ /opt/netflix-household-confirmator/
sudo chown -R netflixbot:netflixbot /opt/netflix-household-confirmator
sudo chmod 600 /opt/netflix-household-confirmator/appsettings.json

sudo systemctl restart netflix-household-confirmator.service
journalctl -u netflix-household-confirmator.service -f
```

## Manage The Service

Check status:

```bash
systemctl status netflix-household-confirmator.service --no-pager -l
```

Follow logs in real time:

```bash
journalctl -u netflix-household-confirmator.service -f
```

Restart:

```bash
sudo systemctl restart netflix-household-confirmator.service
```

Stop:

```bash
sudo systemctl stop netflix-household-confirmator.service
```

Default log file when deployed with `deploy-droplet.sh`:

```bash
tail -f /var/log/netflix-household-confirmator/logfile.log
```

If `logFilePath` is set to `logfile.log`, the log file is written under the service `WorkingDirectory`:

```bash
tail -f /opt/netflix-household-confirmator/logfile.log
```

## Reading Logs

Real success:

```text
Operation=HouseholdConfirmation,OperationStatus=SUCCESS,Message=The household was successfully confirmed.,ConfirmationStatus=Confirmed,PageHeading=YouÃ¢â‚¬â„¢ve updated your Netflix household
```

Expired or invalid link:

```text
Operation=HouseholdConfirmation,OperationStatus=FAILURE,Message=Netflix returned terminal confirmation status: LinkExpired.,ConfirmationStatus=LinkExpired,PageHeading=This link is no longer valid
```

Bot is on the final confirmation page:

```text
ConfirmationStatus=AwaitingFinalConfirmation,PageHeading=Finish updating the Netflix household for this account
```

Request metadata:

```text
RequestedByProfile=BOSS
RequestedFromDevice=Samsung - Smart TV, Apple iPhone 12 Pro Max, PC Chrome - Web browser and other devices
RequestedAt=26 April 7:57 pm GMT+7
```

Email metadata:

```text
EmailDate=...
EmailIdentity=...
```

`EmailIdentity` includes the Netflix confirmation URL hash and request timestamp, which helps detect new emails inside the same Gmail thread.

## Configuration

Droplet configuration file:

```bash
/opt/netflix-household-confirmator/appsettings.json
```

Example:

```json
{
  "botSettings": {
    "pageLoadTimeout": 90,
    "pollIntervalSeconds": 10,
    "errorRetryDelaySeconds": 120
  },
  "imapSettings": {
    "server": "imap.gmail.com",
    "port": 993,
    "username": "your-gmail@gmail.com",
    "password": "your-google-app-password",
    "maxEmailAge": 1800,
    "maxSearchResultsToFetch": 20,
    "folder": "NetflixHousehold"
  },
  "debugSettings": {
    "crashScreenshotFileName": "crash.png",
    "isDebugMode": false
  },
  "nuciLoggerSettings": {
    "minimumLevel": "Debug",
    "logFilePath": "/var/log/netflix-household-confirmator/logfile.log",
    "isFileOutputEnabled": true
  }
}
```

After editing config:

```bash
sudo systemctl restart netflix-household-confirmator.service
```

## Settings Reference

| Setting | Meaning | Suggested value |
| --- | --- | --- |
| `pageLoadTimeout` | Netflix page-load timeout in seconds | `90` |
| `pollIntervalSeconds` | Delay between folder scans | `10` to `60` |
| `errorRetryDelaySeconds` | Delay before retrying after an error | `120` |
| `maxEmailAge` | Only consider Netflix requests newer than this many seconds | `1800` |
| `maxSearchResultsToFetch` | Maximum matching emails to fetch from the configured label on each scan | `10` to `20` |
| `folder` | IMAP folder/Gmail label to scan. No fallback is used. | `NetflixHousehold` |
| `isDebugMode` | `true` runs a visible browser, `false` runs headless | Use `false` on Droplets |
| `logFilePath` | Log file location | `/var/log/netflix-household-confirmator/logfile.log` |

## Troubleshooting

### Service runs but does not detect a new email

Follow the logs:

```bash
journalctl -u netflix-household-confirmator.service -f
```

If you only see:

```text
Retrieved X Netflix candidate email(s) from configured IMAP folder: NetflixHousehold=...
```

possible causes are:

- The Gmail label name in `imapSettings.folder` is wrong.
- The label is hidden from IMAP in Gmail settings.
- Your Gmail filter did not apply the label to the new Netflix email.
- The service could not parse the request timestamp.
- `maxEmailAge` is too short.
- `maxSearchResultsToFetch` is too low for a very active label/thread.

First verify the label name and IMAP visibility in Gmail. If the email is old, try increasing `maxEmailAge` to 3600:

```bash
sudo nano /opt/netflix-household-confirmator/appsettings.json
sudo systemctl restart netflix-household-confirmator.service
```

### Link expired

Log example:

```text
ConfirmationStatus=LinkExpired
PageHeading=This link is no longer valid
```

This means Netflix says the link is expired or invalid. Request a new confirmation email.

### ChromeDriver error: `unknown flag port`

Find the snap Chromium ChromeDriver and symlink it:

```bash
DRV="$(sudo find /snap/chromium -type f -name chromedriver 2>/dev/null | head -n 1)"
echo "DRV=$DRV"
sudo ln -sf "$DRV" /usr/local/bin/chromedriver
/usr/local/bin/chromedriver --version
sudo systemctl restart netflix-household-confirmator.service
```

### Chrome missing shared libraries such as `libatk-1.0.so.0`

Install browser dependencies:

```bash
sudo apt-get update
sudo apt-get install -y \
  libatk1.0-0 libatk-bridge2.0-0 libgtk-3-0 libnss3 \
  libx11-xcb1 libxcomposite1 libxdamage1 libxrandr2 \
  libgbm1 libdrm2 libxkbcommon0 libxshmfence1 \
  fonts-liberation ca-certificates
```

Important: do not put extra characters after a line-continuation `\`. If you copy it as `\  lib...`, apt may report `Unable to locate package`.

### Check Chromium/ChromeDriver processes

```bash
systemctl status netflix-household-confirmator.service --no-pager -l
```

You should see processes similar to:

```text
/usr/bin/dotnet /opt/netflix-household-confirmator/NetflixHouseholdConfirmator.dll
/usr/local/bin/chromedriver --port=...
/snap/chromium/current/usr/lib/chromium-browser/chrome --headless ...
```

## Local Development

Build:

```bash
dotnet build
```

Run:

```bash
dotnet run
```

Publish:

```bash
dotnet publish -c Release -o .publish
```

## Security Notes

- Do not commit a real `appsettings.json` containing your Gmail password.
- Use a Google App Password, not your main Gmail password.
- Logs do not include Netflix query tokens, only URL paths.
- Keep the droplet config file permission at `600`.
- DigitalOcean charges for powered-off Droplets. Destroy the Droplet to stop charges.

## License

The original project is licensed under GPLv3. See [LICENSE](./LICENSE).
