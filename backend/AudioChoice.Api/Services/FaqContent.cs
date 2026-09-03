using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// The canonical help content.
/// </summary>
/// <remarks>
/// Kept in source rather than a database so a change is reviewed like any other change, and so the
/// history of what customers were told is in git. It ships with the API, which means correcting an
/// answer is a deploy rather than an app release in two stores.
/// </remarks>
public static class FaqContent
{
    /// <summary>
    /// Raised whenever the content changes.
    /// </summary>
    /// <remarks>
    /// Lets a client tell a served copy from its own bundled fallback and prefer the newer one, so an
    /// app that has not been updated in a while still shows the better answers.
    /// </remarks>
    public const int Version = 3;

    public static FaqResponse Current { get; } = new(Version, new[]
    {
        new FaqSection("Getting your audiobooks in", new[]
        {
            new FaqEntry(
                "Where can I get audiobooks I can import?",
                "Any audiobook you own as a file will work. Stores that sell DRM-free downloads, such " +
                "as Libro.fm, are the simplest: download the file and import it. You can also import " +
                "audiobooks you lawfully obtained elsewhere."),
            new FaqEntry(
                "How do I import an audiobook?",
                "Tap Import, choose the file, and AudioChoice copies it into the app's private " +
                "storage. Nothing is uploaded unless a scan is needed for that exact file."),
            new FaqEntry(
                "Which file types work?",
                "MP3 and M4B are the usual ones. Audible AAX files can be converted on the device " +
                "using the activation from your own account. EPUB files are imported as reading " +
                "editions rather than audiobooks."),
            new FaqEntry(
                "What is the transfer tool for?",
                "Some audiobooks are easiest to download on a computer. The transfer tool lets you " +
                "send a file from that computer straight into AudioChoice on your phone, so you do " +
                "not have to move it through a cable or a cloud drive."),
            new FaqEntry(
                "Can I close the app while a file converts?",
                "Conversion continues while the app is open. If it is interrupted, importing the same " +
                "file again picks up rather than starting over."),
        }),

        new FaqSection("Filters", new[]
        {
            new FaqEntry(
                "What is the difference between the six sexual content filters?",
                "They are a ladder, and each one means a different amount. Suggestive dialogue is " +
                "flirtation, tension and kissing, however charged. Sexual references are sex spoken " +
                "about rather than happening: a past encounter, a crude joke. Nudity is a body " +
                "described unclothed, or clothing coming off, with nothing further in that passage. " +
                "Implied sexual activity is sex that happens where the narration fades out or cuts " +
                "away. Explicit sexual activity is a sexual act described as it happens. Complete " +
                "sex scenes is the whole scene, from its lead-in to where the story returns to " +
                "something else, and it is applied to every scene containing implied or explicit " +
                "activity, including short ones. Switching on a higher rung does not switch on the " +
                "ones below it, so if you want kissing removed as well as scenes, turn on both."),
            new FaqEntry(
                "Kissing was removed and I only wanted sex scenes filtered. Why?",
                "That was a fault in how scenes were graded and it has been corrected. Kissing and " +
                "undressing now sit under Suggestive dialogue and Nudity, not Explicit sexual " +
                "activity. A book scanned before the correction keeps its old grading until it is " +
                "scanned again, so re-import it or use Scan this audiobook on the player to pick up " +
                "the new one."),
            new FaqEntry(
                "How do filters work?",
                "An audiobook is scanned once, and the result records where each kind of content " +
                "occurs. You choose which categories to remove, and playback skips or mutes those " +
                "moments. Your choices can be protected with a parental-controls PIN."),
            new FaqEntry(
                "Why does one of my audiobooks say filters are unavailable?",
                "Filter results belong to one exact recording. A different edition of the same title " +
                "is a different recording, so it needs its own scan and cannot borrow another one's " +
                "results. Open the player and tap \"Scan this audiobook\" to scan it."),
            new FaqEntry(
                "Two copies of the same book show different filter counts. Why?",
                "They are almost certainly different editions. Cover art that differs is the giveaway. " +
                "AudioChoice refuses to share filter results between recordings it cannot prove are " +
                "identical, because applying one recording's timings to another would remove the " +
                "wrong moments and could play something you asked never to hear."),
            new FaqEntry(
                "Can a whole scene be removed?",
                "Yes. Some categories cover a passage rather than a single word, and the whole passage " +
                "is removed together."),
            new FaqEntry(
                "What if a filter is wrong?",
                "Report it from the player. The report identifies the moment and the control that " +
                "removed it, and never includes the audio or a transcript."),
        }),

        new FaqSection("Reading editions and the voice", new[]
        {
            new FaqEntry(
                "What happens when I import an EPUB?",
                "It goes to the Ebooks shelf in your library, separate from your audiobooks, and " +
                "opens in the reader rather than the player. You can read it, or have it read aloud."),
            new FaqEntry(
                "What is the difference between the two voices?",
                "Your phone's own voice is included and works without a network. The premium voice is " +
                "produced on our servers and sounds closer to a person reading. The passages sent for " +
                "the premium voice are covered by a statement you accept before it is used."),
            new FaqEntry(
                "Why does making the audio take a while?",
                "A chapter is produced in full before it plays, so the first one takes a moment. " +
                "Later chapters are prepared ahead of where you are listening."),
            new FaqEntry(
                "Do my filters apply when a book is read aloud?",
                "Yes. The text is scanned the same way an audiobook is, and anything you filter is " +
                "removed before the voice ever sees it."),
            new FaqEntry(
                "Can I attach an ebook to an audiobook I already have?",
                "Yes, and it stays on your Audiobooks shelf. The reading edition follows along with " +
                "the narration so you can read while you listen."),
        }),

        new FaqSection("Your account", new[]
        {
            new FaqEntry(
                "I cannot sign in on a new device. What now?",
                "Your account works on every device, so the same email and password should sign you " +
                "in. If the password is not accepted, choose \"Forgot password\" and we will email a " +
                "six-digit code you can use to set a new one."),
            new FaqEntry(
                "How long is the reset code good for?",
                "Fifteen minutes, and it can be used once. If it expires, ask for another."),
            new FaqEntry(
                "What does Founder mean on my profile?",
                "Founders are the testers who used AudioChoice before it was released. Their accounts " +
                "have full access at no charge, permanently."),
        }),

        new FaqSection("Your library and privacy", new[]
        {
            new FaqEntry(
                "Does AudioChoice keep my audiobook?",
                "No. Audio stays in the app's private storage on your device. When a scan is needed, " +
                "a fingerprint is checked first so an already-scanned recording is never uploaded " +
                "again, and no transcript is ever sent back to your phone."),
            new FaqEntry(
                "Will I lose my library if the audio is removed?",
                "No. Your books and your place in them belong to your account. Import the file again " +
                "to listen, and you will be where you left off."),
            new FaqEntry(
                "Why do I have to import again on another device?",
                "The audio file itself stays on the device you imported it to; it is never copied to " +
                "our servers. Your library, filters and progress follow your account, so importing " +
                "the same file on a second device picks up where you were."),
            new FaqEntry(
                "How much space does a read-aloud book use?",
                "About a megabyte for every two minutes of audio. The reader shows how much a book is " +
                "using and lets you reclaim it; your place, filters and pronunciations are kept."),
        }),
    });
}
