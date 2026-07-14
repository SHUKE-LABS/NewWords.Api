#!/usr/bin/env bash
#
# apply_migrations.sh — apply outstanding migration_scripts/NN_*.sql to the DB, in
# order, exactly once each. Run by the deploy pipeline on the VPS between "Copy
# files" and the service restart, so schema is ready before the new code starts.
#
# Model: a `schema_migrations` ledger table records every applied filename.
#   - First run (ledger absent): create it and seed every present script as a
#     BASELINE — recorded as applied, executed NONE. An existing production DB
#     already has 01-10 (01-08 pre-existing; 09/10 via the manual incident
#     mitigation, which MUST be applied by hand before this deploy — see README).
#   - Subsequent runs: execute each script not in the ledger, in numeric-prefix
#     order, and record it only on success.
#
# Idempotent: re-running applies nothing already in the ledger, and each script
# is itself INFORMATION_SCHEMA-guarded. A failing script aborts the run with a
# non-zero exit (before the ledger insert), which fails the deploy before restart.
#
# DB credentials are parsed from the deployed appsettings.json ConnectionString
# (override with MYSQL_HOST/PORT/USER/DB/PASSWORD). The password is passed via a
# 0600 --defaults-extra-file, never on a process command line.
#
# Env:
#   APPSETTINGS   path to appsettings.json (default: <repo>/appsettings.json next to this dir)
#   DRY_RUN=1     print the pending/baseline set without executing anything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPSETTINGS="${APPSETTINGS:-$(dirname "$SCRIPT_DIR")/appsettings.json}"
DRY_RUN="${DRY_RUN:-0}"
LEDGER_TABLE="schema_migrations"

log() { printf '%s\n' "$*"; }
fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 1; }

command -v mysql >/dev/null 2>&1 || fatal "mysql client not found on PATH"

# --- Resolve connection params (env overrides parsed ConnectionString) ---------
declare -A CS=()
if [ -f "$APPSETTINGS" ]; then
    raw_cs="$(grep -o '"ConnectionString"[[:space:]]*:[[:space:]]*"[^"]*"' "$APPSETTINGS" \
        | head -1 \
        | sed -E 's/^"ConnectionString"[[:space:]]*:[[:space:]]*"(.*)"$/\1/')"
    IFS=';' read -ra _parts <<< "${raw_cs:-}"
    for _p in "${_parts[@]}"; do
        _p="$(printf '%s' "$_p" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')"
        [ -z "$_p" ] && continue
        [[ "$_p" == *=* ]] || continue
        _key="$(printf '%s' "${_p%%=*}" | tr '[:upper:]' '[:lower:]')"
        CS["$_key"]="${_p#*=}"
    done
fi

MYSQL_HOST="${MYSQL_HOST:-${CS[server]:-}}"
MYSQL_PORT="${MYSQL_PORT:-${CS[port]:-}}"
MYSQL_USER="${MYSQL_USER:-${CS[uid]:-}}"
MYSQL_DB="${MYSQL_DB:-${CS[database]:-}}"
MYSQL_PASSWORD="${MYSQL_PASSWORD:-${CS[password]:-}}"

for _v in HOST PORT USER DB PASSWORD; do
    _name="MYSQL_$_v"
    [ -n "${!_name}" ] || fatal "$_name is empty — could not resolve it from env or $APPSETTINGS ConnectionString; refusing to connect with a missing field"
done

# --- mysql via a 0600 defaults file (keeps the password off the cmdline) -------
# --defaults-file (not --defaults-extra-file) so ONLY this file is read: a stray
# ~/.my.cnf on the host is read AFTER an extra-file and would otherwise silently
# redirect the connection to the wrong server. protocol=tcp forces the host/port
# rather than a local socket.
MY_CNF="$(mktemp)"
chmod 600 "$MY_CNF"
trap 'rm -f "$MY_CNF"' EXIT
cat > "$MY_CNF" <<EOF
[client]
protocol=tcp
host=$MYSQL_HOST
port=$MYSQL_PORT
user=$MYSQL_USER
password=$MYSQL_PASSWORD
EOF

mysql_run() { mysql --defaults-file="$MY_CNF" "$MYSQL_DB" "$@"; }

log "Target DB: $MYSQL_USER@$MYSQL_HOST:$MYSQL_PORT/$MYSQL_DB (dry-run=$DRY_RUN)"

# --- Collect scripts: numeric-prefixed *.sql only, numeric-aware order ---------
mapfile -t SCRIPTS < <(find "$SCRIPT_DIR" -maxdepth 1 -type f -name '[0-9]*_*.sql' -printf '%f\n' | sort -V)
[ "${#SCRIPTS[@]}" -gt 0 ] || fatal "no NN_*.sql migration scripts found in $SCRIPT_DIR"

ledger_exists="$(mysql_run -N -B -e \
    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='$LEDGER_TABLE';")"

# --- First run: seed baseline, execute nothing ---------------------------------
if [ "$ledger_exists" = "0" ]; then
    log "=========================================================================="
    log "  FIRST RUN: '$LEDGER_TABLE' ledger not found — SEEDING BASELINE."
    log "  The ${#SCRIPTS[@]} script(s) below are recorded as ALREADY APPLIED and are"
    log "  NOT executed. This assumes the production DB already contains them."
    log ""
    log "  >>> If any outstanding migration (e.g. 09/10) has NOT been applied to the"
    log "  >>> DB by hand, apply it MANUALLY BEFORE this deploy — otherwise the drift"
    log "  >>> is silently baked into the baseline (the exact bug this guard prevents)."
    log "=========================================================================="
    if [ "$DRY_RUN" = "1" ]; then
        printf '  [dry-run] would baseline-seed: %s\n' "${SCRIPTS[@]}"
        exit 0
    fi
    mysql_run -e "CREATE TABLE IF NOT EXISTS \`$LEDGER_TABLE\` (
        Id        BIGINT       NOT NULL AUTO_INCREMENT,
        Filename  VARCHAR(255) NOT NULL,
        AppliedAt BIGINT       NOT NULL,
        PRIMARY KEY (Id),
        UNIQUE KEY UQ_${LEDGER_TABLE}_Filename (Filename)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
    for f in "${SCRIPTS[@]}"; do
        log "  baseline-seed: $f"
        mysql_run -e "INSERT IGNORE INTO \`$LEDGER_TABLE\` (Filename, AppliedAt) VALUES ('$f', UNIX_TIMESTAMP());"
    done
    log "Baseline seeding complete; no migrations executed."
    exit 0
fi

# --- Subsequent runs: apply each script not yet in the ledger ------------------
pending=0
for f in "${SCRIPTS[@]}"; do
    already="$(mysql_run -N -B -e "SELECT COUNT(*) FROM \`$LEDGER_TABLE\` WHERE Filename='$f';")"
    [ "$already" = "0" ] || continue
    pending=1
    if [ "$DRY_RUN" = "1" ]; then
        log "  [dry-run] pending: $f"
        continue
    fi
    log "  applying: $f"
    mysql_run < "$SCRIPT_DIR/$f"
    mysql_run -e "INSERT INTO \`$LEDGER_TABLE\` (Filename, AppliedAt) VALUES ('$f', UNIX_TIMESTAMP());"
    log "  applied:  $f"
done

if [ "$pending" = "0" ]; then
    log "No pending migrations; schema is up to date."
elif [ "$DRY_RUN" = "1" ]; then
    log "Dry run complete; nothing executed."
else
    log "Migration run complete."
fi
