namespace AudioChoice.Api.Services;

/// <summary>
/// Who is still allowed to use this server.
/// </summary>
/// <remarks>
/// AudioChoice closed as a business. The apps stay installed and the scanning pipeline stays
/// running for the two accounts that still use it, so the server cannot simply be switched off --
/// but it must stop admitting anyone else, including the accounts that already exist.
///
/// An allowlist rather than a disable flag, because "closed" has to mean closed to everyone except
/// a named few, and because the alternative -- deleting every other account -- destroys library,
/// progress and bookmark data that costs nothing to leave alone and cannot be recovered if the
/// decision is ever revisited.
///
/// Defaulted in code rather than left to configuration. A misread environment variable would
/// either lock out the two remaining listeners or quietly reopen the service, and neither failure
/// announces itself. Configuration still overrides it, so the list can change without a deploy.
/// </remarks>
public sealed class ServiceAccessOptions
{
    /// <summary>
    /// The only addresses that may sign in. Comma-separated. Empty means the server is open.
    /// </summary>
    /// <remarks>
    /// Empty meaning open is deliberate: this type is read on every authenticated request, and a
    /// configuration mistake that blanked it should restore the previous behaviour rather than lock
    /// the owner out of his own server with no way back in short of a redeploy.
    /// </remarks>
    public string AllowedEmails { get; init; } =
        "steven.wyattt91@gmail.com,cheyennee.wyatt@gmail.com";

    /// <summary>What someone else is told when they try to sign in.</summary>
    public string ClosedMessage { get; init; } =
        "AudioChoice is no longer in business and is not accepting listeners. " +
        "Thank you for having been part of it.";

    public IReadOnlyList<string> AllowedEmailList => AllowedEmails
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Reduces an address to the mailbox it actually reaches.
    /// </summary>
    /// <remarks>
    /// Gmail ignores dots in the local part and everything from a '+' onward, so
    /// <c>stevenwyattt91@gmail.com</c>, <c>steven.wyattt91@gmail.com</c> and
    /// <c>steven.wyattt91+anything@gmail.com</c> are one account that Google will hand back
    /// under whichever spelling it has on file. Comparing the raw strings locked the owner out
    /// of his own server over a single dot, with a message saying the service was closed.
    ///
    /// This collapses aliases of the same mailbox and nothing else: it widens the list by zero
    /// accounts, because Google already treats these as the same person. Deliberately limited to
    /// Google's own domains -- a dot is significant in a local part generally, and stripping one
    /// elsewhere really would admit a different mailbox.
    /// </remarks>
    private static string Canonical(string? email)
    {
        var trimmed = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1) return trimmed;

        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..];
        if (domain is not ("gmail.com" or "googlemail.com")) return trimmed;

        var plus = local.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) local = local[..plus];
        local = local.Replace(".", string.Empty, StringComparison.Ordinal);
        // An address that is nothing but dots and tags is not a mailbox. Fall back to the
        // address as given rather than inventing "@gmail.com" as something that could match.
        return local.Length == 0 ? trimmed : $"{local}@gmail.com";
    }

    /// <summary>Whether the server is admitting only a named list.</summary>
    public bool IsClosed => AllowedEmailList.Count > 0;

    /// <summary>
    /// Whether this address may use the server.
    /// </summary>
    /// <remarks>
    /// Normalised the same way <c>PostgresAccountStore.NormalizeEmail</c> normalises what it stores,
    /// so an address typed with different casing or surrounding space still matches the row it
    /// belongs to. A null or blank address is refused when the server is closed: an identity with no
    /// email cannot be matched against the list, and admitting it would be a hole rather than a
    /// kindness -- Sign in with Apple's private relay is exactly that case.
    /// </remarks>
    public bool Allows(string? email)
    {
        if (!IsClosed) return true;
        var canonical = Canonical(email);
        if (canonical.Length == 0) return false;
        // Both sides are canonicalised, so the list can be written with or without the dots and
        // still match whichever spelling the provider reports. Linear over a list of two.
        foreach (var allowed in AllowedEmailList)
        {
            if (string.Equals(Canonical(allowed), canonical, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
