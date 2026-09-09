namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Shared page-by-page "visit every work item of a given type, including archived ones"
/// walk used by <see cref="IWorkItemMigration"/> implementations that scan the whole
/// collection for candidates (e.g. ReAccreditation's nation-correction migrations). Extracted
/// because two such migrations had copied this loop verbatim, which SonarCloud flagged as
/// duplication.
/// </summary>
internal static class WorkItemMigrationPaging
{
    public static async Task VisitAllAsync(
        IWorkItemPersistence persistence,
        string typeId,
        Func<WorkItem, CancellationToken, Task> visitCandidateAsync,
        CancellationToken cancellationToken)
    {
        var page = 1;

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [typeId],
                    Page: page,
                    PageSize: WorkItemQuery.MaxPageSize,
                    IncludeArchived: true),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                await visitCandidateAsync(candidate, cancellationToken);
            }

            if (result.Items.Count < WorkItemQuery.MaxPageSize)
            {
                break;
            }

            page++;
        }
    }
}
