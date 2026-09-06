namespace AudioChoice.Api.Processing;

public sealed record TaxonomyMapping(
    Guid CategoryID,
    Guid GroupID,
    Guid EventID);

public static class ContentTaxonomy
{
    public static readonly IReadOnlyDictionary<string, TaxonomyMapping> Mappings =
        new Dictionary<string, TaxonomyMapping>(StringComparer.Ordinal)
        {
            ["sexual_suggestive_dialogue"] = Map(1, 1),
            ["sexual_references"] = Map(1, 2),
            ["sexual_nudity"] = Map(1, 3),
            ["sexual_implied_activity"] = Map(1, 4),
            ["sexual_explicit_activity"] = Map(1, 5),
            ["sexual_complete_scene"] = Map(1, 6),
            ["sexual_violence"] = Map(1, 7),
            ["profanity_mild"] = Map(2, 1),
            ["profanity_strong"] = Map(2, 2),
            ["profanity_sexual"] = Map(2, 3),
            ["profanity_slur"] = Map(2, 4),
            ["violence_mild"] = Map(3, 1),
            ["violence_intense"] = Map(3, 2),
            ["violence_graphic"] = Map(3, 3),
            ["violence_torture"] = Map(3, 4),
            ["violence_death"] = Map(3, 5),
            ["violence_children"] = Map(3, 6),
            ["violence_animals"] = Map(3, 7),
            ["substance_alcohol_use"] = Map(4, 1),
            ["substance_intoxication"] = Map(4, 2),
            ["substance_drug_reference"] = Map(4, 3),
            ["substance_drug_use"] = Map(4, 4),
            ["substance_abuse_overdose"] = Map(4, 5),
            ["blasphemy_religious_profanity"] = Map(5, 1),
            ["blasphemy_statement"] = Map(5, 2),
            ["self_harm_reference"] = Map(6, 1),
            ["self_harm_suicidal_thoughts"] = Map(6, 2),
            ["self_harm_suicide_attempt"] = Map(6, 3),
            ["self_harm_depiction"] = Map(6, 4),

            // Compatibility with scans created by the initial prototype taxonomy.
            ["sexual_explicit"] = new(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("11000000-0000-0000-0000-000000000001"),
                Guid.Parse("11100000-0000-0000-0000-000000000001")),
            ["sexual_implied"] = new(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("11000000-0000-0000-0000-000000000002"),
                Guid.Parse("11100000-0000-0000-0000-000000000002")),
            ["profanity"] = new(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Guid.Parse("21000000-0000-0000-0000-000000000001"),
                Guid.Parse("21100000-0000-0000-0000-000000000001")),
            ["graphic_violence"] = new(
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                Guid.Parse("31000000-0000-0000-0000-000000000001"),
                Guid.Parse("31100000-0000-0000-0000-000000000001")),
            ["self_harm"] = new(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                Guid.Parse("41000000-0000-0000-0000-000000000001"),
                Guid.Parse("41100000-0000-0000-0000-000000000001"))
        };

    /// <summary>
    /// The labels the app offers a listener a switch for, and that any detector -- model or
    /// deterministic -- is allowed to produce an event under.
    /// </summary>
    /// <remarks>
    /// The single source for the taxonomy contract's own "enforced" flag, which both mobile
    /// clients assert their switch tables against. Those were each written out by hand, so a
    /// label could be added to one and not the others -- and a label a detector emits that
    /// the taxonomy does not know is dropped, previously without a word in the log.
    ///
    /// Excludes the three broad violence labels. They exist as mappings so scans made before
    /// the narrow-violence policy still resolve, but nothing must produce new ones: the
    /// Violence switch is reserved for graphic material, torture, and violence involving
    /// children or animals.
    ///
    /// Includes the four profanity labels, because the app's profanity switches must keep
    /// working -- see <see cref="ModelEmittableLabels"/> for the separate, narrower question
    /// of which of these labels Luna itself may propose.
    /// </remarks>
    public static readonly IReadOnlyList<string> EnforcedLabels =
    [
        "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
        "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
        "sexual_violence",
        "profanity_mild", "profanity_strong", "profanity_sexual", "profanity_slur",
        "violence_graphic", "violence_torture", "violence_children", "violence_animals",
        "substance_alcohol_use", "substance_intoxication", "substance_drug_reference",
        "substance_drug_use", "substance_abuse_overdose",
        "blasphemy_religious_profanity", "blasphemy_statement",
        "self_harm_reference", "self_harm_suicidal_thoughts",
        "self_harm_suicide_attempt", "self_harm_depiction"
    ];

    /// <summary>
    /// Of <see cref="EnforcedLabels"/>, the ones Luna's own prompt and response schema may
    /// actually propose.
    /// </summary>
    /// <remarks>
    /// Excludes the four profanity labels. Profanity is matched deterministically, by exact
    /// word, against the transcript's own text -- see
    /// <see cref="DeterministicContentDetector.DetectProfanity"/> -- and always has been. A
    /// model guessing at a literal word it either does or does not say is strictly worse than
    /// exact matching, and every profanity event Luna could produce duplicated a detection
    /// the deterministic pass already made at full confidence, at Luna's own model cost with
    /// no benefit: the app's profanity switches (see <see cref="EnforcedLabels"/>, unchanged)
    /// are populated by the deterministic pass regardless of whether Luna is ever asked about
    /// them.
    /// </remarks>
    public static readonly IReadOnlyList<string> ModelEmittableLabels = EnforcedLabels
        .Where(label => !label.StartsWith("profanity_", StringComparison.Ordinal))
        .ToArray();

    /// <summary>Labels kept only so older scans still resolve; never emitted.</summary>
    public static readonly IReadOnlyList<string> LegacyLabels =
    [
        "violence_mild", "violence_intense", "violence_death",
        "sexual_explicit", "sexual_implied", "profanity", "graphic_violence", "self_harm"
    ];

    private static TaxonomyMapping Map(int category, int group) => new(
        Guid.Parse($"{category}0000000-0000-0000-0000-000000000001"),
        Guid.Parse($"{category}1000000-0000-0000-0000-{group:D12}"),
        Guid.Parse($"{category}1100000-0000-0000-0000-{group:D12}"));
}
