# ADR-0009: End-to-end test organisations and the mgmt-tests caller

**Date:** 2026-10-15
**Status:** Accepted
**Issue:** RA-000 (no Jira story: make the mgmt-tests suite run unskipped on CDP test)

## Context

The `epr-register-enrol-mgmt-tests` journey suite has to run unchanged on
the compose stack and on CDP **test**. On test:

- Mongo is never reset, so the suite cannot rely on seeded fixtures. The
  management-be seeder runs in Development only. The suite has to create
  its own uniquely named work items.
- ReEx is real, and it 404s the organisations the suite invents, so the
  RA-254 prior-year section never renders for them.
- Every authenticated request must be HMAC-signed by a registered caller
  (ADR-0003, ADR-0006). The suite is not one of the two existing callers,
  and borrowing management-fe's or the backend's secret would defeat the
  point of per-caller secrets.

## Decision

1. **Test organisations.** An organisation id starting with the lower-case,
   case-sensitive prefix `e2e-` is an end-to-end test organisation. The
   operator journey backend uses the same prefix. The new switch
   `TestOrganisations__Enabled` (default `false`) gates everything below.
   Startup throws when it is `true` and the CDP `ENVIRONMENT` is `prod`
   (`TestOrganisations.EnsureNotEnabledInProd`).
2. **ReEx routing.** When real ReEx credentials are configured and the switch
   is on, `IReExAccreditationClient` is
   `TestOrganisationReExAccreditationClient`. It answers `e2e-`
   organisations from `StubReExAccreditationClient` and sends everything else
   to `HttpReExAccreditationClient`. This applies to both `GetPriorYearAsync`
   and `GetNationAsync`. The stub itself is unchanged, so the compose stack
   (stub for everything) and CDP test produce the same output for test
   organisations.
3. **A third caller.** `epr-register-enrol-mgmt-tests` (`Auth__MgmtTestsClientId`)
   gets its own secret, `AUTH_SHARED_SECRET__MGMT_TESTS`. It is registered
   through the existing `AddCallerSecret` only when that secret is set AND
   the switch is on. It uses the unchanged v3 HMAC contract, timestamp
   window and nonce store.

## Consequences

- Like the other callers, the mgmt-tests caller is not scoped by route or
  by work item: once it is registered it can do anything management-fe can.
  This is acceptable only because the caller exists solely where the switch
  is on, and the switch cannot be on in prod.
- Registering the mgmt-tests secret on a Development stack turns off
  header-trust for every caller (any registered secret does). A stack that
  sets it must set the management-fe and backend secrets too.
- A real organisation whose id happens to start `e2e-` would get stub ReEx
  data on test. ReEx ids are GUIDs or numeric, so no real id collides.

## Verification

- `TestOrganisationsTests`, `CdpEnvironmentTests`: prefix matching, the
  default-off switch, and the prod guard.
- `TestOrganisationsWiringTests`: which ReEx client gets registered for
  each combination of credentials and switch, startup failing in prod with
  the switch on, and startup succeeding in prod with it off.
- `TestOrganisationReExAccreditationClientTests`: routing for both methods.
- `ClientIdAuthenticationMgmtTestsCallerTests`:
  - a signed request is accepted with the switch on;
  - it is rejected with the switch off;
  - it is rejected when signed with another caller's secret;
  - Development header-trust is unchanged when the caller is half
    configured.
