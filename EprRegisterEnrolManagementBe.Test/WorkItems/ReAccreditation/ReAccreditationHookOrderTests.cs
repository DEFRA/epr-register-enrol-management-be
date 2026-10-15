using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Hooks run in registration order inside the caller's request. The email hook
/// waits on GOV.UK Notify, so the pushes that keep the operator backend in step
/// must be registered ahead of it: a slow or failing email must not be able to
/// delay them past the time the caller (management-fe) is willing to wait.
/// </summary>
public class ReAccreditationHookOrderTests
{
    private static List<Type?> RegisteredHookTypes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notify:Enabled"] = "true" })
            .Build();
        var services = new ServiceCollection();

        new ReAccreditationModule().RegisterServices(services, configuration);

        return services
            .Where(d => d.ServiceType == typeof(IWorkItemPostActionHook))
            .Select(d => d.ImplementationType)
            .ToList();
    }

    [Fact]
    public void Operator_backend_pushes_are_registered_before_the_email_hook()
    {
        var hooks = RegisteredHookTypes();

        var email = hooks.IndexOf(typeof(ReAccreditationNotificationHook));
        Assert.True(email >= 0, "the notification hook should be registered when Notify is enabled");
        Assert.True(hooks.IndexOf(typeof(ReAccreditationQueryPushHook)) < email);
        Assert.True(hooks.IndexOf(typeof(ReAccreditationStatusPushHook)) < email);
    }
}
