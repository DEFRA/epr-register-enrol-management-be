namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: the categories a case type can declare through
/// <see cref="IWorkItemType.Category"/>. A category groups related types so
/// the list can filter on the group without naming each type. A type with no
/// category (re-accreditation) is its own group.
/// </summary>
public static class WorkItemCategories
{
    /// <summary>
    /// In-year changes to an accreditation: add-ORS now; update ORS and
    /// tonnage band upgrade later.
    /// </summary>
    public const string InYearChange = "in-year-change";
}