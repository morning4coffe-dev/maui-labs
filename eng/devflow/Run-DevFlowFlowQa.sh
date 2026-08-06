#!/usr/bin/env bash
# Checked-in DevFlow flow-QA host entry point. It intentionally never installs SDKs,
# workloads, Xcode, Appium, or device tooling.
set -uo pipefail

EXIT_SUCCESS=0
EXIT_USAGE=2
EXIT_PREREQUISITE=3
EXIT_FLOW_FAILURE=4
EXIT_PENDING=5
MAX_ARTIFACTS=256
MAX_DIAGNOSTIC_BYTES=65536

usage() {
  cat <<'EOF'
Usage:
  Run-DevFlowFlowQa.sh --platform android|windows|ios|maccatalyst|macos \
    --results-root <repo>/artifacts/TestResults/devflow-flow/<platform> [options]

Required:
  --platform <name>       android, windows, ios, maccatalyst, or macos
  --results-root <path>   Exact repository-local results directory for the selected platform

Options:
  --repeat <N>            Clean repetitions (default: 3; maximum: 20)
  --configuration <name>  Test configuration (default: Debug)
  --flow-filter <filter>  Additional VSTest filter appended to the platform filter
  --no-build              Pass --no-build to dotnet test
  --qualification         Run the read-only qualification evaluator after the flow host
  --experimental          Required for the experimental AppKit/macOS lane
  --physical-device       Run the separately identified physical-iOS lane
  --device-id <id>        Android serial or required physical-iOS device identifier
  --ios-runtime <version> iOS Simulator runtime selector (for ios simulator only)
  --signing-identity <id> Physical-iOS signing identity; never written to artifacts
  --provisioning-profile <id>
                           Physical-iOS provisioning profile; never written to artifacts
  --keychain <path>       Physical-iOS keychain reference; never written to artifacts
  --apple-spike           Explicitly request the required XCTest/XCUITest capability proof
  --target-app <path>     Optional prebuilt instrumented .app; otherwise the sample is built
  --target-bundle-id <id> Approved target bundle identifier (default: com.companyname.mauitodo)
  --simulator-id <id>     iOS Simulator UDID; otherwise an available iPhone is selected
  --safe-action-id <id>   Safe proof action identifier (default: AddButton)
  --apple-in-app-agent-port <port>
                          In-app DevFlow port for the test build (default: 9223)
  --apple-spike-timeout <seconds>
                           Host/device proof timeout (default: 180; maximum: 600)
  --verbosity <level>     quiet, minimal, normal, detailed, or diagnostic
  --verbose               Alias for --verbosity detailed
  --dry-run               Validate arguments and emit the planned, non-executing command as JSON
  --help                  Show this help text

Exit codes: 0 succeeded, 2 invalid invocation, 3 prerequisite/infrastructure failure,
4 flow failure, 5 pending capability or not-qualified result.
EOF
}

die_usage() {
  printf 'flow-qa: %s\n' "$1" >&2
  usage >&2
  exit "$EXIT_USAGE"
}

json_escape() {
  local value=${1-}
  value=${value//\\/\\\\}
  value=${value//\"/\\\"}
  value=${value//$'\n'/\\n}
  value=${value//$'\r'/\\r}
  value=${value//$'\t'/\\t}
  printf '%s' "$value"
}

json_string() {
  printf '"%s"' "$(json_escape "${1-}")"
}

json_array() {
  local first=1 item
  printf '['
  for item in "$@"; do
    if (( first == 0 )); then printf ','; fi
    json_string "$item"
    first=0
  done
  printf ']'
}

is_unsafe_path() {
  local path=$1
  [[ "$path" == *'*'* || "$path" == *'?'* || "$path" == *'['* || "$path" == *']'* ]] ||
    [[ "$path" =~ (^|[\\/])\.\.($|[\\/]) ]]
}

require_value() {
  local option=$1 value=${2-}
  [[ -n "$value" && "$value" != -* ]] || die_usage "$option requires a value."
}

validate_single_line() {
  local name=$1 value=$2
  [[ -n "$value" && "$value" != *$'\n'* && "$value" != *$'\r'* ]] ||
    die_usage "$name must be a single non-empty line."
}

assert_no_symlink() {
  local target=$1 relative current segment
  relative=${target#"$repo_root"/}
  [[ "$relative" != "$target" ]] || die_usage 'The output path must remain inside the repository.'
  current=$repo_root
  IFS='/' read -r -a components <<< "$relative"
  for segment in "${components[@]}"; do
    [[ -n "$segment" ]] || continue
    current="$current/$segment"
    [[ ! -L "$current" ]] || die_usage 'The output path must not traverse a symbolic link.'
  done
}

resolve_results_root() {
  local input=$1 expected candidate relative_input
  is_unsafe_path "$input" && die_usage '--results-root must not contain wildcards or parent-directory segments.'
  expected="$repo_root/artifacts/TestResults/devflow-flow/$platform"
  if [[ "$input" = /* ]]; then
    candidate=${input%/}
  else
    relative_input=$input
    while [[ "$relative_input" == ./* ]]; do
      relative_input=${relative_input#./}
    done
    candidate="$repo_root/${relative_input%/}"
  fi
  [[ "$candidate" == "$expected" ]] ||
    die_usage "--results-root must resolve exactly to '$expected' for platform '$platform'."
  assert_no_symlink "$candidate"
  resolved_results_root=$candidate
}

resolve_artifact_root() {
  local expected configured candidate relative_configured
  expected="$repo_root/artifacts/devflow/$run_id/$platform"
  configured=
  if [[ "$platform" == android ]]; then
    configured=${DEVFLOW_FLOW_PILOT_ARTIFACT_ROOT-}
  else
    configured=${DEVFLOW_FLOW_QA_ARTIFACT_ROOT-}
  fi
  if [[ -n "$configured" ]]; then
    is_unsafe_path "$configured" && die_usage 'Configured artifact root must not contain wildcards or parent-directory segments.'
    if [[ "$configured" = /* ]]; then
      candidate=${configured%/}
    else
      relative_configured=$configured
      while [[ "$relative_configured" == ./* ]]; do
        relative_configured=${relative_configured#./}
      done
      candidate="$repo_root/${relative_configured%/}"
    fi
    [[ "$candidate" == "$expected" ]] ||
      die_usage "The configured artifact root must resolve exactly to '$expected'."
  fi
  assert_no_symlink "$expected"
  resolved_artifact_root=$expected
}

repo_relative() {
  local path=$1
  path=${path#"$repo_root"/}
  printf '%s' "$path"
}

sha256_file() {
  local path=$1
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$path" | awk '{print "sha256:" tolower($1)}'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$path" | awk '{print "sha256:" tolower($1)}'
  else
    return 1
  fi
}

sha256_string() {
  local value=$1
  if command -v shasum >/dev/null 2>&1; then
    printf '%s' "$value" | shasum -a 256 | awk '{print "sha256:" tolower($1)}'
  elif command -v sha256sum >/dev/null 2>&1; then
    printf '%s' "$value" | sha256sum | awk '{print "sha256:" tolower($1)}'
  else
    return 1
  fi
}

atomic_write() {
  local target=$1 temporary
  mkdir -p -- "$(dirname -- "$target")"
  temporary="${target}.$$.tmp"
  temporary_paths+=("$temporary")
  umask 077
  cat > "$temporary"
  mv -f -- "$temporary" "$target"
}

cleanup_temporary_paths() {
  local path
  for path in "${temporary_paths[@]-}"; do
    [[ -n "$path" ]] && rm -f -- "$path"
  done
}

redact_diagnostic_file() {
  local source=$1 target=$2
  # The raw file is mode 0600, script-owned, and removed immediately after this bounded
  # redacted projection is written. Do not echo raw test output to the terminal.
  sed -E \
    -e 's/([Tt][Oo][Kk][Ee][Nn]|[Pp][Aa][Ss][Ss][Ww][Oo][Rr][Dd]|[Ss][Ee][Cc][Rr][Ee][Tt]|[Aa][Uu][Tt][Hh][Oo][Rr][Ii][Zz][Aa][Tt][Ii][Oo][Nn]|[Aa][Pp][Ii][_-]?[Kk][Ee][Yy])[[:space:]]*[:=][[:space:]]*[^[:space:]]+/\1=[REDACTED]/g' \
    -e 's/(DEVFLOW_IOS_(SIGNING_IDENTITY|PROVISIONING_PROFILE|KEYCHAIN))[[:space:]]*[:=][[:space:]]*[^[:space:]]+/\1=[REDACTED]/g' \
    -e 's/(DEVFLOW_APPLE_AGENT_SESSION_SECRET)[[:space:]]*[:=][[:space:]]*[^[:space:]]+/\1=[REDACTED]/g' \
    "$source" | head -c "$MAX_DIAGNOSTIC_BYTES" > "$target"
}

classify_execution() {
  local exit_code=$1 source=$2
  if (( exit_code == 0 )); then
    printf 'passed'
  elif grep -Eiq '\bcapability-missing\b' "$source"; then
    printf 'capability-missing'
  elif grep -Eiq 'workload|sdk .*not found|adb .*not found|xcrun .*not found|simctl|emulator|agent readiness|fixture.*initializ|infrastructure|device.*not found|timed out|timeout' "$source"; then
    printf 'infrastructure-failure'
  else
    printf 'flow-failure'
  fi
}

build_flow_digests_json() {
  local flow_dir="$flow_directory" file first=1 digest
  printf '['
  if [[ -d "$flow_dir" ]]; then
    shopt -s nullglob
    for file in "$flow_dir"/*.md; do
      [[ "$(basename -- "$file")" == README.md ]] && continue
      digest=$(sha256_file "$file" 2>/dev/null || true)
      if (( first == 0 )); then printf ','; fi
      printf '{"path":'
      json_string "$(repo_relative "$file")"
      printf ',"sha256":'
      if [[ -n "$digest" ]]; then json_string "$digest"; else printf 'null'; fi
      printf '}'
      first=0
    done
    shopt -u nullglob
  fi
  printf ']'
}

collect_artifacts() {
  artifact_records=()
  artifact_omissions=()
  local root file relative digest kind basename count=0
  for root in "$artifact_root" "$results_root"; do
    [[ -d "$root" ]] || continue
    while IFS= read -r -d '' file; do
      basename=$(basename -- "$file")
      [[ "$basename" == *.tmp ]] && continue
      # A manifest cannot safely hash itself. It is rewritten only after all other declared
      # artifacts are collected, including the optional read-only qualification projection.
      [[ "$file" == "$manifest_path" ]] && continue
      if [[ "$root" == "$results_root" && "$basename" != *"$run_id"* ]]; then
        continue
      fi
      if (( count >= MAX_ARTIFACTS )); then
        artifact_omissions+=('{"kind":"artifact-limit","reason":"Only the first 256 artifact references were hashed."}')
        break
      fi
      relative=$(repo_relative "$file")
      digest=$(sha256_file "$file" 2>/dev/null || true)
      if [[ -z "$digest" ]]; then
        artifact_omissions+=("{\"kind\":\"artifact-hash\",\"reason\":\"An artifact could not be hashed.\",\"path\":$(json_string "$relative")}")
        continue
      fi
      case "${file##*.}" in
        trx) kind=test-results ;;
        mauitrace) kind=mauitrace ;;
        json) kind=json ;;
        *) kind=host-diagnostic ;;
      esac
      artifact_records+=("{\"kind\":$(json_string "$kind"),\"path\":$(json_string "$relative"),\"sha256\":$(json_string "$digest"),\"sizeBytes\":$(wc -c < "$file" | tr -d ' '),\"redacted\":true}")
      count=$((count + 1))
    done < <(find "$root" -type f ! -name '*.tmp' -print0)
  done
}

join_json_array() {
  local first=1 item
  printf '['
  for item in "$@"; do
    if (( first == 0 )); then printf ','; fi
    printf '%s' "$item"
    first=0
  done
  printf ']'
}

write_host_diagnostics() {
  atomic_write "$diagnostic_dir/summary.json" <<EOF
{
  "schema": 1,
  "kind": "devflow-flow-qa-host-diagnostics",
  "generatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "status": $(json_string "$status"),
  "classification": $(json_string "$classification"),
  "host": {
    "hostOs": $(json_string "$host_os"),
    "dotnetSdk": $(json_string "$dotnet_sdk"),
    "workloadVersion": $(json_string "${DOTNET_WORKLOAD_VERSION-unknown}"),
    "xcode": $(json_string "$xcode_version"),
    "runtime": $(json_string "${ios_runtime:-default}"),
    "deviceEvidence": {
      "kind": $(json_string "$device_kind"),
      "realDevice": $physical_device,
      "deviceIdFingerprint": $device_id_json,
      "profile": "not-observed"
    },
    "signing": $signing_refs_json
  }
}
EOF
}

get_testing_package_version() {
  local report="$artifact_root/apple-xctest-spike.json" version=
  if [[ -f "$report" ]]; then
    version=$(sed -n 's/.*"assemblyVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$report" | head -n 1)
  fi
  printf '%s' "${version:-unknown}"
}

write_flow_run() {
  local first_attempt=null app_digest app_digest_json=null testing_package_version
  app_digest=$(sha256_file "$app_project" 2>/dev/null || true)
  testing_package_version=$(get_testing_package_version)
  [[ -z "$app_digest" ]] || app_digest_json=$(json_string "$app_digest")
  if (( ${#attempts[@]} > 0 )); then
    first_attempt=${attempts[0]}
  fi
  atomic_write "$flow_run_path" <<EOF
{
  "schema": 1,
  "kind": "devflow-flow-qa-run",
  "generatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "repository": { "commit": $(json_string "$repository_commit") },
  "platform": $(json_string "$platform"),
  "experimental": $manifest_experimental,
  "backend": $manifest_backend,
  "officialCoverage": $manifest_official_coverage,
  "macCatalystEquivalent": $manifest_maccatalyst_equivalent,
  "app": {
    "project": $(json_string "$(repo_relative "$app_project")"),
    "sourceDigest": $app_digest_json,
    "packageDigest": null
  },
  "testing": { "packageVersion": $(json_string "$testing_package_version") },
  "flows": $(build_flow_digests_json),
  "appleQa": $apple_qa_json,
  "hostQa": {
    "runId": $(json_string "$run_id"),
    "configuration": $(json_string "$configuration"),
    "repeat": $repeat,
    "platformFilter": $(json_string "$base_filter"),
    "testFilterDigest": $(json_string "$test_filter_digest"),
    "noBuild": $no_build,
    "status": $(json_string "$status"),
    "classification": $(json_string "$classification"),
    "host": {
      "hostOs": $(json_string "$host_os"),
      "dotnetSdk": $(json_string "$dotnet_sdk"),
      "workloadVersion": $(json_string "${DOTNET_WORKLOAD_VERSION-unknown}"),
      "xcode": $(json_string "$xcode_version"),
      "runtime": $(json_string "${ios_runtime:-default}"),
      "deviceEvidence": {
        "kind": $(json_string "$device_kind"),
        "realDevice": $physical_device,
        "deviceIdFingerprint": $device_id_json,
        "profile": "not-observed"
      },
      "signing": $signing_refs_json
    },
    "resetSeed": {
      "resetFingerprint": $(json_string "$apple_reset_fingerprint"),
      "seedFingerprint": $(json_string "$apple_seed_fingerprint"),
      "backendStateFingerprint": $(json_string "$apple_backend_fingerprint")
    },
    "firstAttempt": $first_attempt,
    "cleanAttempts": $(join_json_array "${attempts[@]}"),
    "diagnosticReruns": [],
    "diagnosticRerunPolicy": "No automatic diagnostic rerun is performed because replay may mutate state.",
    "qualification": $qualification_json
    ,"appleSpike": $apple_spike_json
    ,"appleQa": $apple_qa_json
  },
  "firstAttempt": $first_attempt,
  "diagnosticReruns": [],
  "omissions": $(join_json_array "${omissions[@]}"),
  "privacy": {
    "excludedByDefault": ["screenshots", "source", "raw-model-context", "environment", "signing-inputs"]
  }
}
EOF
}

write_generic_manifest() {
  local app_digest app_digest_json=null testing_package_version
  app_digest=$(sha256_file "$app_project" 2>/dev/null || true)
  testing_package_version=$(get_testing_package_version)
  [[ -z "$app_digest" ]] || app_digest_json=$(json_string "$app_digest")
  [[ -n "$app_digest" ]] || omissions+=('{"kind":"app-digest","reason":"The selected app project was unavailable or could not be hashed."}')
  if ! printf '%s\n' "${omissions[@]}" | grep -q '"kind":"package-digest"'; then
    omissions+=('{"kind":"package-digest","reason":"The platform host did not emit a packaged-app digest for this run."}')
  fi
  collect_artifacts
  omissions+=("${artifact_omissions[@]}")
  atomic_write "$manifest_path" <<EOF
{
  "schema": 1,
  "kind": "devflow-flow-qa",
  "generatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "repository": { "commit": $(json_string "$repository_commit") },
  "workflow": { "runId": $(json_string "$run_id"), "attempt": $(json_string "${GITHUB_RUN_ATTEMPT-}") },
  "experimental": $manifest_experimental,
  "backend": $manifest_backend,
  "officialCoverage": $manifest_official_coverage,
  "macCatalystEquivalent": $manifest_maccatalyst_equivalent,
  "testing": {
    "project": "src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/Microsoft.Maui.DevFlow.Agent.IntegrationTests.csproj",
    "packageVersion": $(json_string "$testing_package_version")
  },
  "platform": {
    "name": $(json_string "$platform"),
    "host": {
      "hostOs": $(json_string "$host_os"),
      "dotnetSdk": $(json_string "$dotnet_sdk"),
      "workloadVersion": $(json_string "${DOTNET_WORKLOAD_VERSION-unknown}"),
      "xcode": $(json_string "$xcode_version"),
      "runtime": $(json_string "${ios_runtime:-default}"),
      "deviceEvidence": {
        "kind": $(json_string "$device_kind"),
        "realDevice": $physical_device,
        "deviceIdFingerprint": $device_id_json,
        "profile": "not-observed"
      },
      "signing": $signing_refs_json
    }
  },
  "app": {
    "project": $(json_string "$(repo_relative "$app_project")"),
    "sourceDigest": $app_digest_json,
    "packageDigest": null
  },
  "flows": $(build_flow_digests_json),
  "appleQa": $apple_qa_json,
  "hostQa": {
    "runId": $(json_string "$run_id"),
    "configuration": $(json_string "$configuration"),
    "repeat": $repeat,
    "platformFilter": $(json_string "$base_filter"),
    "testFilterDigest": $(json_string "$test_filter_digest"),
    "noBuild": $no_build,
    "status": $(json_string "$status"),
    "classification": $(json_string "$classification"),
    "firstAttempt": ${attempts[0]:-null},
    "cleanAttempts": $(join_json_array "${attempts[@]}"),
    "diagnosticReruns": [],
    "resetSeed": {
      "resetFingerprint": $(json_string "$apple_reset_fingerprint"),
      "seedFingerprint": $(json_string "$apple_seed_fingerprint"),
      "backendStateFingerprint": $(json_string "$apple_backend_fingerprint")
    },
    "qualification": $qualification_json
    ,"appleSpike": $apple_spike_json
    ,"appleQa": $apple_qa_json
  },
  "artifacts": $(join_json_array "${artifact_records[@]}"),
  "omissions": $(join_json_array "${omissions[@]}"),
  "privacy": {
    "excludedByDefault": ["screenshots", "source", "raw-model-context", "environment", "signing-inputs"]
  }
}
EOF
}

finalize_android_manifest() {
  local finalizer="$repo_root/eng/devflow/Finalize-DevFlowFlowPilotManifest.ps1" raw
  [[ -f "$finalizer" ]] || return 1
  command -v pwsh >/dev/null 2>&1 || return 1
  raw="$artifact_root/.flow-qa-finalize-$$.tmp"
  temporary_paths+=("$raw")
  umask 077
  FLOW_QA_FINALIZER="$finalizer" \
  FLOW_QA_MANIFEST_PATH="$manifest_path" \
  FLOW_QA_REPOSITORY_ROOT="$repo_root" \
  FLOW_QA_ARTIFACT_ROOT="$artifact_root" \
  FLOW_QA_RESULTS_ROOT="$results_root" \
  FLOW_QA_REPOSITORY_COMMIT="$repository_commit" \
  FLOW_QA_WORKFLOW_RUN_ID="$run_id" \
  FLOW_QA_ANDROID_API="${DEVFLOW_TEST_ANDROID_API-}" \
  FLOW_QA_ANDROID_AVD="${DEVFLOW_TEST_ANDROID_AVD-}" \
  pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & $env:FLOW_QA_FINALIZER -ManifestPath $env:FLOW_QA_MANIFEST_PATH -RepositoryRoot $env:FLOW_QA_REPOSITORY_ROOT -ArtifactRoots @($env:FLOW_QA_ARTIFACT_ROOT, $env:FLOW_QA_RESULTS_ROOT) -Platform android -RepositoryCommit $env:FLOW_QA_REPOSITORY_COMMIT -WorkflowRunId $env:FLOW_QA_WORKFLOW_RUN_ID -AndroidApiLevel $env:FLOW_QA_ANDROID_API -AndroidAvdName $env:FLOW_QA_ANDROID_AVD -DeviceEvidenceKind emulator' >"$raw" 2>&1
  local code=$?
  rm -f -- "$raw"
  return "$code"
}

write_artifacts() {
  if [[ "$classification" =~ ^(flow-failure|infrastructure-failure)$ ]] &&
    ! printf '%s\n' "${omissions[@]}" | grep -q '"kind":"failure-evidence"' &&
    ! find "$artifact_root" -type f -name '*.mauitrace' -print -quit | grep -q .; then
    omissions+=('{"kind":"failure-evidence","reason":"No failure .mauitrace was available for this terminal outcome."}')
  fi
  if [[ "$platform" != android ]] &&
    ! printf '%s\n' "${omissions[@]}" | grep -q '"kind":"package-digest"'; then
    omissions+=('{"kind":"package-digest","reason":"The platform host did not emit a packaged-app digest for this run."}')
  fi
  write_host_diagnostics
  write_flow_run
  if [[ "$platform" == android ]]; then
    if ! finalize_android_manifest; then
      omissions+=('{"kind":"shared-manifest","reason":"The shared Android flow-pilot manifest could not be finalized."}')
      if [[ ! -f "$manifest_path" ]]; then
        write_generic_manifest
      fi
    fi
  else
    write_generic_manifest
  fi
}

print_status() {
  printf 'flow-qa: platform=%s status=%s classification=%s artifacts=%s\n' \
    "$platform" "$status" "$classification" "$(repo_relative "$artifact_root")" >&2
}

run_test_command() {
  local attempt=$1 raw="$artifact_root/.flow-qa-command-$attempt-$$.tmp"
  local diagnostic="$diagnostic_dir/test-output-attempt-$attempt.txt"
  local -a attempt_arguments=("${test_arguments[@]}")
  local index
  for index in "${!attempt_arguments[@]}"; do
    attempt_arguments[$index]=${attempt_arguments[$index]//\{attempt\}/$attempt}
  done
  temporary_paths+=("$raw")
  umask 077
  "${attempt_arguments[@]}" >"$raw" 2>&1
  command_exit=$?
  redact_diagnostic_file "$raw" "$diagnostic"
  command_output=$(head -c "$MAX_DIAGNOSTIC_BYTES" "$diagnostic" || true)
  command_classification=$(classify_execution "$command_exit" "$raw")
  rm -f -- "$raw"
}

run_qualification() {
  local cli_project="$repo_root/src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj"
  local output="$artifact_root/qualification.json"
  local raw="$artifact_root/.flow-qa-qualification-$$.tmp"
  local -a arguments=(run --project "$cli_project" -f net10.0 --configuration "$configuration")
  [[ "$no_build" == true ]] && arguments+=(--no-build)
  arguments+=(-- devflow flow qualify --platform "$platform" --corpus "$repo_root/tests/DevFlow/InspectorCorpus" --artifact-manifest "$manifest_path" --output "$output" --json --fail-on-non-pass)
  temporary_paths+=("$raw")
  umask 077
  dotnet "${arguments[@]}" >"$raw" 2>&1
  local code=$?
  redact_diagnostic_file "$raw" "$diagnostic_dir/qualification-output.txt"
  rm -f -- "$raw"
  if (( code == 0 )); then
    qualification_json='{"status":"qualified"}'
    return 0
  fi
  if [[ -f "$output" ]] && grep -Eq '"status"[[:space:]]*:[[:space:]]*"not-qualified"' "$output"; then
    qualification_json='{"status":"not-qualified"}'
    return "$EXIT_PENDING"
  fi
  qualification_json='{"status":"qualification-failed"}'
  return "$EXIT_PREREQUISITE"
}

apple_info_plist() {
  if [[ "$platform" == ios ]]; then
    printf '%s' "$apple_target_app/Info.plist"
  else
    printf '%s' "$apple_target_app/Contents/Info.plist"
  fi
}

select_ios_simulator() {
  [[ -n "$simulator_id" ]] && return 0
  local json="$artifact_root/.apple-simulators-$$.json"
  temporary_paths+=("$json")
  if ! xcrun simctl list devices available --json >"$json" 2>/dev/null; then
    return 1
  fi
  simulator_id=$(python3 - "$json" "${ios_runtime:-}" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    devices = json.load(source).get("devices", {})
selector = sys.argv[2].lower().replace("x", "").rstrip(".")
candidates = []
for runtime, values in devices.items():
    if "iOS" not in runtime:
        continue
    if selector and selector not in runtime.lower().replace("-", "."):
        continue
    for value in values:
        if value.get("isAvailable", True) and "iPhone" in value.get("name", ""):
            candidates.append((value.get("state") == "Booted", value.get("udid", "")))
for _, udid in sorted(candidates, reverse=True):
    if udid:
        print(udid)
        break
PY
) || return 1
  [[ -n "$simulator_id" ]]
}

collect_ios_simulator_metadata() {
  local json="$artifact_root/.apple-simulator-metadata-$$.json" metadata
  temporary_paths+=("$json")
  xcrun simctl list devices --json >"$json" 2>/dev/null || return 0
  metadata=$(python3 - "$json" "$simulator_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    devices = json.load(source).get("devices", {})
for runtime, values in devices.items():
    for value in values:
        if value.get("udid") == sys.argv[2]:
            print(runtime.replace("com.apple.CoreSimulator.SimRuntime.", "").replace("-", "."))
            print(value.get("name", "unknown"))
            raise SystemExit(0)
PY
)
  local lines=()
  while IFS= read -r line; do lines+=("$line"); done <<< "$metadata"
  [[ -n "${lines[0]-}" ]] && apple_simulator_runtime=${lines[0]}
  [[ -n "${lines[1]-}" ]] && apple_simulator_profile=${lines[1]}
}

build_apple_target() {
  local raw="$artifact_root/.apple-target-build-$$.tmp" runtime_id search_root found project_name
  if [[ -n "$apple_target_app" && -d "$apple_target_app" ]]; then
    return 0
  fi
  if [[ -n "$target_app" && ! -d "$apple_target_app" ]]; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-target-app","reason":"The supplied --target-app directory was not found."}')
    return "$EXIT_PENDING"
  fi
  temporary_paths+=("$raw")
  local -a build_arguments=(build "$app_project" -f "net10.0-$platform" -c "$configuration" --nologo -v minimal
    -p:DevFlowIntegrationTest=true -p:MauiDevFlowPort="$apple_in_app_agent_port")
  if [[ "$platform" == ios ]]; then
    if [[ "$(uname -m)" == arm64 ]]; then runtime_id=iossimulator-arm64; else runtime_id=iossimulator-x64; fi
    build_arguments+=(-p:_DeviceTarget=simulator -p:RuntimeIdentifier="$runtime_id")
  fi
  if ! dotnet "${build_arguments[@]}" >"$raw" 2>&1; then
    redact_diagnostic_file "$raw" "$diagnostic_dir/apple-target-build.txt"
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-target-build","reason":"The instrumented DevFlow.Sample Apple target could not be built on this host."}')
    return "$EXIT_PENDING"
  fi
  redact_diagnostic_file "$raw" "$diagnostic_dir/apple-target-build.txt"
  rm -f -- "$raw"
  project_name=$(basename -- "$app_project" .csproj)
  search_root="$repo_root/artifacts/bin/$project_name/$configuration/net10.0-$platform"
  if [[ ! -d "$search_root" ]]; then
    search_root="$(dirname -- "$app_project")/bin/$configuration/net10.0-$platform"
  fi
  found=$(find "$search_root" -type d -name '*.app' -print -quit 2>/dev/null || true)
  if [[ -z "$found" ]]; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-target-build","reason":"The Apple build completed without a discoverable .app bundle."}')
    return "$EXIT_PENDING"
  fi
  apple_target_app=$found
  return 0
}

verify_apple_target_identity() {
  local plist observed
  plist=$(apple_info_plist)
  [[ -f "$plist" ]] || return 1
  observed=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$plist" 2>/dev/null || true)
  [[ -n "$observed" && "$observed" == "$target_bundle_id" ]]
}

prepare_apple_target() {
  if ! command -v xcodebuild >/dev/null 2>&1 || ! command -v xcrun >/dev/null 2>&1 ||
    ! command -v python3 >/dev/null 2>&1; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-xctest-toolchain","reason":"xcodebuild, xcrun, and python3 are required for Apple flow QA; the script does not install them."}')
    return "$EXIT_PENDING"
  fi
  build_apple_target
  local build_exit=$?
  if (( build_exit != 0 )); then
    return "$build_exit"
  fi
  if ! verify_apple_target_identity; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-target-identity","reason":"The instrumented target bundle identifier did not match --target-bundle-id."}')
    return "$EXIT_PENDING"
  fi
  if [[ "$platform" == ios ]] && ! select_ios_simulator; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-simulator","reason":"No available iPhone simulator could be selected."}')
    return "$EXIT_PENDING"
  fi
  if [[ "$platform" == ios ]]; then
    xcrun simctl boot "$simulator_id" >/dev/null 2>&1 || true
    if ! xcrun simctl bootstatus "$simulator_id" -b >/dev/null 2>&1; then
      status=pending-spike
      classification=capability-missing
      omissions+=('{"kind":"apple-simulator","reason":"The selected iOS Simulator did not become booted."}')
      return "$EXIT_PENDING"
    fi
    collect_ios_simulator_metadata
  fi
  return 0
}

reset_apple_target_for_flow() {
  local raw="$artifact_root/.apple-reset-$$.tmp"
  temporary_paths+=("$raw")
  if [[ "$platform" == ios ]]; then
    xcrun simctl terminate "$simulator_id" "$target_bundle_id" >"$raw" 2>&1 || true
    if ! xcrun simctl uninstall "$simulator_id" "$target_bundle_id" >>"$raw" 2>&1; then
      if ! grep -Eqi 'not installed|not found' "$raw"; then
        redact_diagnostic_file "$raw" "$diagnostic_dir/apple-reset.txt"
        return 1
      fi
    fi
    if ! xcrun simctl install "$simulator_id" "$apple_target_app" >>"$raw" 2>&1; then
      redact_diagnostic_file "$raw" "$diagnostic_dir/apple-reset.txt"
      return 1
    fi
  else
    # This is a process-only safe reset. The target is compiled with the explicit
    # DEVFLOW_INTEGRATION_TEST seed hook; no user container, Keychain, or AppKit lane is erased.
    osascript -e "tell application id \"$target_bundle_id\" to quit" >"$raw" 2>&1 || true
    if ! open -g -n "$apple_target_app" >>"$raw" 2>&1; then
      redact_diagnostic_file "$raw" "$diagnostic_dir/apple-reset.txt"
      return 1
    fi
  fi
  redact_diagnostic_file "$raw" "$diagnostic_dir/apple-reset.txt"
  rm -f -- "$raw"
  return 0
}

run_apple_flow_attempt() {
  local flow_path=$1 repetition=$2 flow_name session_root host_ready host_raw xcode_raw
  local host_pid= host_exit=1 xcode_exit=1 endpoint= session_secret= target_digest=
  flow_name=$(basename -- "$flow_path" .md)
  session_root="$artifact_root/apple-flow-runs/$flow_name/attempt-$repetition"
  host_ready="$session_root/apple-xctest-host-ready.json"
  host_raw="$session_root/.apple-host-$$.tmp"
  xcode_raw="$session_root/.apple-xcode-$$.tmp"
  mkdir -p -- "$session_root"

  if ! reset_apple_target_for_flow; then
    apple_flow_attempts+=("{\"flow\":$(json_string "$flow_name"),\"repetition\":$repetition,\"status\":\"infrastructure-error\",\"reason\":\"reset\"}")
    return "$EXIT_PREREQUISITE"
  fi
  session_secret=$(openssl rand -hex 32) || return "$EXIT_PREREQUISITE"
  target_digest=$(sha256_file "$(apple_info_plist)" 2>/dev/null || true)
  export DEVFLOW_APPLE_AGENT_SESSION_SECRET="$session_secret"
  export DEVFLOW_APPLE_AGENT_SESSION_ID="apple-$run_id-$flow_name-$repetition"
  export DEVFLOW_TARGET_BUNDLE_ID="$target_bundle_id"
  export DEVFLOW_APPLE_AGENT_PLATFORM="$platform"
  export DEVFLOW_APPLE_AGENT_TIMEOUT_SECONDS="$apple_spike_timeout"
  export DEVFLOW_APPLE_IN_APP_AGENT_PORT="$apple_in_app_agent_port"
  export DEVFLOW_APPLE_TEST_SEED=devflow-sample-v1
  [[ -z "$target_digest" ]] || export DEVFLOW_TARGET_APP_DIGEST="$target_digest"

  local -a host_arguments=(run --project "$apple_agent_host_project" --configuration "$configuration")
  [[ "$no_build" == true ]] && host_arguments+=(--no-build)
  host_arguments+=(-- --mode flow --session-id "$DEVFLOW_APPLE_AGENT_SESSION_ID" --platform "$platform"
    --target-bundle-id "$target_bundle_id" --artifact-root "$session_root" --ready-file "$host_ready"
    --safe-action-id "$safe_action_id" --flow-file "$flow_path" --run-id "$flow_name-attempt-$repetition"
    --timeout-seconds "$apple_spike_timeout")
  [[ -z "$target_digest" ]] || host_arguments+=(--target-app-digest "$target_digest")
  local -a xcode_arguments=(test -project "$apple_agent_native_project" -scheme DevFlowAppleTestAgent)
  if [[ "$platform" == ios ]]; then
    xcode_arguments+=(-destination "platform=iOS Simulator,id=$simulator_id")
  else
    xcode_arguments+=(-destination 'platform=macOS')
  fi

  temporary_paths+=("$host_raw" "$xcode_raw")
  umask 077
  dotnet "${host_arguments[@]}" >"$host_raw" 2>&1 &
  host_pid=$!
  local ready_attempt
  for (( ready_attempt = 0; ready_attempt < 30; ready_attempt++ )); do
    [[ -f "$host_ready" ]] && break
    kill -0 "$host_pid" 2>/dev/null || break
    sleep 1
  done
  if [[ ! -f "$host_ready" ]]; then
    wait "$host_pid"; host_exit=$?
    redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-flow-host-$flow_name-$repetition.txt"
    apple_flow_attempts+=("{\"flow\":$(json_string "$flow_name"),\"repetition\":$repetition,\"status\":\"infrastructure-error\",\"reason\":\"host-ready\"}")
    unset DEVFLOW_APPLE_AGENT_SESSION_SECRET
    return "$EXIT_PREREQUISITE"
  fi
  endpoint=$(sed -n 's/.*"endpoint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$host_ready" | head -n 1)
  if [[ -z "$endpoint" ]]; then
    kill -TERM "$host_pid" 2>/dev/null || true
    wait "$host_pid" || true
    redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-flow-host-$flow_name-$repetition.txt"
    apple_flow_attempts+=("{\"flow\":$(json_string "$flow_name"),\"repetition\":$repetition,\"status\":\"infrastructure-error\",\"reason\":\"endpoint\"}")
    unset DEVFLOW_APPLE_AGENT_SESSION_SECRET
    return "$EXIT_PREREQUISITE"
  fi
  export DEVFLOW_APPLE_AGENT_ENDPOINT="$endpoint"
  "${xcode_arguments[@]}" >"$xcode_raw" 2>&1
  xcode_exit=$?
  wait "$host_pid"; host_exit=$?
  redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-flow-host-$flow_name-$repetition.txt"
  redact_diagnostic_file "$xcode_raw" "$diagnostic_dir/apple-flow-xcode-$flow_name-$repetition.txt"
  rm -f -- "$host_raw" "$xcode_raw"
  unset DEVFLOW_APPLE_AGENT_SESSION_SECRET DEVFLOW_APPLE_AGENT_ENDPOINT DEVFLOW_TARGET_APP_DIGEST

  local run_report="$session_root/apple-test-agent-run.json" flow_report="$session_root/flow-runs/$flow_name-attempt-$repetition/flow-run.json"
  local outcome=failed report_digest=
  if [[ -f "$run_report" ]]; then
    outcome=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$run_report" | head -n 1)
  fi
  if [[ -f "$flow_report" ]]; then
    report_digest=$(sed -n 's/.*"reportDigest"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$flow_report" | head -n 1)
  fi
  if [[ "$host_exit" == 0 && "$xcode_exit" == 0 && "$outcome" == passed ]]; then
    apple_flow_attempts+=("{\"flow\":$(json_string "$flow_name"),\"repetition\":$repetition,\"status\":\"passed\",\"report\":$(json_string "$(repo_relative "$flow_report")"),\"reportDigest\":$(json_string "$report_digest")}")
    return "$EXIT_SUCCESS"
  fi
  apple_flow_attempts+=("{\"flow\":$(json_string "$flow_name"),\"repetition\":$repetition,\"status\":$(json_string "${outcome:-failed}"),\"report\":$(json_string "$(repo_relative "$flow_report")")}")
  return "$EXIT_FLOW_FAILURE"
}

write_apple_qa_manifest() {
  local output="$artifact_root/apple-flow-qa.json" flow_path flow_name first=1 flow_entries=()
  local spike_path="$artifact_root/apple-xctest-spike.json" spike_status=not-proved foreground=false authenticated=false receipt=false cancellation=false parity=false
  local testing_version=unknown simulator_fingerprint=null checkpoint_report= webview_outcome=unsupported-by-xcuitest-agent
  [[ -f "$spike_path" ]] && spike_status=$(sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$spike_path" | head -n 1)
  [[ -f "$spike_path" ]] && grep -Eq '"asserted"[[:space:]]*:[[:space:]]*true' "$spike_path" && foreground=true
  [[ -f "$spike_path" ]] && grep -Eq '"authenticated"[[:space:]]*:[[:space:]]*true' "$spike_path" && authenticated=true
  [[ -f "$spike_path" ]] && grep -Eq '"commandReceipt"[[:space:]]*:[[:space:]]*\{' "$spike_path" && receipt=true
  [[ -f "$spike_path" ]] && grep -Eq '"code"[[:space:]]*:[[:space:]]*"apple-agent-cancelled"' "$spike_path" && cancellation=true
  [[ -f "$spike_path" ]] && grep -Eq '"passed"[[:space:]]*:[[:space:]]*true' "$spike_path" && parity=true
  [[ -f "$spike_path" ]] && testing_version=$(sed -n 's/.*"assemblyVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$spike_path" | head -n 1)
  [[ -n "$testing_version" ]] || testing_version=unknown
  [[ "$platform" != macos ]] || webview_outcome=capability-gated-by-appkit-fixture
  if [[ "$platform" == ios && -n "$simulator_id" ]]; then
    simulator_fingerprint=$(json_string "$(sha256_string "$simulator_id")")
  fi
  checkpoint_report=$(find "$artifact_root/apple-flow-runs" -type f -name flow-run.json -print -quit 2>/dev/null || true)
  if [[ -n "$checkpoint_report" ]]; then
    apple_reset_fingerprint=$(sed -n 's/.*"resetIdentity"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$checkpoint_report" | head -n 1)
    apple_seed_fingerprint=$(sed -n 's/.*"seedFingerprint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$checkpoint_report" | head -n 1)
    apple_backend_fingerprint=$(sed -n 's/.*"backendStateFingerprint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$checkpoint_report" | head -n 1)
    apple_reset_fingerprint=${apple_reset_fingerprint:-not-observed}
    apple_seed_fingerprint=${apple_seed_fingerprint:-not-observed}
    apple_backend_fingerprint=${apple_backend_fingerprint:-not-observed}
  fi
  shopt -s nullglob
  for flow_path in "$flow_directory"/*.md; do
    [[ "$(basename -- "$flow_path")" == README.md ]] && continue
    flow_name=$(basename -- "$flow_path" .md)
    local attempts=() record
    for record in "${apple_flow_attempts[@]}"; do
      [[ "$record" == *"\"flow\":\"$flow_name\""* ]] && attempts+=("$record")
    done
    local first_attempt=null
    (( ${#attempts[@]} > 0 )) && first_attempt=${attempts[0]}
    flow_entries+=("{\"name\":$(json_string "$flow_name"),\"firstAttempt\":$first_attempt,\"cleanAttempts\":$(join_json_array "${attempts[@]}")}")
  done
  shopt -u nullglob
  local signing_json='{"physical":false,"identityRef":null,"provisioningProfileRef":null,"keychainRef":null}'
  if [[ "$physical_device" == true ]]; then
    signing_json="{\"physical\":true,\"identityRef\":$([[ -n "$signing_identity" ]] && json_string "$(sha256_string "$signing_identity")" || printf null),\"provisioningProfileRef\":$([[ -n "$provisioning_profile" ]] && json_string "$(sha256_string "$provisioning_profile")" || printf null),\"keychainRef\":$([[ -n "$keychain" ]] && json_string "$(sha256_string "$keychain")" || printf null)}"
  fi
  atomic_write "$output" <<EOF
{
  "schema": 1,
  "kind": "devflow-apple-flow-qa",
  "generatedAt": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "platform": $(json_string "$platform"),
  "experimental": $manifest_experimental,
  "backend": $manifest_backend,
  "officialCoverage": $manifest_official_coverage,
  "macCatalystEquivalent": $manifest_maccatalyst_equivalent,
  "spike": {
    "status": $(json_string "$spike_status"),
    "foregroundProof": $foreground,
    "authenticatedTransport": $authenticated,
    "receipt": $receipt,
    "cancellation": $cancellation,
    "parity": $parity
  },
  "checkpoint": {
    "resetFingerprint": $(json_string "$apple_reset_fingerprint"),
    "seedFingerprint": $(json_string "$apple_seed_fingerprint"),
    "backendStateFingerprint": $(json_string "$apple_backend_fingerprint")
  },
  "apple": {
    "xcodeVersion": $(json_string "$xcode_version"),
    "simulatorRuntime": $(json_string "$apple_simulator_runtime"),
    "simulatorDeviceFingerprint": $simulator_fingerprint,
    "simulatorDeviceProfile": $(json_string "$apple_simulator_profile"),
    "signing": $signing_json,
    "testAgent": { "hostProject": "Microsoft.Maui.DevFlow.TestAgent.Host", "nativeVersion": "1.0.0-experimental" },
    "testingPackageVersion": $(json_string "$testing_version"),
    "artifactTrust": "sha256-bound-redacted-manifest"
  },
  "flows": $(join_json_array "${flow_entries[@]}"),
  "contractOutcomes": {
    "agentAuthoredFlow": "covered-by-reviewed-flow",
    "selectorIdentity": "covered-by-runtime-duplicate-rejection",
    "shellModalRoute": "covered-by-modal-roundtrip",
    "webViewContext": $(json_string "$webview_outcome"),
    "repairAbstention": "covered-by-static-contract",
    "sourceProposal": "covered-by-static-contract",
    "securityPrivacy": "covered-by-static-contract",
    "reportParity": "covered-by-normalized-runner-report"
  }
}
EOF
  apple_qa_json=$(cat "$output")
}

run_apple_flow_qa() {
  local flow_path repetition code=0 any_failure=false
  apple_flow_attempts=()
  for flow_path in "$flow_directory"/*.md; do
    [[ "$(basename -- "$flow_path")" == README.md ]] && continue
    for (( repetition = 1; repetition <= repeat; repetition++ )); do
      if ! run_apple_flow_attempt "$flow_path" "$repetition"; then
        any_failure=true
      fi
    done
  done
  write_apple_qa_manifest
  [[ "$any_failure" == false ]]
}

run_apple_spike() {
  local host_ready="$artifact_root/apple-xctest-host-ready.json"
  local host_raw="$artifact_root/.apple-xctest-host-$$.tmp"
  local xcode_raw="$artifact_root/.apple-xctest-xcode-$$.tmp"
  local host_pid= host_exit=1 xcode_exit=1 endpoint= session_secret= target_digest=
  local -a host_arguments=(run --project "$apple_agent_host_project" --configuration "$configuration")
  local -a xcode_arguments=(test -project "$apple_agent_native_project" -scheme DevFlowAppleTestAgent)

  if [[ ! -f "$apple_agent_host_project" || ! -f "$apple_agent_native_project/project.pbxproj" || ! -f "$apple_agent_swift_source" ]]; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-xctest-agent","reason":"The checked-in Apple XCTest host or native source is unavailable."}')
    return "$EXIT_PENDING"
  fi
  prepare_apple_target || return "$?"
  if ! command -v openssl >/dev/null 2>&1; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-transport-auth","reason":"openssl is required to generate the ephemeral HMAC session secret; no secret fallback is used."}')
    return "$EXIT_PENDING"
  fi

  session_secret=$(openssl rand -hex 32) || {
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-transport-auth","reason":"The ephemeral HMAC session secret could not be generated."}')
    return "$EXIT_PENDING"
  }
  export DEVFLOW_APPLE_AGENT_SESSION_SECRET="$session_secret"
  export DEVFLOW_APPLE_AGENT_SESSION_ID="apple-$run_id"
  export DEVFLOW_TARGET_BUNDLE_ID="$target_bundle_id"
  export DEVFLOW_APPLE_AGENT_PLATFORM="$platform"
  export DEVFLOW_APPLE_AGENT_TIMEOUT_SECONDS="$apple_spike_timeout"
  export DEVFLOW_APPLE_IN_APP_AGENT_PORT="$apple_in_app_agent_port"
  export DEVFLOW_APPLE_TEST_SEED=devflow-sample-v1

  if [[ "$platform" == ios ]]; then
    if ! xcrun simctl bootstatus "$simulator_id" -b >"$xcode_raw" 2>&1; then
      redact_diagnostic_file "$xcode_raw" "$diagnostic_dir/apple-xctest-simulator.txt"
      rm -f -- "$xcode_raw"
      status=pending-spike
      classification=capability-missing
      omissions+=('{"kind":"apple-simulator","reason":"The requested iOS Simulator was not ready."}')
      return "$EXIT_PENDING"
    fi
    if ! xcrun simctl install "$simulator_id" "$apple_target_app" >"$xcode_raw" 2>&1; then
      redact_diagnostic_file "$xcode_raw" "$diagnostic_dir/apple-xctest-install.txt"
      rm -f -- "$xcode_raw"
      status=pending-spike
      classification=capability-missing
      omissions+=('{"kind":"apple-target-install","reason":"The built target app could not be installed on the selected simulator."}')
      return "$EXIT_PENDING"
    fi
    xcode_arguments+=(-destination "platform=iOS Simulator,id=$simulator_id")
  else
    # XCUITest activates the exact bundle ID. Opening first only makes the locally built
    # Mac Catalyst bundle discoverable; it does not claim foreground ownership.
    if ! open -g "$apple_target_app" >"$xcode_raw" 2>&1; then
      redact_diagnostic_file "$xcode_raw" "$diagnostic_dir/apple-xctest-open.txt"
      rm -f -- "$xcode_raw"
      status=pending-spike
      classification=capability-missing
      omissions+=('{"kind":"apple-target-launch","reason":"The built Mac Catalyst target app could not be opened."}')
      return "$EXIT_PENDING"
    fi
    xcode_arguments+=(-destination 'platform=macOS')
  fi

  [[ "$no_build" == true ]] && host_arguments+=(--no-build)
  target_digest=$(sha256_file "$(apple_info_plist)" 2>/dev/null || true)
  [[ -z "$target_digest" ]] || export DEVFLOW_TARGET_APP_DIGEST="$target_digest"
  host_arguments+=(-- --session-id "$DEVFLOW_APPLE_AGENT_SESSION_ID" --platform "$platform" \
    --target-bundle-id "$target_bundle_id" --artifact-root "$artifact_root" --ready-file "$host_ready" \
    --safe-action-id "$safe_action_id" --timeout-seconds "$apple_spike_timeout")
  [[ -z "$target_digest" ]] || host_arguments+=(--target-app-digest "$target_digest")

  temporary_paths+=("$host_raw" "$xcode_raw")
  umask 077
  dotnet "${host_arguments[@]}" >"$host_raw" 2>&1 &
  host_pid=$!

  local ready_attempt
  for (( ready_attempt = 0; ready_attempt < 30; ready_attempt++ )); do
    [[ -f "$host_ready" ]] && break
    if ! kill -0 "$host_pid" 2>/dev/null; then
      break
    fi
    sleep 1
  done
  if [[ ! -f "$host_ready" ]]; then
    wait "$host_pid"; host_exit=$?
    redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-xctest-host.txt"
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-xctest-host","reason":"The macOS host transport did not become ready without exposing its session secret."}')
    return "$EXIT_PENDING"
  fi

  endpoint=$(sed -n 's/.*"endpoint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$host_ready" | head -n 1)
  if [[ -z "$endpoint" ]]; then
    kill -TERM "$host_pid" 2>/dev/null || true
    wait "$host_pid" || true
    redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-xctest-host.txt"
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"apple-xctest-host","reason":"The macOS host readiness report did not contain a loopback endpoint."}')
    return "$EXIT_PENDING"
  fi
  export DEVFLOW_APPLE_AGENT_ENDPOINT="$endpoint"

  "${xcode_arguments[@]}" >"$xcode_raw" 2>&1
  xcode_exit=$?
  wait "$host_pid"; host_exit=$?
  redact_diagnostic_file "$host_raw" "$diagnostic_dir/apple-xctest-host.txt"
  redact_diagnostic_file "$xcode_raw" "$diagnostic_dir/apple-xctest-xcode.txt"
  rm -f -- "$host_raw" "$xcode_raw"

  local spike_path="$artifact_root/apple-xctest-spike.json"
  if [[ "$host_exit" == 0 && "$xcode_exit" == 0 && -f "$spike_path" ]] &&
    grep -Eq '"status"[[:space:]]*:[[:space:]]*"proved"' "$spike_path"; then
    apple_spike_json=$(cat "$spike_path")
    status=passed
    classification=passed
    unset DEVFLOW_APPLE_AGENT_SESSION_SECRET DEVFLOW_APPLE_AGENT_ENDPOINT DEVFLOW_TARGET_APP_DIGEST
    return "$EXIT_SUCCESS"
  fi

  [[ -f "$spike_path" ]] && apple_spike_json=$(cat "$spike_path")
  status=pending-spike
  classification=proof-incomplete
  omissions+=('{"kind":"apple-xctest-proof","reason":"XCTest runtime proof was incomplete; inspect the bounded spike report and redacted host/Xcode diagnostics."}')
  unset DEVFLOW_APPLE_AGENT_SESSION_SECRET DEVFLOW_APPLE_AGENT_ENDPOINT DEVFLOW_TARGET_APP_DIGEST
  return "$EXIT_PENDING"
}

platform=
repeat=3
results_root_input=
configuration=Debug
flow_filter=
no_build=false
qualification=false
experimental=false
physical_device=false
device_id=
ios_runtime=
signing_identity=
provisioning_profile=
keychain=
verbosity=normal
dry_run=false
apple_spike=false
target_app=
target_bundle_id=
simulator_id=
safe_action_id=
apple_spike_timeout=180
apple_in_app_agent_port=9223

while (( $# > 0 )); do
  case "$1" in
    --help|-help)
      usage
      exit "$EXIT_SUCCESS"
      ;;
    --platform|-platform)
      require_value "$1" "${2-}"; platform=$2; shift 2
      ;;
    --repeat|-repeat)
      require_value "$1" "${2-}"; repeat=$2; shift 2
      ;;
    --results-root|-results-root)
      require_value "$1" "${2-}"; results_root_input=$2; shift 2
      ;;
    --configuration|-configuration)
      require_value "$1" "${2-}"; configuration=$2; shift 2
      ;;
    --flow-filter|-flow-filter)
      require_value "$1" "${2-}"; flow_filter=$2; shift 2
      ;;
    --no-build|-no-build)
      no_build=true; shift
      ;;
    --qualification|-qualification)
      qualification=true; shift
      ;;
    --experimental|-experimental)
      experimental=true; shift
      ;;
    --physical-device|-physical-device)
      physical_device=true; shift
      ;;
    --device-id|-device-id)
      require_value "$1" "${2-}"; device_id=$2; shift 2
      ;;
    --ios-runtime|-ios-runtime)
      require_value "$1" "${2-}"; ios_runtime=$2; shift 2
      ;;
    --signing-identity|--ios-signing-identity)
      require_value "$1" "${2-}"; signing_identity=$2; shift 2
      ;;
    --provisioning-profile|--ios-provisioning-profile)
      require_value "$1" "${2-}"; provisioning_profile=$2; shift 2
      ;;
    --keychain|--ios-keychain)
      require_value "$1" "${2-}"; keychain=$2; shift 2
      ;;
    --apple-spike)
      apple_spike=true; shift
      ;;
    --target-app)
      require_value "$1" "${2-}"; target_app=$2; shift 2
      ;;
    --target-bundle-id)
      require_value "$1" "${2-}"; target_bundle_id=$2; shift 2
      ;;
    --simulator-id)
      require_value "$1" "${2-}"; simulator_id=$2; shift 2
      ;;
    --safe-action-id)
      require_value "$1" "${2-}"; safe_action_id=$2; shift 2
      ;;
    --apple-spike-timeout)
      require_value "$1" "${2-}"; apple_spike_timeout=$2; shift 2
      ;;
    --apple-in-app-agent-port)
      require_value "$1" "${2-}"; apple_in_app_agent_port=$2; shift 2
      ;;
    --verbosity|-verbosity)
      require_value "$1" "${2-}"; verbosity=$2; shift 2
      ;;
    --verbose)
      verbosity=detailed; shift
      ;;
    --dry-run|-dry-run)
      dry_run=true; shift
      ;;
    *)
      die_usage "Unknown option '$1'."
      ;;
  esac
done

platform=${platform,,}
[[ "$platform" =~ ^(android|windows|ios|maccatalyst|macos)$ ]] ||
  die_usage '--platform is required and must be android, windows, ios, maccatalyst, or macos.'
[[ -n "$results_root_input" ]] || die_usage '--results-root is required.'
[[ "$repeat" =~ ^[0-9]+$ ]] && (( 10#$repeat >= 1 && 10#$repeat <= 20 )) ||
  die_usage '--repeat must be an integer from 1 through 20.'
repeat=$((10#$repeat))
validate_single_line '--configuration' "$configuration"
[[ "$configuration" =~ ^[A-Za-z0-9._-]+$ ]] ||
  die_usage '--configuration may contain only letters, digits, dot, underscore, and hyphen.'
[[ "$verbosity" =~ ^(quiet|minimal|normal|detailed|diagnostic)$ ]] ||
  die_usage '--verbosity must be quiet, minimal, normal, detailed, or diagnostic.'
[[ -z "$flow_filter" ]] || validate_single_line '--flow-filter' "$flow_filter"
[[ -z "$device_id" ]] || validate_single_line '--device-id' "$device_id"
[[ -z "$ios_runtime" ]] || validate_single_line '--ios-runtime' "$ios_runtime"
[[ -z "$signing_identity" ]] || validate_single_line '--signing-identity' "$signing_identity"
[[ -z "$provisioning_profile" ]] || validate_single_line '--provisioning-profile' "$provisioning_profile"
[[ -z "$keychain" ]] || validate_single_line '--keychain' "$keychain"
[[ -z "$target_app" ]] || validate_single_line '--target-app' "$target_app"
[[ -z "$target_bundle_id" ]] || validate_single_line '--target-bundle-id' "$target_bundle_id"
[[ -z "$simulator_id" ]] || validate_single_line '--simulator-id' "$simulator_id"
[[ -z "$safe_action_id" ]] || validate_single_line '--safe-action-id' "$safe_action_id"
[[ "$apple_spike_timeout" =~ ^[0-9]+$ ]] && (( 10#$apple_spike_timeout >= 15 && 10#$apple_spike_timeout <= 600 )) ||
  die_usage '--apple-spike-timeout must be an integer from 15 through 600.'
apple_spike_timeout=$((10#$apple_spike_timeout))
[[ "$apple_in_app_agent_port" =~ ^[0-9]+$ ]] && (( 10#$apple_in_app_agent_port >= 1024 && 10#$apple_in_app_agent_port <= 65535 )) ||
  die_usage '--apple-in-app-agent-port must be an integer from 1024 through 65535.'
apple_in_app_agent_port=$((10#$apple_in_app_agent_port))

[[ "$platform" != macos || "$experimental" == true ]] ||
  die_usage '--platform macos is experimental and requires --experimental.'
[[ "$experimental" != true || "$platform" == macos ]] ||
  die_usage '--experimental applies only to the separately labeled macos/AppKit lane.'
[[ "$platform" != macos || "$qualification" != true ]] ||
  die_usage '--qualification cannot be used for experimental AppKit; it never qualifies an official MAUI or Mac Catalyst gate.'
[[ "$physical_device" != true || "$platform" == ios ]] ||
  die_usage '--physical-device applies only to --platform ios.'
[[ "$physical_device" != true || -z "$ios_runtime" ]] ||
  die_usage '--ios-runtime is a simulator selector and cannot be combined with --physical-device.'
[[ -z "$ios_runtime" || "$platform" == ios ]] ||
  die_usage '--ios-runtime applies only to the iOS Simulator lane.'
[[ -z "$device_id" || "$platform" == android || ("$platform" == ios && "$physical_device" == true) ]] ||
  die_usage '--device-id applies to Android or to the physical-iOS lane.'
if [[ "$platform" == ios && "$physical_device" == true ]]; then
  [[ -n "$device_id" ]] || die_usage 'Physical iOS requires --device-id.'
  [[ -n "$signing_identity" ]] || die_usage 'Physical iOS requires --signing-identity.'
  [[ -n "$provisioning_profile" ]] || die_usage 'Physical iOS requires --provisioning-profile.'
  [[ -n "$keychain" ]] || die_usage 'Physical iOS requires --keychain.'
elif [[ -n "$signing_identity$provisioning_profile$keychain" ]]; then
  die_usage 'Signing, provisioning, and keychain options apply only to --platform ios --physical-device.'
fi
if [[ "$apple_spike" == true ]]; then
  [[ "$platform" == ios || "$platform" == maccatalyst || "$platform" == macos ]] ||
    die_usage '--apple-spike supports only the iOS Simulator, Mac Catalyst, and experimental AppKit proof lanes.'
  [[ "$physical_device" != true ]] ||
    die_usage '--apple-spike does not claim physical-iOS proof; use the separately provisioned physical lane after this spike.'
fi
[[ -z "$simulator_id" || "$platform" == ios ]] ||
  die_usage '--simulator-id applies only to the iOS Simulator lane.'

if [[ "$platform" =~ ^(ios|maccatalyst|macos)$ && "$physical_device" != true ]]; then
  if [[ "$platform" == macos ]]; then
    target_bundle_id=${target_bundle_id:-com.companyname.mauitodo.appkit}
  else
    target_bundle_id=${target_bundle_id:-com.companyname.mauitodo}
  fi
  safe_action_id=${safe_action_id:-AddButton}
  [[ "$target_bundle_id" =~ ^[A-Za-z0-9.-]+$ ]] ||
    die_usage '--target-bundle-id may contain only letters, digits, dot, and hyphen.'
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repo_root=$(cd -- "$script_dir/../.." && pwd -P)
[[ -f "$repo_root/global.json" && -f "$repo_root/MauiLabs.slnx" ]] || {
  printf 'flow-qa: The script must be run from a maui-labs checkout with global.json.\n' >&2
  exit "$EXIT_PREREQUISITE"
}

if [[ -n "${DEVFLOW_FLOW_QA_RUN_ID-}" ]]; then
  [[ "$DEVFLOW_FLOW_QA_RUN_ID" =~ ^[A-Za-z0-9._-]+$ ]] ||
    die_usage 'DEVFLOW_FLOW_QA_RUN_ID may contain only letters, digits, dot, underscore, and hyphen.'
  run_id=$DEVFLOW_FLOW_QA_RUN_ID
elif [[ -n "${GITHUB_RUN_ID-}" ]]; then
  run_id="$GITHUB_RUN_ID${GITHUB_RUN_ATTEMPT:+-$GITHUB_RUN_ATTEMPT}"
else
  run_id="local-$(date -u '+%Y%m%d%H%M%S')-$$"
fi

resolve_results_root "$results_root_input"
results_root=$resolved_results_root
resolve_artifact_root
artifact_root=$resolved_artifact_root
diagnostic_dir="$artifact_root/host-diagnostics"
manifest_path="$artifact_root/manifest.json"
flow_run_path="$artifact_root/flow-run.json"
test_project="$repo_root/src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/Microsoft.Maui.DevFlow.Agent.IntegrationTests.csproj"
if [[ "$platform" == macos ]]; then
  app_project="$repo_root/samples/DevFlow.Sample.MacOS/DevFlow.Sample.MacOS.csproj"
  flow_directory="$repo_root/samples/DevFlow.Sample.MacOS/maui-tests"
else
  app_project="$repo_root/samples/DevFlow.Sample/DevFlow.Sample.csproj"
  flow_directory="$repo_root/samples/DevFlow.Sample/maui-tests"
fi
apple_agent_root="$repo_root/src/DevFlow/Microsoft.Maui.DevFlow.TestAgent"
apple_agent_host_project="$repo_root/src/DevFlow/Microsoft.Maui.DevFlow.TestAgent.Host/Microsoft.Maui.DevFlow.TestAgent.Host.csproj"
apple_agent_native_project="$apple_agent_root/AppleXCTestAgent/DevFlowAppleTestAgent.xcodeproj"
apple_agent_swift_source="$apple_agent_root/AppleXCTestAgent/DevFlowAppleTestAgentUITests/DevFlowAppleTestAgentUITests.swift"
apple_agent_source_available=false
[[ -f "$apple_agent_host_project" && -f "$apple_agent_native_project/project.pbxproj" && -f "$apple_agent_swift_source" ]] &&
  apple_agent_source_available=true
apple_target_app=
if [[ -n "$target_app" ]]; then
  if [[ "$target_app" = /* ]]; then
    apple_target_app=${target_app%/}
  else
    apple_target_app="$repo_root/${target_app%/}"
  fi
fi
apple_qa_manifest_path="$artifact_root/apple-flow-qa.json"
apple_simulator_runtime=${ios_runtime:-default}
apple_simulator_profile=not-observed
apple_reset_fingerprint=not-observed
apple_seed_fingerprint=not-observed
apple_backend_fingerprint=not-observed

case "$platform" in
  android) base_filter='Category=FlowPilot' ;;
  windows) base_filter='Category=WindowsFlowQa' ;;
  macos) base_filter='Category=AppKitFlowQa' ;;
  *) base_filter='Category=AppleTestAgent' ;;
esac
if [[ -n "$flow_filter" ]]; then
  test_filter="$base_filter&($flow_filter)"
else
  test_filter=$base_filter
fi
manifest_experimental=false
manifest_backend=null
manifest_official_coverage=true
manifest_maccatalyst_equivalent=null
if [[ "$platform" == macos ]]; then
  manifest_experimental=true
  manifest_backend=$(json_string appkit)
  manifest_official_coverage=false
  manifest_maccatalyst_equivalent=false
fi
test_filter_digest=$(sha256_string "$test_filter" 2>/dev/null || printf unknown)
if [[ "$platform" == android || "$platform" == windows ]]; then
  trx_file_name="devflow-flow-$platform-$run_id.trx"
else
  trx_file_name="devflow-flow-$platform-$run_id-attempt-{attempt}.trx"
fi
test_arguments=(test "$test_project" --configuration "$configuration" --filter "$test_filter"
  --logger "trx;LogFileName=$trx_file_name"
  --logger "console;verbosity=$verbosity"
  --results-directory "$results_root")
[[ "$no_build" == true ]] && test_arguments+=(--no-build)

temporary_paths=()
trap cleanup_temporary_paths EXIT

if [[ "$dry_run" == true ]]; then
  printf '{"schema":1,"kind":"devflow-flow-qa-dry-run","status":"dry-run","platform":'
  json_string "$platform"
  printf ',"repeat":%s,"configuration":' "$repeat"
  json_string "$configuration"
  printf ',"testFilter":'
  json_string "$test_filter"
  printf ',"noBuild":%s,"qualificationRequested":%s,"experimental":%s,"physicalDevice":%s,"signingInputsConfigured":%s' \
    "$no_build" "$qualification" "$experimental" "$physical_device" \
    "$([[ -n "$signing_identity" && -n "$provisioning_profile" && -n "$keychain" ]] && printf true || printf false)"
  printf ',"backend":%s,"officialCoverage":%s,"macCatalystEquivalent":%s' \
    "$manifest_backend" "$manifest_official_coverage" "$manifest_maccatalyst_equivalent"
  printf ',"appProject":'
  json_string "$(repo_relative "$app_project")"
  local_apple_runtime=false
  [[ "$platform" == ios || "$platform" == maccatalyst || "$platform" == macos ]] && local_apple_runtime=true
  printf ',"appleSpike":%s' "$local_apple_runtime"
  if [[ "$local_apple_runtime" == true ]]; then
    printf ',"appleTarget":{"app":'
    if [[ -n "$apple_target_app" ]]; then json_string "$apple_target_app"; else printf 'null'; fi
    printf ',"bundleId":'
    if [[ -n "$target_bundle_id" ]]; then json_string "$target_bundle_id"; else printf 'null'; fi
    printf ',"simulatorIdConfigured":%s,"safeActionId":' "$([[ -n "$simulator_id" ]] && printf true || printf false)"
    if [[ -n "$safe_action_id" ]]; then json_string "$safe_action_id"; else printf 'null'; fi
    printf '}'
  fi
  printf ',"command":{"tool":"dotnet","arguments":'
  if [[ "$local_apple_runtime" == true ]]; then
    json_array run --project "$apple_agent_host_project" --configuration "$configuration" -- --session-id "apple-$run_id" --platform "$platform" --target-bundle-id "$target_bundle_id" --artifact-root "$artifact_root" --ready-file "$artifact_root/apple-xctest-host-ready.json" --safe-action-id "$safe_action_id" --timeout-seconds "$apple_spike_timeout"
  else
    json_array "${test_arguments[@]}"
  fi
  printf '},"artifactPaths":{"testResults":'
  json_string "$(repo_relative "$results_root")"
  printf ',"artifactRoot":'
  json_string "$(repo_relative "$artifact_root")"
  printf ',"manifest":'
  json_string "$(repo_relative "$manifest_path")"
  printf ',"flowRun":'
  json_string "$(repo_relative "$flow_run_path")"
  printf '},"capability":{"required":'
  if [[ "$platform" =~ ^(ios|maccatalyst|macos)$ ]]; then
    json_string 'apple-test-agent'
    printf ',"sourceAvailable":%s,"available":false,"state":' "$apple_agent_source_available"
    if [[ "$local_apple_runtime" == true && "$apple_agent_source_available" == true ]]; then json_string proof-required; else json_string pending-spike; fi
  else
    json_string 'platform-fixture'
    printf ',"available":true,"state":"planned"'
  fi
  printf '}}\n'
  exit "$EXIT_SUCCESS"
fi

mkdir -p -- "$results_root" "$artifact_root" "$diagnostic_dir"
canonical_results=$(cd -- "$results_root" && pwd -P)
canonical_expected=$(cd -- "$repo_root/artifacts/TestResults/devflow-flow/$platform" && pwd -P)
[[ "$canonical_results" == "$canonical_expected" ]] ||
  die_usage 'The resolved results path traversed a symbolic link.'
[[ ! -e "$manifest_path" && ! -e "$flow_run_path" ]] || {
  printf "flow-qa: Refusing to overwrite existing run artifacts for '%s'.\n" "$run_id" >&2
  exit "$EXIT_USAGE"
}

repository_commit=$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || printf unknown)
host_os=$(uname -srm)
dotnet_sdk=unknown
xcode_version=not-applicable
device_kind=desktop-host
if command -v dotnet >/dev/null 2>&1; then
  dotnet_sdk=$(dotnet --version 2>/dev/null || printf unknown)
fi
if [[ "$(uname -s)" == Darwin ]]; then
  xcode_version=unavailable
  if command -v xcodebuild >/dev/null 2>&1; then
    xcode_version=$(xcodebuild -version 2>/dev/null | tr '\n' ';' || printf unavailable)
  fi
fi
case "$platform" in
  android) device_kind=emulator ;;
  ios)
    if [[ "$physical_device" == true ]]; then device_kind=physical-device; else device_kind=simulator; fi
    ;;
esac
device_id_digest=
[[ -z "$device_id" ]] || device_id_digest=$(sha256_string "$device_id" 2>/dev/null || true)
device_id_json=null
[[ -z "$device_id_digest" ]] || device_id_json=$(json_string "$device_id_digest")
signing_refs_json='{"physical":false,"identityRef":null,"provisioningProfileRef":null,"keychainRef":null}'
if [[ "$physical_device" == true ]]; then
  signing_refs_json="{\"physical\":true,\"identityRef\":$([[ -n "$signing_identity" ]] && json_string "$(sha256_string "$signing_identity")" || printf null),\"provisioningProfileRef\":$([[ -n "$provisioning_profile" ]] && json_string "$(sha256_string "$provisioning_profile")" || printf null),\"keychainRef\":$([[ -n "$keychain" ]] && json_string "$(sha256_string "$keychain")" || printf null)}"
fi

attempts=()
omissions=('{"kind":"diagnostic-rerun","reason":"No automatic diagnostic rerun was performed because replay may mutate state."}')
artifact_records=()
artifact_omissions=()
qualification_json=null
apple_spike_json=null
apple_qa_json=null
status=pending
classification=pending

if ! command -v dotnet >/dev/null 2>&1; then
  status=failed
  classification=prerequisite-missing
  omissions+=('{"kind":"prerequisite","reason":"dotnet was not found. The script does not install SDKs or workloads."}')
  write_artifacts
  print_status
  exit "$EXIT_PREREQUISITE"
fi
if [[ ! -f "$test_project" ]]; then
  status=failed
  classification=prerequisite-missing
  omissions+=('{"kind":"test-project","reason":"The integration test project was unavailable."}')
  write_artifacts
  print_status
  exit "$EXIT_PREREQUISITE"
fi

host_kernel=$(uname -s)
if [[ "$platform" == windows && "$host_kernel" != MINGW* && "$host_kernel" != MSYS* && "$host_kernel" != CYGWIN* ]]; then
  status=failed
  classification=prerequisite-missing
  omissions+=('{"kind":"host-platform","reason":"The windows lane requires a Windows host."}')
  write_artifacts
  print_status
  exit "$EXIT_PREREQUISITE"
fi
if [[ "$platform" =~ ^(ios|maccatalyst|macos)$ && "$host_kernel" != Darwin ]]; then
  status=failed
  classification=prerequisite-missing
  omissions+=("{\"kind\":\"host-platform\",\"reason\":\"The '$platform' lane requires a macOS host.\"}")
  write_artifacts
  print_status
  exit "$EXIT_PREREQUISITE"
fi
if [[ "$platform" == macos && ! -f "$app_project" ]]; then
  status=unsupported
  classification=unsupported-platform
  omissions+=('{"kind":"appkit-sample","reason":"No experimental AppKit sample or fixture project is available."}')
  write_artifacts
  print_status
  exit "$EXIT_PENDING"
fi

cd -- "$repo_root"
if [[ "$platform" == ios || "$platform" == maccatalyst || "$platform" == macos ]]; then
  if [[ "$physical_device" == true ]]; then
    status=pending-spike
    classification=capability-missing
    omissions+=('{"kind":"physical-ios-flow-qa","reason":"The simulator/Mac Catalyst XCTest agent proof does not certify the separately provisioned physical-iOS lane."}')
    write_artifacts
    print_status
    exit "$EXIT_PENDING"
  fi

  # Every non-dry-run official Apple invocation establishes fresh foreground/auth/cancellation/
  # parity evidence before it can execute the Tier-1 corpus. A checked-in source tree is never
  # treated as a capability report.
  run_apple_spike
  apple_spike_exit=$?
  if (( apple_spike_exit != EXIT_SUCCESS )); then
    write_artifacts
    print_status
    exit "$EXIT_PENDING"
  fi

  run_apple_flow_qa
  apple_qa_exit=$?
  if [[ "$platform" == macos ]]; then
    export DEVFLOW_RUN_APPKIT_FLOW_QA=1
    export DEVFLOW_APPKIT_QA_MANIFEST="$apple_qa_manifest_path"
  else
    export DEVFLOW_RUN_APPLE_FLOW_QA=1
    export DEVFLOW_APPLE_QA_MANIFEST="$apple_qa_manifest_path"
  fi
  run_test_command 1
  attempts+=("{\"kind\":\"clean\",\"repetition\":1,\"exitCode\":$command_exit,\"classification\":$(json_string "$command_classification"),\"diagnostic\":$(json_string "$(repo_relative "$diagnostic_dir/test-output-attempt-1.txt")")}")

  if (( apple_qa_exit != 0 )); then
    status=failed
    classification=flow-failure
  elif (( command_exit != 0 )); then
    status=failed
    classification=$command_classification
  else
    status=passed
    classification=passed
  fi
  write_artifacts
  if [[ "$qualification" == true ]]; then
    # Apple evidence is adapted read-only for review. The Android preview policy remains
    # intentionally non-authoritative for iOS, Mac Catalyst, and experimental AppKit, so its
    # not-qualified result cannot relabel a completed Apple runtime attempt.
    run_qualification || true
    # Register the explicit, read-only qualification output in the final hash manifest without
    # converting its advisory result into Apple runtime status.
    write_flow_run
    write_generic_manifest
  fi
  print_status
  case "$classification" in
    passed) exit "$EXIT_SUCCESS" ;;
    flow-failure) exit "$EXIT_FLOW_FAILURE" ;;
    *) exit "$EXIT_PREREQUISITE" ;;
  esac
fi

if [[ "$platform" == android ]]; then
  export DEVFLOW_TEST_PLATFORM=android
  export DEVFLOW_RUN_ANDROID_FLOW_PILOT=1
  export DEVFLOW_FLOW_PILOT_REPEAT="$repeat"
  export DEVFLOW_FLOW_PILOT_ARTIFACT_ROOT="$artifact_root"
  export DEVFLOW_FLOW_PILOT_RESULTS_ROOT="$results_root"
  export DEVFLOW_FLOW_PILOT_WORKFLOW_RUN_ID="$run_id"
  export DEVFLOW_FLOW_PILOT_REPOSITORY_COMMIT="$repository_commit"
  export DEVFLOW_FLOW_PILOT_DEVICE_EVIDENCE_KIND=emulator
  [[ -z "$device_id" ]] || export DEVFLOW_TEST_ANDROID_SERIAL="$device_id"
  run_test_command 1
  attempts+=("{\"kind\":\"clean\",\"repetition\":1,\"exitCode\":$command_exit,\"classification\":$(json_string "$command_classification"),\"diagnostic\":$(json_string "$(repo_relative "$diagnostic_dir/test-output-attempt-1.txt")")}")
elif [[ "$platform" == windows ]]; then
  export DEVFLOW_TEST_PLATFORM=windows
  export DEVFLOW_RUN_WINDOWS_FLOW_QA=1
  export DEVFLOW_FLOW_QA_RUN_ID="$run_id"
  export DEVFLOW_FLOW_QA_REPEAT="$repeat"
  export DEVFLOW_FLOW_QA_ARTIFACT_ROOT="$artifact_root"
  export DEVFLOW_FLOW_QA_APP_PROJECT="$app_project"
  # WindowsFixture owns clean per-flow repetitions; do not multiply repeat values here.
  run_test_command 1
  attempts+=("{\"kind\":\"tier-1-corpus\",\"repetition\":1,\"cleanRepetitionsPerFlow\":$repeat,\"exitCode\":$command_exit,\"classification\":$(json_string "$command_classification"),\"diagnostic\":$(json_string "$(repo_relative "$diagnostic_dir/test-output-attempt-1.txt")")}")
else
  export DEVFLOW_TEST_PLATFORM="$platform"
  export DEVFLOW_FLOW_QA_RUN_ID="$run_id"
  export DEVFLOW_FLOW_QA_REPEAT="$repeat"
  export DEVFLOW_FLOW_QA_APP_PROJECT="$app_project"
  [[ -z "$ios_runtime" ]] || export DEVFLOW_TEST_IOS_VERSION="$ios_runtime"
  if [[ "$physical_device" == true ]]; then
    export DEVFLOW_FLOW_QA_PHYSICAL_DEVICE=1
    export DEVFLOW_FLOW_QA_DEVICE_ID="$device_id"
    export DEVFLOW_IOS_SIGNING_IDENTITY="$signing_identity"
    export DEVFLOW_IOS_PROVISIONING_PROFILE="$provisioning_profile"
    export DEVFLOW_IOS_KEYCHAIN="$keychain"
  fi
  for (( attempt = 1; attempt <= repeat; attempt++ )); do
    run_test_command "$attempt"
    attempts+=("{\"kind\":\"clean\",\"repetition\":$attempt,\"exitCode\":$command_exit,\"classification\":$(json_string "$command_classification"),\"diagnostic\":$(json_string "$(repo_relative "$diagnostic_dir/test-output-attempt-$attempt.txt")")}")
  done
fi

if printf '%s\n' "${attempts[@]}" | grep -q 'capability-missing'; then
  status=capability-missing
  classification=capability-missing
elif printf '%s\n' "${attempts[@]}" | grep -q 'infrastructure-failure'; then
  status=failed
  classification=infrastructure-failure
elif printf '%s\n' "${attempts[@]}" | grep -q 'flow-failure'; then
  status=failed
  classification=flow-failure
else
  status=passed
  classification=passed
fi
write_artifacts

if [[ "$qualification" == true && "$classification" == passed ]]; then
  run_qualification
  qualification_exit=$?
  if (( qualification_exit == EXIT_PENDING )); then
    status=not-qualified
    classification=not-qualified
  elif (( qualification_exit != 0 )); then
    status=failed
    classification=infrastructure-failure
  fi
  write_artifacts
elif [[ "$qualification" == true ]]; then
  omissions+=('{"kind":"qualification","reason":"Qualification was not run because platform execution did not pass."}')
  write_artifacts
fi

print_status
case "$classification" in
  passed) exit "$EXIT_SUCCESS" ;;
  flow-failure) exit "$EXIT_FLOW_FAILURE" ;;
  capability-missing) exit "$EXIT_PENDING" ;;
  not-qualified) exit "$EXIT_PENDING" ;;
  *) exit "$EXIT_PREREQUISITE" ;;
esac
