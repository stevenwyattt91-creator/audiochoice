namespace AudioChoice.Api.Services;

/// <summary>
/// Reduces an address to the mailbox it actually reaches.
/// </summary>
/// <remarks>
/// Gmail ignores dots in the local part and everything from a '+' onward, so
/// <c>stevenwyattt91@gmail.com</c>, <c>steven.wyattt91@gmail.com</c> and
/// <c>steven.wyattt91+anything@gmail.com</c> are one account that Google will hand back under
/// whichever spelling it has on file. Comparing raw strings is what let a Google sign-in with
/// the "wrong" dot spelling pass <see cref="ServiceAccessOptions.Allows"/> (which already
/// canonicalized) and then still create a brand-new, empty, duplicate account in
/// <c>PostgresAccountStore.LoginExternal</c> (which did not) -- the real account's existing
/// library, bookmarks and progress were untouched, but the fresh sign-in landed on an unrelated,
/// empty row instead of being matched to them.
///
/// Extracted out of <c>ServiceAccessOptions</c> (where this logic originated) so every place
/// that looks up or matches an account by email uses the exact same canonicalization -- the
/// allowlist check and the account store must never disagree about which strings name the same
/// mailbox.
///
/// Deliberately limited to Google's own domains: a dot is significant in a local part generally,
/// and stripping one elsewhere would admit a different mailbox rather than collapse an alias of
/// the same one.
/// </remarks>
public static class EmailCanonicalizer
{
    public static string Canonical(string? email)
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
}
