# Seed reference hashing compatibility

Sonar issue [AaAVI_Pprlc9PDceTdtf](https://sonarcloud.io/project/issues?id=DEFRA_epr-register-enrol-management-be&open=AaAVI_Pprlc9PDceTdtf)
flags `ReAccreditationSeeder.GenerateDeterministicReference` under rule S4790.
The reference generator now uses SHA-256 instead of SHA-1.

## Where the hash is used

The private helper has one caller: the seeder's `Build` method. It hashes a
fixture seed key, takes the first four bytes, and maps them to a nine-digit
`RA-` application reference. The result is stored in
`WorkItem.Payload["applicationReference"]` and in the submission audit entry's
`Details["applicationReference"]`. The full hash is never stored or compared.
These references are display/search labels, not authentication credentials or
integrity checks. The numeric format still has a finite collision space;
SHA-256 does not make it a security token or guarantee unique references.

Startup registration requires both a Development host and
`WorkItems:SeedOnStartup=true` (`AddWorkItemSeederIfDevelopment`). Real
submissions use `ApplicationReferenceGenerator` through
`WorkItemService.SubmitAsync`, independently of this helper.

## Existing data and rollout

Seed document IDs come from the separate `WorkItemSeed.DeterministicId` helper.
Its UUID v5 SHA-1 algorithm and namespace remain unchanged. Changing either
would re-key seeded records, risk duplicates, and break fixture migrations
that look up the original IDs. Its existing, narrowly scoped S4790 suppression
documents the UUID v5 requirement.

`WorkItemSeederHostedService` calls `CreateIfAbsentAsync`, which attempts an
insert and treats duplicate keys as a no-op. It never replaces an existing
record. A previously seeded database therefore keeps its old application
references, audit history and IDs when reseeded. No migration is needed to
read those records, and no re-accreditation migration recalculates this hash.

Fresh databases and missing fixtures receive the SHA-256-derived references.
For example, `acme-recycling` changes from `RA-706111075` to `RA-636082265`,
while its ID stays `cc1a0c7f-0b02-5241-93d4-777d37ce10e9`. References can
therefore differ between fresh and previously seeded Development databases.
External scripts that hardcode old fixture reference numbers would need to
adapt. Searches of this backend and the local management frontend and journey
test repositories found no consumer deriving references from the seed key or
depending on the old generated numbers. Comparing all 16 current fixtures'
old and new reference values also found no cross-version collisions, so
partially seeded databases can mix these versions without a reference clash
within this fixture set. This is a source-code assessment;
deployed database contents and external consumers were not inspected.

## Regression coverage

- Seeder tests pin the new reference with the existing UUID, its stability
  across time, its submission audit value, the reference format, and uniqueness
  across the current fixture set.
- A real MongoDB hosted-service test starts with the original Acme UUID and
  SHA-1-derived reference, runs seeding twice, and verifies that the entire
  stored document is unchanged while missing fixtures are inserted without
  duplicates.
- Existing UUID v5 contract and concurrent seeding tests continue to protect
  database identity and idempotency.
