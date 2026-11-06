#!/usr/bin/env bash
# Runs the SonarCloud pull-request analysis that CI runs, from your machine,
# then prints the quality gate, issues, hotspots and uncovered new lines.
#
# The analysis runs on SonarCloud itself (same org, project, quality profiles
# and quality gate as CI), so the results are the ones CI would give for your
# working tree — uncommitted changes included. It replaces the PR's current
# SonarCloud result (and its GitHub check) until CI's next scan.
#
# This file is identical in every epr-register-enrol repo with a SonarCloud
# project; change it in all of them together.
#
# Usage: scripts/sonar.sh [--pr <number>] [--report-only] [--check-token]
#   --pr <number>  PR to analyse as. Defaults to the open PR for the current
#                  branch (needs one: SonarCloud compares a PR against its base).
#   --report-only  Skip the scan; print the PR's latest SonarCloud result.
#                  Needs no token.
#   --check-token  Check the token is set, valid and in the SonarCloud org.
#
# Token: SONAR_TOKEN, else the file ~/.config/sonarcloud/token. How to get one
# is in the README, under "Running the SonarCloud scan locally".
#
# Needs: git, gh, curl, jq, plus npm (JS repos) or dotnet (.NET repos).
# The scanners fetch their own JRE, so no Java install is needed.
#
# Exit codes: 0 gate passed, 1 gate failed, 2 could not run (message says why).
set -euo pipefail

REPO_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SONAR_URL=https://sonarcloud.io
SONAR_SCAN_JS_VERSION=5.0.2
TOKEN_FILE=${XDG_CONFIG_HOME:-$HOME/.config}/sonarcloud/token
TOOLS_DIR=${XDG_CACHE_HOME:-$HOME/.cache}/epr-register-enrol-sonar

die() { echo "sonar: $*" >&2; exit 2; }

pr= report_only=false check_token=false
while (($#)); do
  case $1 in
    --pr) pr=${2:?--pr needs a number}; shift 2 ;;
    --report-only) report_only=true; shift ;;
    --check-token) check_token=true; shift ;;
    -h|--help) sed -n '2,/^set -e/{/^set -e/d;s/^# \{0,1\}//;p}' "$0"; exit 0 ;;
    *) die "unknown argument $1 (see --help)" ;;
  esac
done
cd "$REPO_DIR"

if [[ -z ${SONAR_TOKEN:-} && -r $TOKEN_FILE ]]; then
  SONAR_TOKEN=$(tr -d '[:space:]' <"$TOKEN_FILE")
fi
export SONAR_TOKEN=${SONAR_TOKEN:-}

no_token() {
  die "no SonarCloud token. A person has to create one — agents: stop and ask.
  1. Sign in at $SONAR_URL with GitHub (your account must be in the defra org).
  2. Generate a token at $SONAR_URL/account/security.
  3. Save it:  mkdir -p ${TOKEN_FILE%/*} && (umask 077 && read -rsp 'Token: ' t && echo \"\$t\" >$TOKEN_FILE); echo
  4. Check it: scripts/sonar.sh --check-token
  Or export SONAR_TOKEN. Details: README.md, \"Running the SonarCloud scan locally\"."
}

api() {
  local auth=()
  [[ -n $SONAR_TOKEN ]] && auth=(-u "$SONAR_TOKEN:")
  curl -sSf "${auth[@]}" "$SONAR_URL/api/$1"
}

# The .NET repos keep their scanner settings inline in sonarcloud.yml; reuse
# that exact `begin` line (minus the token and host) so this never drifts.
dotnet_begin_args() {
  local line
  line=$(grep -m1 'dotnet-sonarscanner begin' .github/workflows/sonarcloud.yml) ||
    die "no dotnet-sonarscanner begin line in .github/workflows/sonarcloud.yml"
  sed 's/.*dotnet-sonarscanner begin//' <<<"$line" | xargs -n1 printf '%s\n' |
    grep -v -e '^/d:sonar\.token=' -e '^/d:sonar\.host\.url='
}

if [[ -f sonar-project.properties ]]; then
  kind=js
  project=$(sed -nE 's/^sonar\.projectKey=//p' sonar-project.properties)
  org=$(sed -nE 's/^sonar\.organization=//p' sonar-project.properties)
  report_task=.scannerwork/report-task.txt
else
  kind=dotnet
  mapfile -t begin_args < <(dotnet_begin_args)
  project=$(printf '%s\n' "${begin_args[@]}" | sed -nE 's#^/k:##p')
  org=$(printf '%s\n' "${begin_args[@]}" | sed -nE 's#^/o:##p')
  report_task=.sonarqube/out/.sonar/report-task.txt
fi
[[ -n $project && -n $org ]] || die "could not work out the SonarCloud project key and org"

if $check_token || ! $report_only; then
  [[ -n $SONAR_TOKEN ]] || no_token
  api authentication/validate | jq -e .valid >/dev/null ||
    die "the token is not valid for $SONAR_URL (expired or revoked?) — generate a new one at $SONAR_URL/account/security"
  api "organizations/search?member=true&ps=500" | jq -e --arg org "$org" 'any(.organizations[]; .key == $org)' >/dev/null ||
    die "the token's account is not a member of the $org SonarCloud org — ask an org admin to add you"
fi
if $check_token; then
  echo "sonar: token OK — valid and a member of the $org org"
  exit 0
fi

branch=$(git branch --show-current)
if [[ -n $pr ]]; then
  pr_json=$(gh pr view "$pr" --json number,state,headRefName,baseRefName)
else
  pr_json=$(gh pr view --json number,state,headRefName,baseRefName 2>/dev/null) ||
    die "no open PR for branch ${branch:-(detached HEAD)} — open one (a draft is fine) or pass --pr <number>"
fi
pr=$(jq -r .number <<<"$pr_json")
pr_branch=$(jq -r .headRefName <<<"$pr_json")
pr_base=$(jq -r .baseRefName <<<"$pr_json")
# gh also finds merged and closed PRs for a branch; scanning one would only
# overwrite its final result.
pr_state=$(jq -r .state <<<"$pr_json")
$report_only || [[ $pr_state == OPEN ]] ||
  die "PR #$pr is ${pr_state,,} — open a new PR for this branch, or pass --pr <number>"
$report_only || [[ $pr_branch == "$branch" ]] ||
  echo "sonar: note — scanning this working tree as PR #$pr (head $pr_branch), which is not the checked-out branch" >&2

pr_params=(
  "sonar.pullrequest.key=$pr"
  "sonar.pullrequest.branch=$pr_branch"
  "sonar.pullrequest.base=$pr_base"
)

# Matches CI's Node version, where nvm is available to switch to it.
use_nvmrc_node() {
  local nvm_sh=${NVM_DIR:-$HOME/.nvm}/nvm.sh
  [[ -f .nvmrc && -s $nvm_sh ]] || return 0
  set +u
  # shellcheck source=/dev/null
  source "$nvm_sh"
  nvm use >/dev/null || die "Node $(<.nvmrc) from .nvmrc is not installed — run: nvm install $(<.nvmrc)"
  set -u
}

scan_js() {
  use_nvmrc_node
  [[ -d node_modules ]] || npm ci
  npm test
  npx --yes "@sonar/scan@$SONAR_SCAN_JS_VERSION" \
    "-Dsonar.host.url=$SONAR_URL" "${pr_params[@]/#/-D}"
}

scan_dotnet() {
  # A user-local SDK (dotnet-install.sh) is often only on PATH in interactive
  # shells; the tools' launchers also need DOTNET_ROOT to find it.
  command -v dotnet >/dev/null || PATH=$HOME/.dotnet:$PATH
  command -v dotnet >/dev/null || die "dotnet not found"
  export DOTNET_ROOT=${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}
  # The scanner unpacks its analyzers under $TMPDIR/.sonarqube; two scans
  # sharing that (say backend and management-be at once) corrupt each other.
  scan_tmp=$(mktemp -d)
  export TMPDIR=$scan_tmp
  mkdir -p "$TOOLS_DIR"
  [[ -x $TOOLS_DIR/dotnet-sonarscanner ]] || dotnet tool update dotnet-sonarscanner --tool-path "$TOOLS_DIR"
  [[ -x $TOOLS_DIR/dotnet-coverage ]] || dotnet tool update dotnet-coverage --tool-path "$TOOLS_DIR"
  "$TOOLS_DIR/dotnet-sonarscanner" begin "${begin_args[@]}" \
    "/d:sonar.host.url=$SONAR_URL" "/d:sonar.token=$SONAR_TOKEN" "${pr_params[@]/#//d:}"
  dotnet build --no-incremental
  "$TOOLS_DIR/dotnet-coverage" collect "dotnet test" -f xml -o coverage.xml
  "$TOOLS_DIR/dotnet-sonarscanner" end "/d:sonar.token=$SONAR_TOKEN"
}

wait_for_analysis() {
  local task_id status
  task_id=$(sed -n 's/^ceTaskId=//p' "$report_task")
  echo "sonar: waiting for SonarCloud to process the analysis..." >&2
  for _ in $(seq 120); do
    status=$(api "ce/task?id=$task_id" | jq -r .task.status)
    case $status in
      SUCCESS) return 0 ;;
      FAILED|CANCELED) die "SonarCloud analysis $status: $SONAR_URL/project/background_tasks?id=$project" ;;
    esac
    sleep 5
  done
  die "timed out waiting for SonarCloud task $task_id"
}

if ! $report_only; then
  scan_tmp=
  trap 'rm -rf "$REPO_DIR/.scannerwork" "$REPO_DIR/.sonarqube" "$REPO_DIR/coverage.xml" ${scan_tmp:+"$scan_tmp"}' EXIT
  "scan_$kind"
  wait_for_analysis
fi

q="projectKey=$project&pullRequest=$pr"

echo
gate=$(api "qualitygates/project_status?$q") ||
  die "no SonarCloud result for PR #$pr yet — CI has not scanned it, or it was purged after merging"
jq -r --arg url "$SONAR_URL/summary/new_code?id=$project&pullRequest=$pr" '
  # Ratings come back as 1-5; SonarCloud shows them as A-E.
  def show($metric): if . == null then "-" elif ($metric | test("rating$")) then "ABCDE"[(tonumber - 1):tonumber] else . end;
  .projectStatus |
  "Quality gate: \(.status)   \($url)",
  (.conditions[] | .metricKey as $m |
    "  \(if .status == "OK" then "ok  " else "FAIL" end)  \($m)  \(.actualValue | show($m))  " +
    "(fails if \(if .comparator == "LT" then "<" else ">" end) \(.errorThreshold | show($m)))")
' <<<"$gate"

echo
api "issues/search?componentKeys=$project&pullRequest=$pr&resolved=false&ps=500" | jq -r '
  "Issues: \(.total)",
  (.issues | sort_by(.component, .line)[] |
    "  \(.component | sub("^[^:]*:"; "")):\(.line // 0)  \(.severity // .impacts[0].severity)  \(.rule)  \(.message)")'

echo
api "hotspots/search?$q&status=TO_REVIEW&ps=500" | jq -r '
  "Security hotspots to review: \(.paging.total)",
  (.hotspots[] |
    "  \(.component | sub("^[^:]*:"; "")):\(.line // 0)  \(.vulnerabilityProbability)  \(.ruleKey)  \(.message)")'

echo
api "measures/component_tree?component=$project&pullRequest=$pr&qualifiers=FIL&ps=500&metricKeys=new_uncovered_lines,new_duplicated_lines" | jq -r '
  def val($m): [.measures[] | select(.metric == $m) | (.periods[0].value // .period.value // .value) | tonumber] | first // 0;
  [.components[] | {path, uncovered: val("new_uncovered_lines"), duplicated: val("new_duplicated_lines")}] as $files |
  "Files with uncovered new lines: \([$files[] | select(.uncovered > 0)] | length)",
  ($files[] | select(.uncovered > 0) | "  \(.path)  \(.uncovered) uncovered"),
  "Files with duplicated new lines: \([$files[] | select(.duplicated > 0)] | length)",
  ($files[] | select(.duplicated > 0) | "  \(.path)  \(.duplicated) duplicated")'

[[ $(jq -r .projectStatus.status <<<"$gate") == OK ]]
