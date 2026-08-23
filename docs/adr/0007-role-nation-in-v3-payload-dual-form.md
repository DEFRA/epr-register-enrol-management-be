# ADR-0007: Role/nation in the v3 HMAC payload — two accepted wire forms

**Date:** 2026-08-22
**Status:** Accepted
**Issue:** RA-469 (hotfix for the 2026-08-22 dev outage)

## Context

RA-469 added a regulator-facing recycling-operations edit whose AC17
authorization (403 for `support-readonly`, 403 on nation mismatch) is
enforced in this service as well as in the BFF, using the `x-cdp-user-role`
and `x-cdp-user-nation` trust headers. Review of PR #142 correctly pointed
out that those two headers were not covered by the v3 HMAC signature
(ADR-0003/ADR-0005: `v3\nclientId\nuserId\nuserName\ntimestamp\nnonce`), so
anything able to alter them on an already-signed request could bypass that
authorization while the signature still validated. The fix folded role and
nation into the canonical payload — as empty strings when absent — giving:

```
v3
clientId
userId or ""
userName or ""
role or ""
nation or ""
timestamp
nonce
```

and the companion change was made to `epr-register-enrol-management-fe`'s
`sign-request.js`, which now emits exactly that eight-line string on every
call.

What the change missed is that `ClientIdAuthenticationHandler.ComputeSignature`
is the contract with **both** callers, in **both** directions:

- `epr-register-enrol-backend`'s `HttpCaseWorkingApiAdapter` signs its
  `/work-items` calls to this service with the original six-line string, and
  never sends role/nation.
- `epr-register-enrol-backend`'s `CaseManagementAuthenticationHandler`
  verifies this service's pushes back to it (`case-management/{id}/status`,
  `/query`) against the same six-line string — and this service's
  `OperatorBackendSigning` / `HttpOperatorBackendPushAdapter` reuse
  `ComputeSignature` to produce those signatures.

Every existing test signed and verified through `ComputeSignature`, so both
sides of this repo moved together and stayed self-consistent; the backend
was not touched. When management-be 0.127.0 auto-deployed to dev at 17:36
UTC, every operator submission started failing with
`401 Invalid x-cdp-auth-signature header` (two extra `\n`-separated empty
fields in the verifier's string), and every push back to the operator
backend would have failed the same way as soon as a caseworker acted on a
work item.

Three options were considered:

1. Forward-fix the backend to the eight-line form in both directions. Correct
   end state, but dev stays broken until a backend release ships, and the
   reverse direction needs a second coordinated step.
2. Roll management-be back to 0.126.0, then do (1). Reopens the role/nation
   tampering gap in the meantime and still needs (1).
3. Make this service accept both forms, and emit the form each peer
   verifies. No change to either sibling, nothing reopened, one deploy.

## Decision

Option 3, in this service only:

- `ComputeSignature` produces the **legacy** six-line form when
  `Role` and `Nation` are both `null`, and the **extended** eight-line form
  when either is supplied. Null (as opposed to empty) is the selector. All
  outbound signing to the operator backend passes null/null, so pushes back
  to `epr-register-enrol-backend` sign the form it verifies.
- Inbound verification (`VerifySignature`) accepts:
  - when an `x-cdp-user-role` or `x-cdp-user-nation` header is present —
    **only** the extended form over the values actually sent;
  - when neither is present — **either** the legacy form (what the operator
    backend sends) **or** the extended form with empty role/nation (what
    management-fe sends on every call that does not forward role/nation).
    Both candidates are computed before the result is combined, so a
    mismatch costs the same whichever form the caller used.

This is not a downgrade path. The two forms cannot collide (header values
cannot contain a newline, so a legacy string always has exactly six lines and
an extended one exactly eight), and in the only case where both are accepted
they bind identical semantic content. A request signed over a real
role/nation fails both candidates once those headers are stripped; a
legacy-signed request fails the only candidate once a role/nation header is
added. The RA-469 tamper-detection property from PR #142 is preserved in
full.

## Consequences

### Positive

- Dev (and any later environment) recovers with a single management-be
  deploy; no coordinated release across three services under incident
  pressure.
- The role/nation integrity fix stays in force — no rollback window in which
  the original review finding is re-exposed.
- The wire format is now pinned in tests by an independent
  re-implementation (`WireFormatReference`), so a future change to the
  canonical string fails CI in this repo instead of surfacing as 401s in a
  sibling's dev logs.

### Negative

- Two wire forms to reason about until the follow-up lands. This is
  transitional debt, tracked as a follow-up:
  1. move `epr-register-enrol-backend` to the extended form in both
     directions (its own verifier should dual-accept during its transition,
     for the same reason);
  2. once the backend is on the extended form in every environment, drop
     legacy acceptance here and make `ComputeSignature` unconditionally
     extended — at that point consider bumping the prefix to `v4` so the
     two forms are distinguishable by inspection;
  3. add a cross-repo contract check (shared canonical test vectors) so
     sign/verify drift between the three services is caught in CI.
- One extra HMAC computation on the mismatch path for requests with no
  role/nation header. Negligible.

### Neutral

- management-fe is unaffected: it already emits the extended form with
  empties, which remains accepted; when it forwards role/nation the extended
  form over those values is the only one accepted, exactly as before this
  ADR.
- Authorization placement is unchanged from ADR-0005 — the only
  backend-side authorization remains the RA-469 recycling-operations
  endpoint; every other endpoint treats role/nation as audit-only claims.

## Verification

- `EprRegisterEnrolManagementBe.Test/Auth/ClientIdSignaturePayloadFormatTests.cs`
  — pins both wire forms byte-for-byte against `WireFormatReference`, and
  covers `VerifySignature`'s acceptance matrix (both forms without
  role/nation; extended-only with; role-signed signature rejected once
  headers are absent; wrong secret rejected in either form).
- `EprRegisterEnrolManagementBe.Test/Auth/ClientIdAuthenticationTests.cs`
  — end-to-end through the real handler: legacy-signed request accepted
  (the outage regression), extended-with-empties accepted, legacy-signed
  rejected once a role header is added, role-signed rejected once
  role/nation headers are stripped. The PR #142 tamper tests are unchanged
  and still pass.
- `EprRegisterEnrolManagementBe.Test/Integrations/OperatorBackend/OperatorBackendSigningTests.cs`
  — pushes to the operator backend sign the legacy form (asserted against
  `WireFormatReference`, not `ComputeSignature`).
- `docs/cdp-deployment.md` and `docs/operator-submission-flow.md` updated
  to document both forms and which peer uses which.
