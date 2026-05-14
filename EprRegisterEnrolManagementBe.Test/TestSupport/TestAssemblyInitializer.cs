using System.Runtime.CompilerServices;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// Forces the same Mongo BSON conventions Program.cs registers at
/// production startup to be installed before any test in this assembly
/// touches a WorkItem serializer. Without this, any test class that
/// renders / serialises a WorkItem before a class that uses
/// <see cref="MongoIntegrationFixture"/> sees PascalCase element
/// names (e.g. "TypeId" rather than "typeId") because the
/// <see cref="MongoConventions.Register"/> call is otherwise gated on
/// the integration fixture's static constructor — and xUnit test
/// ordering across collections is non-deterministic in CI.
/// </summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        MongoConventions.Register();
        WorkItemBsonRegistration.Register();
    }
}
