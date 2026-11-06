# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ca08a54f -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->


## Build & Test

_Add your build and test commands here_

```bash
# Example:
# npm install
# npm test
```

## SonarCloud scan — before calling a PR done

The SonarCloud quality gate is not a required check, so a PR can be green and mergeable while
failing it. Once the PR is open:

1. Run `scripts/sonar.sh > /tmp/sonar-management-be.log 2>&1; echo "exit=$?"`, then read only the
   report: `sed -n '/^Quality gate:/,$p' /tmp/sonar-management-be.log`. It runs the full test suite
   for coverage, so it can be your one full-suite run.
2. `exit=0`: the gate passed. `exit=1`: fix every gate failure it lists, plus any MINOR issue in
   code you wrote (INFO can stay); re-scan once, then push.
3. `exit=2`: it could not run, and the message says why. **If the message is about the token,
   stop and tell the human.** Never create, request, search for, print or copy a token, and
   never use CI's secrets — only a person can set one up (README.md, "Running the SonarCloud
   scan locally"). `scripts/sonar.sh --check-token` checks the setup without scanning.

## Architecture Overview

_Add a brief overview of your project architecture_

## Conventions & Patterns

_Add your project-specific conventions here_
