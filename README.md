# Netflix Household Auto Confirmator

Bot tự động đọc email Netflix Household qua Gmail/IMAP và bấm xác nhận cập nhật hộ Netflix trên trang Netflix.

Dự án này được tối ưu cho Ubuntu/DigitalOcean Droplet nhỏ, chạy bằng Chromium headless và systemd service.

## Tính Năng

- Kết nối Gmail/IMAP và quét email gần đây.
- Tìm email có subject `Important: How to update your Netflix household`.
- Trích xuất link Netflix confirmation trong email.
- Mở link bằng Selenium + Chromium headless.
- Tự động bấm đủ 2 bước:
  - `Yes, this was me`
  - `Confirm update`
- Ghi log chi tiết trạng thái thực tế:
  - `Confirmed`
  - `AlreadyConfirmed`
  - `LinkExpired`
  - `RequiresSignIn`
  - `NetflixError`
  - `AwaitingFinalConfirmation`
- Đọc profile/device/time request từ trang Netflix hoặc email.
- Phân biệt email mới trong cùng Gmail thread bằng thời gian request của Netflix, ví dụ `26 April 7:57 pm GMT+7`.
- Không log token trong URL Netflix. Log chỉ giữ domain/path.

## Cách Hoạt Động

1. Service khởi động Chromium headless.
2. Service đăng nhập IMAP.
3. Mỗi `pollIntervalSeconds`, bot đọc các email mới trong Inbox.
4. Bot tìm email Netflix Household mới nhất trong cửa sổ `maxEmailAge`.
5. Bot đọc thời gian request trong nội dung email, ví dụ:

```text
We received a request to update the Netflix household for your account on 26 April 7:57 pm GMT+7.
```

6. Bot dùng thời gian request này để phân biệt các email trong cùng Gmail thread.
7. Bot mở link Netflix, bấm các nút cần thiết, rồi ghi log kết quả.

## Yêu Cầu

- Ubuntu 24.04 LTS hoặc tương đương.
- .NET SDK 10.0.
- Chromium + ChromeDriver.
- Gmail/IMAP account nhận email Netflix.
- Gmail đã bật 2-Step Verification và tạo App Password.

Khuyến nghị droplet:

- Tối thiểu: `1 vCPU / 1 GB RAM` kèm swap 2 GB.
- Khuyến nghị ổn định: `1 vCPU / 2 GB RAM`.
- Nếu chỉ chạy bot này, 2 GB RAM là đủ thoải mái.

## Gmail IMAP

Với Gmail:

```text
IMAP server: imap.gmail.com
IMAP port: 993
Username: địa chỉ Gmail của bạn
Password: Google App Password, không phải mật khẩu Gmail chính
```

Cách tạo App Password:

1. Vào Google Account.
2. Bật `2-Step Verification`.
3. Vào `App passwords`.
4. Tạo password cho app, ví dụ tên `Netflix bot`.
5. Dùng chuỗi password 16 ký tự đó làm IMAP password.

## Deploy Trên Droplet

Clone repo:

```bash
git clone https://github.com/ntmq-j/netflix-household-auto.git
cd ~/netflix-household-auto
```

Chạy deploy:

```bash
chmod +x deploy-droplet.sh
sudo ./deploy-droplet.sh \
  --imap-server imap.gmail.com \
  --imap-port 993 \
  --imap-username your-gmail@gmail.com \
  --imap-password 'your-google-app-password' \
  --max-email-age 1800 \
  --page-load-timeout 90 \
  --poll-interval 10 \
  --error-retry-delay 120 \
  --debug-mode false
```

Nếu không muốn password hiện trong shell history, bỏ `--imap-password`, script sẽ hỏi password và ẩn input:

```bash
sudo ./deploy-droplet.sh \
  --imap-server imap.gmail.com \
  --imap-port 993 \
  --imap-username your-gmail@gmail.com
```

## Cập Nhật Bản Mới Trên Droplet

Dùng lệnh này để update code mà không đè lên `appsettings.json` đang chứa password:

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

## Quản Lý Service

Kiểm tra trạng thái:

```bash
systemctl status netflix-household-confirmator.service --no-pager -l
```

Xem log realtime:

```bash
journalctl -u netflix-household-confirmator.service -f
```

Restart service:

```bash
sudo systemctl restart netflix-household-confirmator.service
```

Stop service:

```bash
sudo systemctl stop netflix-household-confirmator.service
```

Log file mặc định khi deploy droplet:

```bash
tail -f /var/log/netflix-household-confirmator/logfile.log
```

Nếu `logFilePath` là `logfile.log`, file log nằm trong `WorkingDirectory` của service:

```bash
tail -f /opt/netflix-household-confirmator/logfile.log
```

## Đọc Log

Success thật sự:

```text
Operation=HouseholdConfirmation,OperationStatus=SUCCESS,Message=The household was successfully confirmed.,ConfirmationStatus=Confirmed,PageHeading=You’ve updated your Netflix household
```

Link đã hết hạn hoặc đã bị Netflix vô hiệu hóa:

```text
Operation=HouseholdConfirmation,OperationStatus=FAILURE,Message=Netflix returned terminal confirmation status: LinkExpired.,ConfirmationStatus=LinkExpired,PageHeading=This link is no longer valid
```

Bot đang ở trang confirm cuối:

```text
ConfirmationStatus=AwaitingFinalConfirmation,PageHeading=Finish updating the Netflix household for this account
```

Metadata request:

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

`EmailIdentity` có chứa hash của confirmation URL và request time, giúp phân biệt email mới trong cùng Gmail thread.

## Cấu Hình

File cấu hình trên droplet:

```bash
/opt/netflix-household-confirmator/appsettings.json
```

Ví dụ:

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
    "maxEmailAge": 1800
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

Sau khi sửa config:

```bash
sudo systemctl restart netflix-household-confirmator.service
```

## Giải Thích Setting

| Setting | Ý nghĩa | Gợi ý |
| --- | --- | --- |
| `pageLoadTimeout` | Timeout khi load trang Netflix | `90` |
| `pollIntervalSeconds` | Số giây giữa mỗi lần quét email | `10` đến `60` |
| `errorRetryDelaySeconds` | Chờ bao lâu sau lỗi rồi mới retry | `120` |
| `maxEmailAge` | Chỉ đọc email trong bao nhiêu giây gần đây | `1800` |
| `isDebugMode` | `true` để chạy browser visible, `false` để headless | Droplet nên dùng `false` |
| `logFilePath` | Đường dẫn log file | `/var/log/netflix-household-confirmator/logfile.log` |

## Troubleshooting

### Service chạy nhưng không thấy email mới

Kiểm tra log:

```bash
journalctl -u netflix-household-confirmator.service -f
```

Nếu chỉ thấy:

```text
Scanned X recent inbox email(s).
```

thì có thể:

- Email Netflix không nằm trong Inbox.
- Email mới bị Gmail gom thread nhưng bot chưa đọc được request time.
- `maxEmailAge` quá ngắn.
- Gmail IMAP sync chậm.

Thử tăng `maxEmailAge` lên 3600:

```bash
sudo nano /opt/netflix-household-confirmator/appsettings.json
sudo systemctl restart netflix-household-confirmator.service
```

### Link expired

Log:

```text
ConfirmationStatus=LinkExpired
PageHeading=This link is no longer valid
```

Nghĩa là link đã hết hạn hoặc request đã bị Netflix vô hiệu hóa. Hãy request confirmation mới.

### ChromeDriver lỗi `unknown flag port`

Thử tìm ChromeDriver của snap Chromium và symlink lại:

```bash
DRV="$(sudo find /snap/chromium -type f -name chromedriver 2>/dev/null | head -n 1)"
echo "DRV=$DRV"
sudo ln -sf "$DRV" /usr/local/bin/chromedriver
/usr/local/bin/chromedriver --version
sudo systemctl restart netflix-household-confirmator.service
```

### Chrome lỗi thiếu shared library như `libatk-1.0.so.0`

Cài các dependency browser:

```bash
sudo apt-get update
sudo apt-get install -y \
  libatk1.0-0 libatk-bridge2.0-0 libgtk-3-0 libnss3 \
  libx11-xcb1 libxcomposite1 libxdamage1 libxrandr2 \
  libgbm1 libdrm2 libxkbcommon0 libxshmfence1 \
  fonts-liberation ca-certificates
```

Lưu ý: sau dấu `\` không được có ký tự thừa. Nếu copy sai thành `\  lib...` thì apt sẽ báo `Unable to locate package`.

### Kiểm tra Chromium/ChromeDriver đang chạy

```bash
systemctl status netflix-household-confirmator.service --no-pager -l
```

Bạn sẽ thấy các process:

```text
/usr/bin/dotnet /opt/netflix-household-confirmator/NetflixHouseholdConfirmator.dll
/usr/local/bin/chromedriver --port=...
/snap/chromium/current/usr/lib/chromium-browser/chrome --headless ...
```

## Chạy Local Để Dev

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

## Bảo Mật

- Không commit `appsettings.json` thật có password Gmail.
- Dùng Google App Password, không dùng mật khẩu Gmail chính.
- Log không ghi query token Netflix, chỉ ghi path URL.
- File config trên droplet nên để permission `600`.
- Droplet power off vẫn bị tính tiền trên DigitalOcean; muốn ngừng phí thì phải destroy droplet.

## License

Dự án gốc dùng GPLv3. Xem [LICENSE](./LICENSE).
