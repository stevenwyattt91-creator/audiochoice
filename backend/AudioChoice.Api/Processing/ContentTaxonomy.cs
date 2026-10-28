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
            ["sexual_kissing"] = Map(1, 8),
            // Added for the owner's permanent rule narrowing (see EnforcedLabels' own
            // remarks): a genuinely new concept, not a renaming of sexual_explicit_activity
            // or sexual_complete_scene. Originally defined as strictly narrower than those --
            // requiring graphic, mechanical, anatomical sexual description to qualify at all
            // -- then broadened at the owner's explicit request (see EnforcedLabels' own
            // remarks on the second revision) to cover sexual activity leading into, during,
            // and explicitly talked about, while keeping pure tension and clean aftermath
            // playable. Kept the same GUID/group (9) through that second revision: it is
            // still the one concept beyond nudity/violence that gets cut, just drawn at a
            // different line, not a different concept needing a new identity.
            ["sexual_explicit_vulgar_scene"] = Map(1, 9),
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
    /// Narrowed to sexual_* only, at the owner's explicit, permanent request: this deployment
    /// serves two accounts who only ever cared about sexual content, and profanity, violence,
    /// substance use, blasphemy, and self-harm are not filtered at all going forward -- not
    /// "deprioritized," genuinely removed. Every label that used to be enforced here moved to
    /// <see cref="LegacyLabels"/> instead of being deleted outright, so a scan result written
    /// before this change still resolves to a description rather than being dropped or
    /// crashing anything that looks the label up; nothing new is ever produced under them.
    /// </remarks>
    // "sexual_violence" (rape / sexual assault) belongs here and must never be removed by a
    // future edit that is really only trying to remove the separate, non-sexual violence_*
    // category below -- it is explicitly a sexual-content label (category 1, same as every
    // other sexual_* entry), not a violence label that happens to share a word. Anything that
    // falls under the sexual-content umbrella, including rape/sexual assault, stays enforced.
    //
    // Narrowed a second time, permanently, at the owner's explicit request, from the original
    // eight sexual_* labels down to exactly three real concepts:
    //   - sexual_nudity: ONLY a specific anatomical word naming intimate anatomy (genitalia,
    //     breasts, nipples, areolas, buttocks). A general reference to nakedness/undressing
    //     with no anatomical word used does not qualify.
    //   - sexual_explicit_vulgar_scene: originally ONLY graphic, mechanical, anatomical
    //     description of a sexual act. Revised a third time, still at the owner's explicit
    //     request, to a broader ceiling after real production review found the mechanical-
    //     only bar let too much through: the ceiling for "still just tension" is now kissing,
    //     pulling a partner close, embracing, and straddling with nothing further -- the
    //     moment a passage moves past that into undressing with intent, further physical
    //     positioning toward the act, vulgar/explicit sexual dialogue, talk about sexual
    //     activity (including masturbation), or the act itself in any form, it is in scope.
    //     Quiet aftermath (lying together, breathing hard) stays allowed as long as it does
    //     not describe either character's body or recap the act. sexual_suggestive_dialogue,
    //     sexual_references, sexual_implied_activity, sexual_explicit_activity,
    //     sexual_complete_scene, and sexual_kissing are still not enforced as their own
    //     separate labels -- this remains three labels, not a return to eight -- but a
    //     passage that would have tripped one of those now very likely also crosses this
    //     label's own broadened ceiling. See OpenAIContentAnalysisProvider.BuildInput's own
    //     prompt text for the full current rule and worked examples.
    //   - sexual_violence: ONLY an ongoing scene depicting rape or sexual assault as a sexual
    //     act, as it happens. A brief reference or backstory mention (a character stating or
    //     recalling that they were assaulted, without the act itself being depicted) does not
    //     qualify. Unchanged in definition from before; still fully enforced.
    public static readonly IReadOnlyList<string> EnforcedLabels =
    [
        "sexual_nudity", "sexual_explicit_vulgar_scene", "sexual_violence"
    ];

    /// <summary>
    /// Of <see cref="EnforcedLabels"/>, the ones Luna's own prompt and response schema may
    /// actually propose.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="EnforcedLabels"/> now that profanity, violence, substance,
    /// blasphemy, and self-harm are no longer enforced at all -- the historical distinction
    /// (profanity matched deterministically rather than model-proposed) no longer has
    /// anything on the non-sexual side left to apply to. Kept as a separate list rather than
    /// collapsed into a direct alias so a future category added to <see cref="EnforcedLabels"/>
    /// that again needs the same deterministic-vs-model split has somewhere to express that.
    /// </remarks>
    public static readonly IReadOnlyList<string> ModelEmittableLabels = EnforcedLabels;

    /// <summary>Labels kept only so older scans still resolve; never emitted.</summary>
    /// <remarks>
    /// Everything in the previously-enforced profanity, violence, substance, blasphemy, and
    /// self-harm categories lives here now, alongside the pre-existing legacy entries, for the
    /// same reason: a scan made before this change may still have events under these labels,
    /// and those must keep resolving to a <see cref="CanonicalDescription"/> rather than being
    /// silently dropped. Nothing may ever emit a new event under any of these.
    /// </remarks>
    public static readonly IReadOnlyList<string> LegacyLabels =
    [
        "violence_mild", "violence_intense", "violence_death",
        "sexual_explicit", "sexual_implied", "profanity", "graphic_violence", "self_harm",
        "profanity_mild", "profanity_strong", "profanity_sexual", "profanity_slur",
        "violence_graphic", "violence_torture", "violence_children", "violence_animals",
        "substance_alcohol_use", "substance_intoxication", "substance_drug_reference",
        "substance_drug_use", "substance_abuse_overdose",
        "blasphemy_religious_profanity", "blasphemy_statement",
        "self_harm_reference", "self_harm_suicidal_thoughts",
        "self_harm_suicide_attempt", "self_harm_depiction",
        // Moved here when EnforcedLabels was narrowed to exactly three labels (see that
        // list's own remarks): a scan made under the prior eight-label rule may still carry
        // events under these, and those must keep resolving to a CanonicalDescription rather
        // than being dropped. Nothing may ever emit a new event under any of these again.
        "sexual_suggestive_dialogue", "sexual_references", "sexual_implied_activity",
        "sexual_explicit_activity", "sexual_complete_scene", "sexual_kissing"
    ];

    /// <summary>
    /// What a listener is shown for an event of this label, absent a usable description from
    /// whatever detected it.
    /// </summary>
    /// <remarks>
    /// Lives beside the label's own identifiers because it is part of what the label means,
    /// not a detail of one detector. Two callers need the same answer: the analysis provider
    /// falls back to it when a model-written description is missing, too vague, or names the
    /// very content it is meant to hide; and the admin repair endpoint uses it so an event
    /// added by hand to fix a miss reads exactly like the scanned events around it.
    ///
    /// Every string here describes the <em>kind</em> of content and never the content itself,
    /// which is the whole point -- a filter that spells out what it removed has removed
    /// nothing.
    /// </remarks>
    public static string CanonicalDescription(string label) => label switch
    {
        "sexual_suggestive_dialogue" => "Suggestive dialogue or innuendo occurs",
        "sexual_kissing" => "Characters kiss",
        "sexual_references" => "A sexual reference is made",
        "sexual_nudity" => "A character removes clothing or is described without clothing",
        "sexual_implied_activity" => "An intimate encounter is implied",
        "sexual_explicit_activity" => "Characters are described in an intimate encounter",
        "sexual_complete_scene" => "Characters are described in a sustained intimate encounter",
        "sexual_explicit_vulgar_scene" => "A sexual scene is described in explicit, graphic detail",
        "sexual_violence" => "Sexual violence is described",
        "violence_graphic" => "Graphic violence described",
        "violence_torture" => "Torture described",
        "violence_children" => "Violence involving children described",
        "violence_animals" => "Violence involving animals described",
        "substance_alcohol_use" => "Alcohol use described",
        "substance_intoxication" => "Intoxication described",
        "substance_drug_reference" => "Drug reference detected",
        "substance_drug_use" => "Drug use described",
        "substance_abuse_overdose" => "Substance abuse or overdose described",
        "blasphemy_religious_profanity" => "Religious profanity detected",
        "blasphemy_statement" => "Blasphemous statement detected",
        "self_harm_reference" => "Self-harm reference detected",
        "self_harm_suicidal_thoughts" => "Suicidal thoughts described",
        "self_harm_suicide_attempt" => "Suicide attempt described",
        "self_harm_depiction" => "Self-harm depicted",
        _ => "Content event detected"
    };

    private static TaxonomyMapping Map(int category, int group) => new(
        Guid.Parse($"{category}0000000-0000-0000-0000-000000000001"),
        Guid.Parse($"{category}1000000-0000-0000-0000-{group:D12}"),
        Guid.Parse($"{category}1100000-0000-0000-0000-{group:D12}"));
}
