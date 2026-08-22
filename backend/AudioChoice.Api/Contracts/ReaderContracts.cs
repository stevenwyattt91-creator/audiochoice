using AudioChoice.Api.Processing;

namespace AudioChoice.Api.Contracts;

/// <summary>
/// EPUB text is used in memory to create a local reading map and is never persisted.
/// </summary>
public sealed record ReaderAlignmentRequest(Guid LibraryBookID, string EpubText);

public sealed record ReaderAlignmentResponse(IReadOnlyList<ReaderTimingRange> Ranges);
