#!/usr/bin/env bash
# ============================================================================
# Refresh-Tasks.sh — Auto-generates per-task markdown files from JSONL logs
#
# Bash translation of Refresh-Tasks.ps1
# Requires: jq, bash 3.2+
#
# Files generated per task folder:
#   - prompts.md   — All known prompts (competition + sandbox), deduplicated
#   - runs.md      — Latest competition + sandbox results, API calls, errors
#   - history.md   — Score progression across submissions
#
# Usage:
#   ./scripts/Refresh-Tasks.sh
# ============================================================================

set -euo pipefail
export LC_NUMERIC=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LOGS_DIR="$ROOT/src/logs"
TASKS_DIR="$ROOT/tasks"

# Check jq is available
if ! command -v jq &>/dev/null; then
    echo "ERROR: jq is required but not found. Install with: brew install jq"
    exit 1
fi

# --- Task ID → folder mapping (bash 3.2 compatible) ---
get_task_folder() {
    case "$1" in
        01) echo "01-create-employee-basic" ;;
        02) echo "02-create-customer" ;;
        03) echo "03-create-product" ;;
        04) echo "04-create-supplier" ;;
        05) echo "05-create-department-multi" ;;
        06) echo "06-create-invoice-simple" ;;
        07) echo "07-register-payment-simple" ;;
        08) echo "08-create-project-basic" ;;
        09) echo "09-create-invoice-multiline" ;;
        10) echo "10-register-payment-create-pay" ;;
        11) echo "11-create-voucher-supplier-inv" ;;
        12) echo "12-run-payroll" ;;
        13) echo "13-create-travel-expense" ;;
        14) echo "14-create-credit-note" ;;
        15) echo "15-create-project-fixed-price" ;;
        16) echo "16-create-project-timesheet" ;;
        17) echo "17-create-voucher-dimension" ;;
        18) echo "18-register-payment-full-chain" ;;
        19) echo "19-create-employee-pdf-contract" ;;
        20) echo "20-create-voucher-pdf-supplier" ;;
        21) echo "21-create-employee-pdf-offer" ;;
        22) echo "22-create-voucher-pdf-receipt" ;;
        23) echo "23-bank-reconciliation-csv" ;;
        24) echo "24-create-voucher-ledger-correction" ;;
        25) echo "25-register-payment-overdue-reminder" ;;
        26) echo "26-annual-accounts-monthly-close" ;;
        27) echo "27-register-payment-fx-eur" ;;
        28) echo "28-create-project-cost-analysis" ;;
        29) echo "29-create-project-lifecycle" ;;
        30) echo "30-create-voucher-annual-accounts" ;;
    esac
}

# Task metadata lookup functions (bash 3.2 compatible)
get_task_tier() {
    case "$1" in
        01|02|03|04|05) echo 1 ;;
        06|07|08|09|10|11|12|13|14|15|16|17|18) echo 2 ;;
        *) echo 3 ;;
    esac
}

get_task_type() {
    case "$1" in
        01|19|21) echo "create_employee" ;;
        02) echo "create_customer" ;;
        03) echo "create_product" ;;
        04) echo "create_supplier" ;;
        05) echo "create_department" ;;
        06|09) echo "create_invoice" ;;
        07|10|18|25|27) echo "register_payment" ;;
        08|15|16|28|29) echo "create_project" ;;
        11|17|20|22|24|30) echo "create_voucher" ;;
        12) echo "run_payroll" ;;
        13) echo "create_travel_expense" ;;
        14) echo "create_credit_note" ;;
        23) echo "bank_reconciliation" ;;
        26) echo "annual_accounts" ;;
    esac
}

get_task_variant() {
    case "$1" in
        01) echo "Basic" ;; 02) echo "Standard" ;; 03) echo "Standard" ;;
        04) echo "Standard" ;; 05) echo "Multi" ;; 06) echo "Simple" ;;
        07) echo "Simple existing" ;; 08) echo "Basic" ;; 09) echo "Multi-line" ;;
        10) echo "Create + pay" ;; 11) echo "Supplier invoice" ;; 12) echo "Standard" ;;
        13) echo "With costs" ;; 14) echo "Standard" ;; 15) echo "Fixed-price" ;;
        16) echo "Timesheet hours" ;; 17) echo "Custom dimension" ;; 18) echo "Full chain" ;;
        19) echo "PDF contract (T3)" ;; 20) echo "PDF supplier inv (T3)" ;;
        21) echo "PDF offer letter (T3)" ;; 22) echo "PDF receipt (T3)" ;;
        23) echo "CSV (T3)" ;; 24) echo "Ledger correction (T3)" ;;
        25) echo "Overdue + reminder (T3)" ;; 26) echo "Monthly close (T3)" ;;
        27) echo "FX/EUR (T3)" ;; 28) echo "Cost analysis (T3)" ;;
        29) echo "Full lifecycle (T3)" ;; 30) echo "Annual accounts (T3)" ;;
    esac
}

get_task_leader_max() {
    case "$1" in
        01) echo "2.00" ;; 02) echo "2.00" ;; 03) echo "2.00" ;;
        04) echo "2.00" ;; 05) echo "2.00" ;; 06) echo "1.67" ;;
        07) echo "2.00" ;; 08) echo "2.00" ;; 09) echo "4.00" ;;
        10) echo "4.00" ;; 11) echo "4.00" ;; 12) echo "4.00" ;;
        13) echo "2.40" ;; 14) echo "4.00" ;; 15) echo "3.33" ;;
        16) echo "3.00" ;; 17) echo "3.50" ;; 18) echo "4.00" ;;
        19) echo "2.73" ;; 20) echo "2.40" ;; 21) echo "2.57" ;;
        22) echo "0.00" ;; 23) echo "0.60" ;; 24) echo "2.25" ;;
        25) echo "6.00" ;; 26) echo "6.00" ;; 27) echo "6.00" ;;
        28) echo "1.50" ;; 29) echo "2.73" ;; 30) echo "1.80" ;;
    esac
}

# Sorted task IDs
TASK_IDS=(01 02 03 04 05 06 07 08 09 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30)

# --- Helper: Truncate string ---
truncate_str() {
    local text="$1"
    local max="${2:-200}"
    if [[ ${#text} -le $max ]]; then
        echo "$text"
    else
        echo "${text:0:$max}..."
    fi
}

hash_text() {
    if command -v md5sum >/dev/null 2>&1; then
        md5sum | cut -c1-32
    elif command -v md5 >/dev/null 2>&1; then
        md5 -q
    else
        shasum -a 256 | cut -d' ' -f1 | cut -c1-32
    fi
}

# --- Helper: Write file only if content changed ---
UPDATED_FILES=0
SKIPPED_FILES=0

write_if_changed() {
    local path="$1"
    local content="$2"

    if [[ -f "$path" ]]; then
        local existing
        existing="$(cat "$path")"
        # Normalize trailing whitespace for comparison
        local norm_existing norm_content
        norm_existing="$(printf '%s' "$existing" | sed 's/[[:space:]]*$//')"
        norm_content="$(printf '%s' "$content" | sed 's/[[:space:]]*$//')"
        if [[ "$norm_existing" == "$norm_content" ]]; then
            ((SKIPPED_FILES++)) || true
            return
        fi
    fi
    printf '%s' "$content" > "$path"
    ((UPDATED_FILES++)) || true
}

# --- Helper: Get task ID from prompt text ---
get_task_id_from_prompt() {
    local prompt="$1"
    local has_files="${2:-false}"

    [[ -z "$prompt" ]] && return 1

    # Task 25: overdue invoice + reminder fee
    if echo "$prompt" | grep -iqE 'retard|overdue|forfalt|forfalte|überfällig|uberfallig|vencid|rappel|reminder|purring|purregebyr|mahngeb[uü]hr|recordatorio'; then
        echo "25"; return 0
    fi

    # Task 26: monthly closing
    if echo "$prompt" | grep -iqE 'cierre mensual|monthly close|month-end|month end|månedsavslutning|månadsslutt|månedsslutt|cl[oô]ture mensuelle|fechamento mensal'; then
        echo "26"; return 0
    fi

    # Task 30: annual/year-end closing
    if echo "$prompt" | grep -iqE 'årsoppgjer|årsoppgjør|annual accounts|year-end|year end|cl[oô]ture annuelle|Jahresabschluss|fechamento contábil|cierre contable anual'; then
        echo "30"; return 0
    fi

    return 1
}

# --- Helper: Get task ID from a JSONL entry (as JSON string) ---
get_task_id() {
    local entry_json="$1"

    local task_type prompt has_files file_count

    task_type="$(echo "$entry_json" | jq -r '.task_type // empty')"
    prompt="$(echo "$entry_json" | jq -r '.prompt // empty')"
    file_count="$(echo "$entry_json" | jq -r '(.files // []) | length')"
    has_files="false"
    [[ "$file_count" -gt 0 ]] && has_files="true"

    # Unknown or missing type → try prompt heuristics
    if [[ -z "$task_type" || "$task_type" == "unknown" ]]; then
        get_task_id_from_prompt "$prompt" "$has_files" && return 0
        return 1
    fi

    # Simple unique task types → direct mapping
    case "$task_type" in
        create_customer)       echo "02"; return 0 ;;
        create_product)        echo "03"; return 0 ;;
        create_supplier)       echo "04"; return 0 ;;
        create_department)     echo "05"; return 0 ;;
        run_payroll)           echo "12"; return 0 ;;
        create_travel_expense) echo "13"; return 0 ;;
        create_credit_note)    echo "14"; return 0 ;;
        bank_reconciliation)   echo "23"; return 0 ;;
        set_fixed_price)       echo "15"; return 0 ;;
        reminder_fee|overdue_invoice_reminder) echo "25"; return 0 ;;
    esac

    # Multi-variant types — try prompt heuristics first
    local prompt_id
    if prompt_id="$(get_task_id_from_prompt "$prompt" "$has_files")"; then
        echo "$prompt_id"; return 0
    fi

    case "$task_type" in
        create_employee)
            if [[ "$has_files" == "true" ]] && echo "$prompt" | grep -iqE 'carta de oferta|offer letter'; then
                echo "21"; return 0
            fi
            if [[ "$has_files" == "true" ]]; then
                echo "19"; return 0
            fi
            echo "01"; return 0
            ;;
        create_invoice)
            if echo "$prompt" | grep -iqE 'linje|line|línea|ligne|Zeile|produto' && echo "$prompt" | grep -iqE '[0-9]+[[:space:]]*(stk|x|×|units)'; then
                echo "09"; return 0
            fi
            # Check for multiple orderLines in extraction
            local ol_count
            ol_count="$(echo "$entry_json" | jq -r '[.extraction.entities[]? | select(.orderLines) | .orderLines | length] | add // 0')"
            if [[ "$ol_count" -gt 1 ]]; then
                echo "09"; return 0
            fi
            echo "06"; return 0
            ;;
        register_payment)
            if echo "$prompt" | grep -iqE 'EUR|USD|valuta|currency|devise|Währung|câmbio|veksel'; then
                echo "27"; return 0
            fi
            if echo "$prompt" | grep -iqE 'kunde|customer|client|Kunde|cliente' && echo "$prompt" | grep -iqE 'faktura|invoice|facture|Rechnung|fatura'; then
                echo "18"; return 0
            fi
            if echo "$prompt" | grep -iqE 'opprett|create|créer|erstellen|criar' && echo "$prompt" | grep -iqE 'betal|pay|payer|zahlen|pagar'; then
                echo "10"; return 0
            fi
            echo "07"; return 0
            ;;
        create_project)
            if echo "$prompt" | grep -iqE 'kostnad|cost|coût|Kosten|custo|auka|increase'; then
                echo "28"; return 0
            fi
            if echo "$prompt" | grep -iqE 'livssyklus|lifecycle|lebenszyklus|ciclo|cycle de vie|timer.*faktura|hours.*invoice'; then
                echo "29"; return 0
            fi
            if echo "$prompt" | grep -iqE 'fastpris|fixed.price|prix fixe|Festpreis|preço fixo'; then
                echo "15"; return 0
            fi
            if echo "$prompt" | grep -iqE 'timer|timesheet|hours|heures|Stunden|horas'; then
                echo "16"; return 0
            fi
            echo "08"; return 0
            ;;
        create_voucher)
            if [[ "$has_files" == "true" ]] && echo "$prompt" | grep -iqE 'kvittering|receipt|reçu|Quittung|recibo'; then
                echo "22"; return 0
            fi
            if [[ "$has_files" == "true" ]] && echo "$prompt" | grep -iqE 'leverandør|supplier|fournisseur|Lieferant|fornecedor|facture|faktura|invoice|Rechnung'; then
                echo "20"; return 0
            fi
            if echo "$prompt" | grep -iqE 'årsoppgjer|annual|annuel|Jahres|anual'; then
                echo "30"; return 0
            fi
            if echo "$prompt" | grep -iqE 'korriger|correct|corriger|korrigieren|corrigir|Hauptbuch|ledger|grand livre'; then
                echo "24"; return 0
            fi
            if echo "$prompt" | grep -iqE 'dimensjon|dimension|Dimension|dimensão'; then
                echo "17"; return 0
            fi
            echo "11"; return 0
            ;;
        annual_accounts)
            if echo "$prompt" | grep -iqE 'cierre mensual|monthly close|month-end|month end|månedsavslutning|månadsslutt|månedsslutt|cl[oô]ture mensuelle|fechamento mensal'; then
                echo "26"; return 0
            fi
            echo "30"; return 0
            ;;
    esac

    return 1
}

# --- Load all JSONL data and classify entries by task ID ---
echo "Loading logs..."

# Create temp dir for intermediate files
TMPDIR_WORK="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_WORK"' EXIT

# Count entries
count_lines() {
    if [[ -f "$1" ]]; then
        grep -c . "$1" 2>/dev/null || echo "0"
    else
        echo "0"
    fi
}

sub_count="$(count_lines "$LOGS_DIR/submissions.jsonl")"
sand_count="$(count_lines "$LOGS_DIR/sandbox.jsonl")"
res_count="$(count_lines "$LOGS_DIR/results.jsonl")"
val_count="$(count_lines "$LOGS_DIR/validations.jsonl")"

echo "  submissions: $sub_count, sandbox: $sand_count, results: $res_count, validations: $val_count"

# --- Process each JSONL entry and write to per-task temp files ---
# We process line-by-line and bucket entries by task ID

classify_entries() {
    local source_file="$1"
    local prefix="$2"  # "sub" or "sand"

    [[ -f "$source_file" ]] || return 0

    while IFS= read -r line; do
        [[ -z "$line" || "$line" =~ ^[[:space:]]*$ ]] && continue

        local task_id
        if task_id="$(get_task_id "$line")"; then
            echo "$line" >> "$TMPDIR_WORK/${prefix}_${task_id}.jsonl"
        fi
    done < "$source_file"
}

echo "Classifying submissions..."
classify_entries "$LOGS_DIR/submissions.jsonl" "sub"
echo "Classifying sandbox..."
classify_entries "$LOGS_DIR/sandbox.jsonl" "sand"

# --- Process results.jsonl — extract per-task results ---
echo "Classifying results..."
if [[ -f "$LOGS_DIR/results.jsonl" ]]; then
    while IFS= read -r line; do
        [[ -z "$line" || "$line" =~ ^[[:space:]]*$ ]] && continue

        # Extract tasks array and iterate
        task_count="$(echo "$line" | jq -r '(.tasks // []) | length')"

        for ((ti=0; ti<task_count; ti++)); do
            task_json="$(echo "$line" | jq -c ".tasks[$ti]")"
            # Build a fake entry for classification
            fake_entry="$(echo "$task_json" | jq -c '{task_type: .task_type, prompt: .prompt, files: [], extraction: null}')"

            task_id=""
            if task_id="$(get_task_id "$fake_entry")"; then
                # Store result with parent metadata
                result_entry=""
                result_entry="$(echo "$line" | jq -c --argjson task "$task_json" '{
                    submission_id: .submission_id,
                    timestamp: .timestamp,
                    score_raw: .score_raw,
                    score_max: .score_max,
                    normalized_score: .normalized_score,
                    checks: .checks,
                    task: $task
                }')"
                echo "$result_entry" >> "$TMPDIR_WORK/res_${task_id}.jsonl"
            fi
        done
    done < "$LOGS_DIR/results.jsonl"
fi

# --- Process validations.jsonl ---
echo "Classifying validations..."
if [[ -f "$LOGS_DIR/validations.jsonl" ]]; then
    while IFS= read -r line; do
        [[ -z "$line" || "$line" =~ ^[[:space:]]*$ ]] && continue
        fake_entry="$(echo "$line" | jq -c '{task_type: .task_type, prompt: .prompt, files: [], extraction: null}')"
        task_id=""
        if task_id="$(get_task_id "$fake_entry")"; then
            echo "$line" >> "$TMPDIR_WORK/val_${task_id}.jsonl"
        fi
    done < "$LOGS_DIR/validations.jsonl"
fi

# --- Generate markdown files per task ---
GENERATED=0

# Helper: generate API calls table from a JSON entry
render_api_calls() {
    local entry="$1"
    local call_count
    call_count="$(printf '%s\n' "$entry" | jq -r '(.api_calls // []) | length')"
    [[ "$call_count" -eq 0 ]] && return

    local md="### API Calls\n\n"
    md+="| # | Method | Path | Status | Time |\n|---|---|---|---|---|\n"

    while IFS=$'\t' read -r call_no method raw_path status time_ms; do
        [[ -z "$call_no" ]] && continue
        local path status_icon
        path="$(truncate_str "$raw_path" 60)"

        if [[ "$status" -ge 400 ]]; then
            status_icon="❌ $status"
        else
            status_icon="✅ $status"
        fi

        local time_str=""
        [[ -n "$time_ms" && "$time_ms" != "null" ]] && time_str="${time_ms}ms"

        md+="| $call_no | \`$method\` | \`$path\` | $status_icon | $time_str |\n"
    done < <(
        printf '%s\n' "$entry" | jq -r '
            (.api_calls // [])
            | to_entries[]?
            | [
                (.key + 1),
                (.value.Method // .value.method // ""),
                (.value.Path // .value.path // ""),
                (.value.Status // .value.status_code // 0),
                (.value.ElapsedMs // .value.elapsed_ms // "")
            ]
            | @tsv
        '
    )
    md+="\n"

    local error_indices
    error_indices="$(printf '%s\n' "$entry" | jq -r '[.api_calls // [] | to_entries[] | select((.value.Status // .value.status_code // 0) >= 400) | .key] | .[]')"
    if [[ -n "$error_indices" ]]; then
        md+="### Error Responses\n\n"
        for ei in $error_indices; do
            local err_method err_path err_status err_body
            err_method="$(printf '%s\n' "$entry" | jq -r ".api_calls[$ei].Method // .api_calls[$ei].method // \"\"")"
            err_path="$(printf '%s\n' "$entry" | jq -r ".api_calls[$ei].Path // .api_calls[$ei].path // \"\"")"
            err_status="$(printf '%s\n' "$entry" | jq -r ".api_calls[$ei].Status // .api_calls[$ei].status_code // 0")"
            err_body="$(printf '%s\n' "$entry" | jq -r ".api_calls[$ei].ResponseSnippet // .api_calls[$ei].response_body // empty")"

            md+="**$err_method $err_path** → $err_status\n\n"
            if [[ -n "$err_body" ]]; then
                err_body="$(truncate_str "$err_body" 500)"
                md+="\`\`\`json\n$err_body\n\`\`\`\n\n"
            fi
        done
    fi

    printf '%s' "$md"
}

format_bool() {
    case "$1" in
        true) echo "True" ;;
        false) echo "False" ;;
        *) echo "$1" ;;
    esac
}

# Helper: render run summary table
render_run_summary() {
    local entry="$1"
    local md=""

    local timestamp task_type handler success elapsed_ms call_count error_count error_msg
    timestamp="$(printf '%s\n' "$entry" | jq -r '.timestamp // ""')"
    task_type="$(printf '%s\n' "$entry" | jq -r '.task_type // ""')"
    handler="$(printf '%s\n' "$entry" | jq -r '.handler // ""')"
    success="$(printf '%s\n' "$entry" | jq -r 'if .success == true then "True" elif .success == false then "False" else "" end')"
    elapsed_ms="$(printf '%s\n' "$entry" | jq -r '.elapsed_ms // ""')"
    call_count="$(printf '%s\n' "$entry" | jq -r '.call_count // ""')"
    error_count="$(printf '%s\n' "$entry" | jq -r '.error_count // ""')"
    error_msg="$(printf '%s\n' "$entry" | jq -r '.error // empty')"

    md+="| Field | Value |\n|---|---|\n"
    md+="| Timestamp | $timestamp |\n"
    md+="| Task Type | \`$task_type\` |\n"
    md+="| Handler | \`$handler\` |\n"
    md+="| Success | $success |\n"
    md+="| Elapsed | $elapsed_ms ms |\n"
    md+="| API Calls | $call_count |\n"
    md+="| Errors | $error_count |\n"
    if [[ -n "$error_msg" ]]; then
        error_msg="$(truncate_str "$error_msg" 150)"
        md+="| Error | \`$error_msg\` |\n"
    fi
    md+="\n"

    printf '%s' "$md"
}

for id in "${TASK_IDS[@]}"; do
    folder="$(get_task_folder "$id")"
    dir="$TASKS_DIR/$folder"
    [[ -d "$dir" ]] || continue

    sub_file="$TMPDIR_WORK/sub_${id}.jsonl"
    sand_file="$TMPDIR_WORK/sand_${id}.jsonl"
    res_file="$TMPDIR_WORK/res_${id}.jsonl"
    val_file="$TMPDIR_WORK/val_${id}.jsonl"

    # ========== prompts.md ==========
    prompt_md="# prompts — task $id\n\n"
    prompt_md+="*Auto-generated by Refresh-Tasks.sh*\n\n"

    # Collect unique prompts from submissions + sandbox
    SEEN_FILE="$TMPDIR_WORK/seen_${id}.txt"
    > "$SEEN_FILE"
    prompt_entries=()

    collect_prompts() {
        local file="$1"
        local source="$2"
        [[ -f "$file" ]] || return 0

        while IFS= read -r line; do
            local p
            p="$(echo "$line" | jq -r '.prompt // empty')"
            [[ -z "$p" ]] && continue

            # Use a hash for dedup
            local hash
            hash="$(printf '%s' "$p" | hash_text)"
            grep -qx "$hash" "$SEEN_FILE" 2>/dev/null && continue
            echo "$hash" >> "$SEEN_FILE"

            local fc lang
            fc="$(echo "$line" | jq -r '(.files // []) | length')"
            lang="$(echo "$line" | jq -r '.language // empty')"
            local file_info=""
            [[ "$fc" -gt 0 ]] && file_info=" (+ $fc file(s))"

            prompt_entries+=("$(jq -nc --arg source "$source" --arg lang "$lang" --arg fileinfo "$file_info" --arg prompt "$p" '{source:$source,lang:$lang,fileinfo:$fileinfo,prompt:$prompt}')")
        done < "$file"
    }

    collect_prompts "$sub_file" "competition"
    collect_prompts "$sand_file" "sandbox"

    if [[ ${#prompt_entries[@]} -eq 0 ]]; then
        prompt_md+="No prompts recorded yet.\n"
    else
        prompt_md+="${#prompt_entries[@]} unique prompt(s) found.\n\n"
        pi=0
        for pe_json in "${prompt_entries[@]}"; do
            ((pi++)) || true
            pe_source="$(echo "$pe_json" | jq -r '.source')"
            pe_lang="$(echo "$pe_json" | jq -r '.lang // empty')"
            pe_fileinfo="$(echo "$pe_json" | jq -r '.fileinfo')"
            pe_prompt="$(echo "$pe_json" | jq -r '.prompt')"

            lang_str=""
            [[ -n "$pe_lang" ]] && lang_str=" | $pe_lang"

            prompt_md+="## Prompt $pi ($pe_source$lang_str)$pe_fileinfo\n\n"
            prompt_md+="\`\`\`text\n$pe_prompt\n\`\`\`\n\n"
        done
    fi

    write_if_changed "$dir/prompts.md" "$(printf '%b' "$prompt_md")"

    # ========== runs.md ==========
    runs_md="# runs — task $id\n\n"
    runs_md+="*Auto-generated by Refresh-Tasks.sh*\n\n"

    # Latest competition run (last line sorted by timestamp)
    if [[ -f "$sub_file" ]]; then
        latest_comp="$(jq -s 'sort_by(.timestamp) | last' "$sub_file")"
        runs_md+="## Latest Competition Run\n\n"
        runs_md+="$(render_run_summary "$latest_comp")"

        # API calls
        api_md="$(render_api_calls "$latest_comp")"
        [[ -n "$api_md" ]] && runs_md+="$api_md"$'\n'

        # Extraction
        extraction="$(echo "$latest_comp" | jq -r '.extraction // empty')"
        if [[ -n "$extraction" && "$extraction" != "null" ]]; then
            runs_md+="### LLM Extraction\n\n"
            runs_md+="\`\`\`json\n$(echo "$latest_comp" | jq -c '.extraction')\n\`\`\`\n\n"
        fi
    else
        runs_md+="## Latest Competition Run\n\nNo competition runs recorded.\n\n"
    fi

    # Latest sandbox run
    if [[ -f "$sand_file" ]]; then
        latest_sand="$(jq -s 'sort_by(.timestamp) | last' "$sand_file")"
        runs_md+="## Latest Sandbox Run\n\n"
        runs_md+="$(render_run_summary "$latest_sand")"

        # API calls (abbreviated — no error details)
        sand_call_count="$(printf '%s\n' "$latest_sand" | jq -r '(.api_calls // []) | length')"
        if [[ "$sand_call_count" -gt 0 ]]; then
            runs_md+="### API Calls\n\n"
            runs_md+="| # | Method | Path | Status | Time |\n|---|---|---|---|---|\n"
            while IFS=$'\t' read -r call_no method raw_path status time_ms; do
                [[ -z "$call_no" ]] && continue
                path="$(truncate_str "$raw_path" 60)"
                if [[ "$status" -ge 400 ]]; then status_icon="❌ $status"; else status_icon="✅ $status"; fi
                time_str=""
                [[ -n "$time_ms" && "$time_ms" != "null" ]] && time_str="${time_ms}ms"
                runs_md+="| $call_no | \`$method\` | \`$path\` | $status_icon | $time_str |\n"
            done < <(
                printf '%s\n' "$latest_sand" | jq -r '
                    (.api_calls // [])
                    | to_entries[]?
                    | [
                        (.key + 1),
                        (.value.Method // .value.method // ""),
                        (.value.Path // .value.path // ""),
                        (.value.Status // .value.status_code // 0),
                        (.value.ElapsedMs // .value.elapsed_ms // "")
                    ]
                    | @tsv
                '
            )
            runs_md+="\n"
        fi
    fi

    # Latest validation
    if [[ -f "$val_file" ]]; then
        latest_val="$(jq -s 'sort_by(.timestamp) | last' "$val_file")"
        correctness="$(echo "$latest_val" | jq -r '.correctness // ""')"
        points_earned="$(echo "$latest_val" | jq -r '.points_earned // ""')"
        max_points="$(echo "$latest_val" | jq -r '.max_points // ""')"

        runs_md+="## Latest Local Validation\n\n"
        runs_md+="| Field | Value |\n|---|---|\n"
        runs_md+="| Correctness | $correctness |\n"
        runs_md+="| Points | $points_earned / $max_points |\n\n"

        checks_count="$(echo "$latest_val" | jq -r '(.checks // []) | length')"
        if [[ "$checks_count" -gt 0 ]]; then
            runs_md+="### Checks\n\n"
            runs_md+="| Check | Expected | Actual | Passed | Points |\n|---|---|---|---|---|\n"
            for ((ci=0; ci<checks_count; ci++)); do
                field="$(echo "$latest_val" | jq -r ".checks[$ci].field // \"\"")"
                expected="$(echo "$latest_val" | jq -r ".checks[$ci].expected // \"\"")"
                actual="$(echo "$latest_val" | jq -r ".checks[$ci].actual // \"\"")"
                passed="$(echo "$latest_val" | jq -r ".checks[$ci].passed // false")"
                points="$(echo "$latest_val" | jq -r ".checks[$ci].points // 0")"
                if [[ "$passed" == "true" ]]; then icon="✅"; else icon="❌"; fi
                runs_md+="| $field | \`$expected\` | \`$actual\` | $icon | $points |\n"
            done
            runs_md+="\n"
        fi
    fi

    write_if_changed "$dir/runs.md" "$(printf '%b' "$runs_md")"

    # ========== history.md ==========
    history_md="# history — task $id\n\n"
    history_md+="*Auto-generated by Refresh-Tasks.sh*\n\n"

    # Merge sub + sand into one sorted list
    all_runs_json="[]"
    if [[ -f "$sub_file" ]]; then
        all_runs_json="$(jq -s '[.[] | {timestamp, source: "competition", success, handler, calls: .call_count, errors: .error_count, elapsed_ms}]' "$sub_file")"
    fi
    if [[ -f "$sand_file" ]]; then
        sand_runs="$(jq -s '[.[] | {timestamp, source: "sandbox", success, handler, calls: .call_count, errors: .error_count, elapsed_ms}]' "$sand_file")"
        all_runs_json="$(echo "$all_runs_json $sand_runs" | jq -s 'add | sort_by(.timestamp)')"
    else
        all_runs_json="$(echo "$all_runs_json" | jq 'sort_by(.timestamp)')"
    fi

    run_count="$(echo "$all_runs_json" | jq 'length')"

    if [[ "$run_count" -eq 0 ]]; then
        history_md+="No runs recorded.\n"
    else
        history_md+="$run_count total run(s).\n\n"
        history_md+="| # | Timestamp | Source | Success | Handler | Calls | Errors | Time |\n"
        history_md+="|---|---|---|---|---|---|---|---|\n"

        for ((ri=0; ri<run_count; ri++)); do
            r_ts="$(echo "$all_runs_json" | jq -r ".[$ri].timestamp // \"\"" | head -c 19)"
            r_source="$(echo "$all_runs_json" | jq -r ".[$ri].source // \"\"")"
            r_success="$(echo "$all_runs_json" | jq -r ".[$ri].success // \"\"")"
            r_handler="$(echo "$all_runs_json" | jq -r ".[$ri].handler // \"\"")"
            r_calls="$(echo "$all_runs_json" | jq -r ".[$ri].calls // \"\"")"
            r_errors="$(echo "$all_runs_json" | jq -r ".[$ri].errors // \"\"")"
            r_elapsed="$(echo "$all_runs_json" | jq -r ".[$ri].elapsed_ms // \"\"")"
            if [[ "$r_success" == "true" ]]; then r_icon="✅"; else r_icon="❌"; fi
            history_md+="| $((ri+1)) | $r_ts | $r_source | $r_icon | $r_handler | $r_calls | $r_errors | ${r_elapsed}ms |\n"
        done
    fi

    write_if_changed "$dir/history.md" "$(printf '%b' "$history_md")"

    ((GENERATED++)) || true
done

# ================================================================
# PRIORITY_EXECUTION_ORDER.md — Auto-regenerated from leaderboard
# ================================================================

LEADERBOARD_FILE="$LOGS_DIR/leaderboard.jsonl"
if [[ -f "$LEADERBOARD_FILE" ]] && [[ -s "$LEADERBOARD_FILE" ]]; then
    echo "  Generating PRIORITY_EXECUTION_ORDER.md..."

    latest_lb="$(tail -1 "$LEADERBOARD_FILE")"
    our_total="$(echo "$latest_lb" | jq -r '.total_best_score // 0' | xargs printf '%.2f')"

    # Parse date
    lb_date="$(echo "$latest_lb" | jq -r '.timestamp // empty' | head -c 10)"
    [[ -z "$lb_date" ]] && lb_date="$(date +%Y-%m-%d)"

    # Build best scores lookup: task_id → best_score (temp file for bash 3.2 compat)
    SCORES_FILE="$TMPDIR_WORK/best_scores.tsv"
    > "$SCORES_FILE"
    echo "$latest_lb" | jq -r '(.tasks // [])[] | [.tx_task_id, (.best_score // 0 | tostring)] | @tsv' >> "$SCORES_FILE"

    # Helper: look up best score by task ID
    get_best_score() {
        local result
        result="$(grep "^${1}	" "$SCORES_FILE" 2>/dev/null | head -1 | cut -f2)"
        echo "${result:-0}"
    }

    # Build rows — collect into temp file (jq-friendly) for sorting
    rows_json="[]"
    leader_total="0"

    for tid in "${TASK_IDS[@]}"; do
        us="$(get_best_score "$tid")"
        leader="$(get_task_leader_max "$tid")"
        gap="$(echo "$us - $leader" | bc)"
        folder="$(get_task_folder "$tid")"

        status=""
        if (( $(echo "$gap > 0" | bc -l) )); then status="✅ Leading"
        elif (( $(echo "$gap == 0 && $us > 0" | bc -l) )); then status="✅ Tied"
        elif (( $(echo "$gap == 0 && $us == 0 && $leader == 0" | bc -l) )); then status="❌ Both fail"
        elif (( $(echo "$gap >= -0.5" | bc -l) )); then status="⚠️ Behind"
        else status="❌ Failing"
        fi

        rows_json="$(echo "$rows_json" | jq -c --arg id "$tid" --arg tier "$(get_task_tier "$tid")" \
            --arg type "$(get_task_type "$tid")" --arg variant "$(get_task_variant "$tid")" \
            --arg us "$us" --arg leader "$leader" --arg gap "$gap" \
            --arg status "$status" --arg folder "$folder" \
            '. + [{id:$id, tier:($tier|tonumber), type:$type, variant:$variant, us:($us|tonumber), leader:($leader|tonumber), gap:($gap|tonumber), status:$status, folder:$folder}]')"

        leader_total="$(echo "$leader_total + $leader" | bc)"
    done

    leader_total="$(printf '%.2f' "$leader_total")"
    gap_total="$(printf '%.2f' "$(echo "$our_total - $leader_total" | bc)")"

    # Priority rows: gap < 0, sorted by gap ascending
    priority_json="$(echo "$rows_json" | jq -c '[.[] | select(.gap < 0)] | sort_by(.gap)')"
    priority_count="$(echo "$priority_json" | jq 'length')"

    # Build markdown
    md="# Priority Execution Order\n\n"
    md+="*Auto-generated by Refresh-Tasks.sh*\n\n"
    md+="**Current scores:** Us $our_total pts | Leader $leader_total pts | Gap $gap_total pts ($lb_date)\n\n"

    # Priority table
    md+="## Tasks to improve (sorted by gap)\n\n"
    md+="| # | Task | Type | Variant | Us | Leader | Gap | Status | Folder |\n"
    md+="|---|---|---|---|---:|---:|---:|---|---|\n"
    for ((pi=0; pi<priority_count; pi++)); do
        r_id="$(echo "$priority_json" | jq -r ".[$pi].id")"
        r_type="$(echo "$priority_json" | jq -r ".[$pi].type")"
        r_variant="$(echo "$priority_json" | jq -r ".[$pi].variant")"
        r_us="$(echo "$priority_json" | jq -r ".[$pi].us")"
        r_leader="$(echo "$priority_json" | jq -r ".[$pi].leader")"
        r_gap="$(echo "$priority_json" | jq -r ".[$pi].gap")"
        r_status="$(echo "$priority_json" | jq -r ".[$pi].status")"
        r_folder="$(echo "$priority_json" | jq -r ".[$pi].folder")"
        md+="| $((pi+1)) | $r_id | \`$r_type\` | $r_variant | $r_us | $r_leader | $r_gap | $r_status | [$r_folder]($r_folder/) |\n"
    done

    # Milestones
    md+="\n## Milestones\n\n"
    md+="| After task # | Cumulative recoverable | Projected total |\n"
    md+="|---|---|---|\n"
    for cp in 3 5 8 "$priority_count"; do
        [[ "$cp" -gt "$priority_count" ]] && continue
        cum_gain="$(echo "$priority_json" | jq -r "[.[:$cp][].gap | fabs] | add // 0" | xargs printf '%.2f')"
        projected="$(echo "$our_total + $cum_gain" | bc | xargs printf '%.2f')"
        md+="| #1–$cp | +$cum_gain | ~$projected pts |\n"
    done

    # Tasks at parity or leading
    md+="\n## Tasks at parity or leading (no action needed)\n\n"
    md+="| Task | Folder | Status |\n"
    md+="|---|---|---|\n"
    ok_json="$(echo "$rows_json" | jq -c '[.[] | select(.gap >= 0 and (.gap != 0 or .us != 0 or .leader != 0))]')"
    ok_count="$(echo "$ok_json" | jq 'length')"
    for ((oi=0; oi<ok_count; oi++)); do
        r_id="$(echo "$ok_json" | jq -r ".[$oi].id")"
        r_variant="$(echo "$ok_json" | jq -r ".[$oi].variant")"
        r_folder="$(echo "$ok_json" | jq -r ".[$oi].folder")"
        r_status="$(echo "$ok_json" | jq -r ".[$oi].status")"
        r_gap="$(echo "$ok_json" | jq -r ".[$oi].gap")"
        detail=""
        (( $(echo "$r_gap > 0" | bc -l) )) && detail="(+$r_gap)"
        md+="| $r_id — $r_variant | [$r_folder]($r_folder/) | $r_status $detail |\n"
    done

    # Zero-score tasks
    zero_json="$(echo "$rows_json" | jq -c '[.[] | select(.us == 0 and .leader == 0)]')"
    zero_count="$(echo "$zero_json" | jq 'length')"
    if [[ "$zero_count" -gt 0 ]]; then
        md+="\n## Zero-score tasks (both teams)\n\n"
        md+="| Task | Folder | Notes |\n"
        md+="|---|---|---|\n"
        for ((zi=0; zi<zero_count; zi++)); do
            r_id="$(echo "$zero_json" | jq -r ".[$zi].id")"
            r_variant="$(echo "$zero_json" | jq -r ".[$zi].variant")"
            r_folder="$(echo "$zero_json" | jq -r ".[$zi].folder")"
            md+="| $r_id — $r_variant | [$r_folder]($r_folder/) | Both score 0 |\n"
        done
    fi

    # All tasks reference table
    md+="\n## All Tasks\n\n"
    md+="| Task | Type | Variant | Tier | Us | Leader | Gap | Status | Folder |\n"
    md+="|:---:|---|---|:---:|:---:|:---:|---:|---|---|\n"
    all_count="$(echo "$rows_json" | jq 'length')"
    for ((ai=0; ai<all_count; ai++)); do
        r_id="$(echo "$rows_json" | jq -r ".[$ai].id")"
        r_type="$(echo "$rows_json" | jq -r ".[$ai].type")"
        r_variant="$(echo "$rows_json" | jq -r ".[$ai].variant")"
        r_tier="$(echo "$rows_json" | jq -r ".[$ai].tier")"
        r_us="$(echo "$rows_json" | jq -r ".[$ai].us")"
        r_leader="$(echo "$rows_json" | jq -r ".[$ai].leader")"
        r_gap="$(echo "$rows_json" | jq -r ".[$ai].gap")"
        r_status="$(echo "$rows_json" | jq -r ".[$ai].status")"
        r_folder="$(echo "$rows_json" | jq -r ".[$ai].folder")"
        md+="| $r_id | \`$r_type\` | $r_variant | $r_tier | $r_us | $r_leader | $r_gap | $r_status | [$r_folder]($r_folder/) |\n"
    done

    # Tier summary
    md+="\n## Tier Summary\n\n"
    md+="| Tier | Tasks | Our Total | Leader Total | Gap |\n"
    md+="|---|:---:|:---:|:---:|---:|\n"

    for tier_info in "1:Tier 1 (basic CRUD):01-05" "2:Tier 2 (multi-step):06-18" "3:Tier 3 (advanced/PDF):19-30"; do
        IFS=: read -r tier_num tier_name tier_range <<< "$tier_info"
        us_sum="$(echo "$rows_json" | jq -r "[.[] | select(.tier == $tier_num) | .us] | add // 0" | xargs printf '%.2f')"
        ld_sum="$(echo "$rows_json" | jq -r "[.[] | select(.tier == $tier_num) | .leader] | add // 0" | xargs printf '%.2f')"
        gap_sum="$(echo "$us_sum - $ld_sum" | bc | xargs printf '%.2f')"
        md+="| $tier_name | $tier_range | $us_sum | $ld_sum | $gap_sum |\n"
    done

    prio_file="$TASKS_DIR/PRIORITY_EXECUTION_ORDER.md"
    write_if_changed "$prio_file" "$(printf '%b' "$md")"
    echo "  PRIORITY_EXECUTION_ORDER.md — processed"
else
    echo "  PRIORITY_EXECUTION_ORDER.md — skipped (no leaderboard data)"
fi

echo ""
echo "Processed $GENERATED task(s): $UPDATED_FILES file(s) updated, $SKIPPED_FILES unchanged."
