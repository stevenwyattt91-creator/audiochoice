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
    /// The labels the analysis model is allowed to return.
    /// </summary>
    /// <remarks>
    /// The single source for both the response schema's enum and the allowed-labels list in
    /// the prompt. Those were each written out by hand, so a label could be added to one and
    /// not the others -- and a label the model emits that the taxonomy does not know is
    /// dropped, previously without a word in the log.
    ///
    /// Excludes the three broad violence labels. They exist as mappings so scans made before
    /// the narrow-violence policy still resolve, but the model must not produce new ones: the
    /// Violence switch is reserved for graphic material, torture, and violence involving
    /// children or animals.
    /// </remarks>
    public static readonly IReadOnlyList<string> EnforcedLabels =
    [
        "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
        "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
        "profanity_mild", "profanity_strong", "profanity_sexual", "profanity_slur",
        "violence_graphic", "violence_torture", "violence_children", "violence_animals",
        "substance_alcohol_use", "substance_intoxication", "substance_drug_reference",
        "substance_drug_use", "substance_abuse_overdose",
        "blasphemy_religious_profanity", "blasphemy_statement",
        "self_harm_reference", "self_harm_suicidal_thoughts",
        "self_harm_suicide_attempt", "self_harm_depiction"
    ];

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
