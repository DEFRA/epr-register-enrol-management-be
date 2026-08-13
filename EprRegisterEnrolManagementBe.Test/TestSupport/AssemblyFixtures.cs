using EprRegisterEnrolManagementBe.Test.TestSupport;

// One ephemeral mongod for the entire test assembly instead of one per
// IClassFixture<MongoIntegrationFixture> class. xUnit v3 assembly fixtures
// are created once before any test runs and torn down once after the last
// test finishes, without merging the classes that consume them into a
// single collection — so cross-class parallelism is unaffected, unlike
// ICollectionFixture, which would force all consuming classes to run
// sequentially.
[assembly: AssemblyFixture(typeof(MongoIntegrationFixture))]
