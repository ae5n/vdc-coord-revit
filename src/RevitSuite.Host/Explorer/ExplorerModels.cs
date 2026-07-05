using System;
using System.Collections.Generic;

namespace RevitSuite.Host.Explorer
{
    public enum ExplorerScope
    {
        EntireProject,
        ActiveView,
        CurrentSelection
    }

    public enum GroupingMode
    {
        Category,
        Model,
        Level,
        Workset,
        OwnerView,
        DesignOption
    }

    /// <summary>
    /// Immutable snapshot of one Revit element. Never holds a live Revit API object,
    /// so instances are safe to share with the WPF UI thread.
    /// </summary>
    public sealed record ElementRecord(
        long IdValue,
        string UniqueId,
        string? Category,
        string? Family,
        string? TypeName,
        string? InstanceName,
        long? TypeIdValue,
        string? LevelName,
        string? WorksetName,
        string? OwnerViewName,
        string? DesignOptionName,
        string Origin,
        bool IsLinked,
        long? LinkInstanceIdValue,
        bool IsElementType,
        bool IsViewSpecific,
        bool IsPinned,
        bool IsInGroup)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(InstanceName) ? $"Element {IdValue}" : $"{InstanceName} [{IdValue}]";

        private string? _searchText;

        /// <summary>Normalized lowercase haystack used by the debounced search box.</summary>
        public string SearchText => _searchText ??= string.Join(" ",
                Category ?? string.Empty,
                Family ?? string.Empty,
                TypeName ?? string.Empty,
                InstanceName ?? string.Empty,
                LevelName ?? string.Empty,
                WorksetName ?? string.Empty,
                OwnerViewName ?? string.Empty,
                DesignOptionName ?? string.Empty,
                Origin,
                IdValue.ToString())
            .ToLowerInvariant();
    }

    public enum ParameterStorageKind
    {
        None,
        String,
        Integer,
        Double,
        ElementId
    }

    public sealed record ParameterValueDto(
        string DisplayName,
        string StableKey,
        ParameterStorageKind Kind,
        double? NumericValue,
        string? DisplayValue,
        bool IsReadOnly);

    public enum QueryOperator
    {
        Equals,
        NotEquals,
        Contains,
        NotContains,
        StartsWith,
        EndsWith,
        IsEmpty,
        IsNotEmpty,
        Regex,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Between,
        HasParameter,
        MissingParameter
    }

    public enum LogicalOperator
    {
        And,
        Or
    }

    public sealed record QueryCondition(
        string ParameterKey,
        string? ParameterDisplayName,
        QueryOperator Operator,
        string? Value,
        string? Value2);

    public sealed record QueryDefinition(
        string Id,
        string Name,
        ExplorerScope Scope,
        IReadOnlyList<string> Categories,
        IReadOnlyList<QueryCondition> Conditions,
        LogicalOperator Operator,
        bool IncludeElementTypes,
        bool IncludeLinkedDocuments = false);

    public enum WarningRank
    {
        NotRanked,
        Low,
        Medium,
        High
    }

    public sealed record WarningRecord(
        string WarningKey,
        string FailureDefinitionId,
        string Description,
        WarningRank Rank,
        IReadOnlyList<long> FailingElementIds,
        IReadOnlyList<long> AdditionalElementIds,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> ElementNames,
        string Origin = "Host",
        long? LinkInstanceIdValue = null);

    public sealed record WarningRanking(
        string? FailureDefinitionId,
        string? DescriptionPattern,
        WarningRank Rank);

    public sealed record WarningSnapshot(
        int SchemaVersion,
        string ModelIdentity,
        string ModelTitle,
        DateTimeOffset CreatedUtc,
        IReadOnlyList<WarningRecord> Warnings);

    public sealed record WarningDiff(
        IReadOnlyList<WarningRecord> NewWarnings,
        IReadOnlyList<WarningRecord> ResolvedWarnings,
        DateTimeOffset BaselineUtc,
        DateTimeOffset CurrentUtc);

    public enum AuditSeverity
    {
        Info,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// One audit rule. Either <paramref name="DetectorId"/> names a built-in detector,
    /// or <paramref name="Query"/> defines a user-authored parameter query.
    /// </summary>
    public sealed record AuditRule(
        string Id,
        string Name,
        string Description,
        AuditSeverity Severity,
        string WhyItMatters,
        string SafeFixGuidance,
        string? DetectorId,
        QueryDefinition? Query,
        bool Enabled = true);

    public sealed record AuditRulePack(
        int SchemaVersion,
        string PackId,
        string PackName,
        string Author,
        IReadOnlyList<AuditRule> Rules);

    public sealed record AuditFinding(
        string RuleId,
        string RuleName,
        AuditSeverity Severity,
        IReadOnlyList<long> ElementIds,
        string Summary,
        string WhyItMatters,
        string SafeFixGuidance,
        string Origin = "Host",
        long? LinkInstanceIdValue = null);

    public sealed record HealthScoreComponent(
        string Label,
        AuditSeverity Severity,
        int Count,
        double Deduction);

    public sealed record HealthScore(
        double Score,
        IReadOnlyList<HealthScoreComponent> Components,
        DateTimeOffset ComputedUtc);

    public sealed record AuditSnapshot(
        int SchemaVersion,
        string ModelIdentity,
        string ModelTitle,
        DateTimeOffset CreatedUtc,
        IReadOnlyList<AuditFinding> Findings,
        HealthScore Health);

    public sealed record ViewRecord(
        long IdValue,
        string Name,
        string ViewKind,
        bool IsTemplate,
        bool IsPlacedOnSheet,
        IReadOnlyList<string> SheetNumbers,
        string? ViewTemplateName,
        string? LevelName,
        bool HasDuplicateName)
    {
        public string SheetNumbersText => string.Join("; ", SheetNumbers);
    }

    /// <summary>
    /// Preflight facts shown in the safe-delete confirmation dialog.
    /// <paramref name="DependentCount"/> is the number of additional elements Revit will
    /// cascade-delete (computed by a rolled-back trial delete); -1 when it could not be determined.
    /// </summary>
    public sealed record DeletePreflight(
        int ElementCount,
        IReadOnlyDictionary<string, int> CategoryCounts,
        int ViewSpecificCount,
        int PinnedCount,
        int OwnedByOthersCount,
        bool IsWorkshared,
        int DependentCount,
        IReadOnlyDictionary<string, int> DependentCategoryCounts);
}
