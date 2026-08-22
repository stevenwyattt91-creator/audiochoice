namespace AudioChoice.Api.Contracts;

public sealed record FilterRule(
    string Key,
    bool Enabled,
    string Action,
    string Severity);

public sealed record FilterProfileUpsertRequest(
    string Name,
    bool IsActive,
    IReadOnlyList<FilterRule> Rules,
    IReadOnlyList<string> CustomWords);

public sealed record FilterProfile(
    Guid ID,
    string Name,
    bool IsActive,
    IReadOnlyList<FilterRule> Rules,
    IReadOnlyList<string> CustomWords,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BookNoteUpsertRequest(
    Guid LibraryBookID,
    double? PositionSeconds,
    string Text);

public sealed record BookNote(
    Guid ID,
    Guid LibraryBookID,
    double? PositionSeconds,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CollectionUpsertRequest(
    string Name,
    IReadOnlyList<Guid> LibraryBookIDs);

public sealed record LibraryCollection(
    Guid ID,
    string Name,
    IReadOnlyList<Guid> LibraryBookIDs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BookFilterSettingsUpsertRequest(
    IReadOnlyList<Guid> DisabledCategoryIDs,
    IReadOnlyList<Guid> DisabledGroupIDs,
    IReadOnlyList<string> DisabledEventKeys,
    IReadOnlyList<string> DisabledAggregateKeys);

public sealed record BookFilterSettings(
    Guid LibraryBookID,
    IReadOnlyList<Guid> DisabledCategoryIDs,
    IReadOnlyList<Guid> DisabledGroupIDs,
    IReadOnlyList<string> DisabledEventKeys,
    IReadOnlyList<string> DisabledAggregateKeys,
    DateTimeOffset UpdatedAt);
