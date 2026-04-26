#!/usr/bin/env bash
set -euo pipefail

APP_NAME="netflix-household-confirmator"
APP_USER="${APP_USER:-netflixbot}"
INSTALL_DIR="${INSTALL_DIR:-/opt/${APP_NAME}}"
LOG_DIR="${LOG_DIR:-/var/log/${APP_NAME}}"
RUNTIME_DIR="${RUNTIME_DIR:-/tmp/runtime-${APP_USER}}"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}.service"
PUBLISH_DIR=".publish"

IMAP_SERVER="${IMAP_SERVER:-}"
IMAP_PORT="${IMAP_PORT:-993}"
IMAP_USERNAME="${IMAP_USERNAME:-}"
IMAP_PASSWORD="${IMAP_PASSWORD:-}"
IMAP_FOLDER="${IMAP_FOLDER:-NetflixHousehold}"
MAX_EMAIL_AGE="${MAX_EMAIL_AGE:-1800}"
MAX_SEARCH_RESULTS_TO_FETCH="${MAX_SEARCH_RESULTS_TO_FETCH:-20}"
PAGE_LOAD_TIMEOUT="${PAGE_LOAD_TIMEOUT:-90}"
POLL_INTERVAL_SECONDS="${POLL_INTERVAL_SECONDS:-5}"
ERROR_RETRY_DELAY_SECONDS="${ERROR_RETRY_DELAY_SECONDS:-15}"
DEBUG_MODE="${DEBUG_MODE:-false}"
CRASH_SCREENSHOT_FILE_NAME="${CRASH_SCREENSHOT_FILE_NAME:-crash.png}"
LOG_MINIMUM_LEVEL="${LOG_MINIMUM_LEVEL:-Debug}"
LOG_FILE_PATH="${LOG_FILE_PATH:-${LOG_DIR}/logfile.log}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}"

show_help() {
  cat <<'EOF'
Usage:
  sudo ./deploy-droplet.sh [options]

Options:
  --imap-server <value>         IMAP server hostname
  --imap-port <value>           IMAP port (default: 993)
  --imap-username <value>       IMAP username
  --imap-password <value>       IMAP password
  --imap-folder <value>         IMAP folder/label to scan (default: NetflixHousehold)
  --max-email-age <value>       Max email age in seconds (default: 1800)
  --max-search-results <value>  Max matching emails to fetch per scan (default: 20)
  --page-load-timeout <value>   Browser page load timeout in seconds (default: 90)
  --poll-interval <value>       Inbox polling interval in seconds (default: 5)
  --error-retry-delay <value>   Retry delay after a processing error in seconds (default: 15)
  --debug-mode <true|false>     Run browser in visible mode (default: false)
  --help                        Show this help

You can also pass values through environment variables:
  IMAP_SERVER, IMAP_PORT, IMAP_USERNAME, IMAP_PASSWORD, IMAP_FOLDER,
  MAX_EMAIL_AGE, MAX_SEARCH_RESULTS_TO_FETCH, PAGE_LOAD_TIMEOUT, POLL_INTERVAL_SECONDS,
  ERROR_RETRY_DELAY_SECONDS, DEBUG_MODE,
  CRASH_SCREENSHOT_FILE_NAME, LOG_MINIMUM_LEVEL, LOG_FILE_PATH
EOF
}

die() {
  echo "Error: $*" >&2
  exit 1
}

require_command() {
  local command_name="$1"
  command -v "${command_name}" >/dev/null 2>&1 || die "Required command not found: ${command_name}"
}

prompt_if_empty() {
  local variable_name="$1"
  local prompt_message="$2"
  local is_secret="${3:-false}"

  if [[ -n "${!variable_name}" ]]; then
    return
  fi

  if [[ ! -t 0 ]]; then
    die "Missing required value for ${variable_name}. Provide it via CLI option or environment variable."
  fi

  if [[ "${is_secret}" == "true" ]]; then
    read -r -s -p "${prompt_message}: " input_value
    echo
  else
    read -r -p "${prompt_message}: " input_value
  fi

  printf -v "${variable_name}" '%s' "${input_value}"
}

assert_boolean() {
  local value="$1"
  [[ "${value}" == "true" || "${value}" == "false" ]] || die "Invalid boolean value: ${value} (expected true or false)"
}

assert_positive_integer() {
  local value="$1"
  local label="$2"

  [[ "${value}" =~ ^[0-9]+$ ]] || die "Invalid ${label}: ${value} (expected a positive integer)"
  [[ "${value}" -gt 0 ]] || die "Invalid ${label}: ${value} (must be greater than 0)"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --imap-server)
        IMAP_SERVER="${2:-}"
        shift 2
        ;;
      --imap-port)
        IMAP_PORT="${2:-}"
        shift 2
        ;;
      --imap-username)
        IMAP_USERNAME="${2:-}"
        shift 2
        ;;
      --imap-password)
        IMAP_PASSWORD="${2:-}"
        shift 2
        ;;
      --imap-folder)
        IMAP_FOLDER="${2:-}"
        shift 2
        ;;
      --max-email-age)
        MAX_EMAIL_AGE="${2:-}"
        shift 2
        ;;
      --max-search-results)
        MAX_SEARCH_RESULTS_TO_FETCH="${2:-}"
        shift 2
        ;;
      --page-load-timeout)
        PAGE_LOAD_TIMEOUT="${2:-}"
        shift 2
        ;;
      --poll-interval)
        POLL_INTERVAL_SECONDS="${2:-}"
        shift 2
        ;;
      --error-retry-delay)
        ERROR_RETRY_DELAY_SECONDS="${2:-}"
        shift 2
        ;;
      --debug-mode)
        DEBUG_MODE="${2:-}"
        shift 2
        ;;
      --help|-h)
        show_help
        exit 0
        ;;
      *)
        die "Unknown option: $1"
        ;;
    esac
  done
}

ensure_root() {
  if [[ "${EUID}" -eq 0 ]]; then
    return
  fi

  echo "Escalating to sudo..."
  exec sudo -E bash "$0" "$@"
}

install_dotnet_sdk() {
  if apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1; then
    apt-get install -y dotnet-sdk-10.0
    return
  fi

  echo "dotnet-sdk-10.0 not in current feed, enabling Ubuntu backports..."
  apt-get install -y software-properties-common
  add-apt-repository -y ppa:dotnet/backports
  apt-get update
  apt-get install -y dotnet-sdk-10.0
}

install_browser_stack() {
  apt-get install -y snapd
  systemctl enable --now snapd

  if ! snap list chromium >/dev/null 2>&1; then
    snap install chromium
  fi

  if [[ -x "/snap/bin/chromium.chromedriver" ]] && [[ ! -x "/usr/local/bin/chromedriver" ]]; then
    ln -sf /snap/bin/chromium.chromedriver /usr/local/bin/chromedriver
  fi
}

write_appsettings() {
  mkdir -p "${INSTALL_DIR}"
  mkdir -p "$(dirname "${LOG_FILE_PATH}")"

  cat > "${INSTALL_DIR}/appsettings.json" <<EOF
{
  "botSettings": {
    "pageLoadTimeout": ${PAGE_LOAD_TIMEOUT},
    "pollIntervalSeconds": ${POLL_INTERVAL_SECONDS},
    "errorRetryDelaySeconds": ${ERROR_RETRY_DELAY_SECONDS}
  },
  "imapSettings": {
    "server": "${IMAP_SERVER}",
    "port": ${IMAP_PORT},
    "username": "${IMAP_USERNAME}",
    "password": "${IMAP_PASSWORD}",
    "maxEmailAge": ${MAX_EMAIL_AGE},
    "maxSearchResultsToFetch": ${MAX_SEARCH_RESULTS_TO_FETCH},
    "folder": "${IMAP_FOLDER}"
  },
  "debugSettings": {
    "crashScreenshotFileName": "${CRASH_SCREENSHOT_FILE_NAME}",
    "isDebugMode": ${DEBUG_MODE}
  },
  "nuciLoggerSettings": {
    "minimumLevel": "${LOG_MINIMUM_LEVEL}",
    "logFilePath": "${LOG_FILE_PATH}",
    "isFileOutputEnabled": true
  }
}
EOF

  chmod 600 "${INSTALL_DIR}/appsettings.json"
}

write_service() {
  cat > "${SERVICE_FILE}" <<EOF
[Unit]
Description=Netflix Household Confirmator
After=network-online.target snapd.service
Wants=network-online.target snapd.service

[Service]
Type=simple
User=${APP_USER}
WorkingDirectory=${INSTALL_DIR}
ExecStart=/usr/bin/dotnet ${INSTALL_DIR}/NetflixHouseholdConfirmator.dll
Restart=always
RestartSec=10
Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin
Environment=HOME=/home/${APP_USER}
Environment=XDG_RUNTIME_DIR=${RUNTIME_DIR}

[Install]
WantedBy=multi-user.target
EOF
}

main() {
  parse_args "$@"
  ensure_root "$@"

  prompt_if_empty "IMAP_SERVER" "IMAP server"
  prompt_if_empty "IMAP_USERNAME" "IMAP username"
  prompt_if_empty "IMAP_PASSWORD" "IMAP password" "true"
  prompt_if_empty "IMAP_FOLDER" "IMAP folder/label"

  assert_boolean "${DEBUG_MODE}"
  assert_positive_integer "${IMAP_PORT}" "IMAP port"
  assert_positive_integer "${MAX_EMAIL_AGE}" "max email age"
  assert_positive_integer "${MAX_SEARCH_RESULTS_TO_FETCH}" "max search results"
  assert_positive_integer "${PAGE_LOAD_TIMEOUT}" "page load timeout"
  assert_positive_integer "${POLL_INTERVAL_SECONDS}" "poll interval"
  assert_positive_integer "${ERROR_RETRY_DELAY_SECONDS}" "error retry delay"

  require_command apt-get
  require_command systemctl

  echo "Installing OS dependencies..."
  apt-get update
  apt-get install -y ca-certificates curl git
  install_dotnet_sdk
  install_browser_stack

  if ! id -u "${APP_USER}" >/dev/null 2>&1; then
    useradd -r -m -s /usr/sbin/nologin "${APP_USER}"
  fi

  echo "Publishing the application..."
  cd "${PROJECT_DIR}"
  dotnet restore
  dotnet publish -c Release -o "${PUBLISH_DIR}"

  mkdir -p "${INSTALL_DIR}" "${LOG_DIR}" "${RUNTIME_DIR}"
  cp -a "${PUBLISH_DIR}/." "${INSTALL_DIR}/"

  write_appsettings
  write_service

  chown -R "${APP_USER}:${APP_USER}" "${INSTALL_DIR}" "${LOG_DIR}" "${RUNTIME_DIR}"
  chmod 750 "${INSTALL_DIR}" "${LOG_DIR}"
  chmod 700 "${RUNTIME_DIR}"

  systemctl daemon-reload
  systemctl enable --now "${APP_NAME}.service"

  echo
  echo "Deployment complete."
  echo "Service name: ${APP_NAME}.service"
  echo "Status: systemctl status ${APP_NAME}.service --no-pager"
  echo "Logs:   journalctl -u ${APP_NAME}.service -f"
}

main "$@"
