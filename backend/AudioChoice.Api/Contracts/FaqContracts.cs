namespace AudioChoice.Api.Contracts;

/// <summary>
/// The in-app help content, served rather than compiled into each app.
/// </summary>
/// <remarks>
/// Both apps used to hold their own hardcoded copy, and they drifted: Android carried eleven
/// questions and iOS four different ones, so the same product answered differently depending on the
/// phone. Neither mentioned the reading edition, the two library shelves, the voice tiers, rescanning
/// or password reset.
///
/// Serving it fixes the drift and removes an App Store review from the path of correcting a wrong
/// answer, which is what let it go stale. Each app keeps a bundled copy as a fallback, because a help
/// screen that is empty when the network is poor is worse than one that is slightly behind.
/// </remarks>
public sealed record FaqResponse(int Version, IReadOnlyList<FaqSection> Sections);

public sealed record FaqSection(string Title, IReadOnlyList<FaqEntry> Items);

public sealed record FaqEntry(string Question, string Answer);
