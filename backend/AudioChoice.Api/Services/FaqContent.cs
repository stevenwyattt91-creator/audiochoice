using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// The canonical help content.
/// </summary>
/// <remarks>
/// Kept in source rather than a database so a change is reviewed like any other change, and so the
/// history of what customers were told is in git. It ships with the API, which means correcting an
/// answer is a deploy rather than an app release in two stores.
///
/// Scope rule for anything added here: describe only what a released build actually does. Narration
/// voices live in the experimental source set and the closed-beta title allowlist is gated behind
/// BetaConfig, so neither belongs in help text a paying listener reads.
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
    public const int Version = 5;

    public static FaqResponse Current { get; } = new(Version, new[]
    {
        new FaqSection("Getting started", new[]
        {
            new FaqEntry(
                "What does AudioChoice do?",
                "It plays audiobooks you already own and lets you decide what you hear. An audiobook " +
                "is scanned once to find where sensitive content occurs, then you choose which kinds " +
                "to skip or mute. The story keeps going; only the moments you asked about are " +
                "removed."),
            new FaqEntry(
                "Does AudioChoice come with audiobooks?",
                "No. AudioChoice does not sell, supply, or lend audiobooks, and there is no catalog to " +
                "borrow from. You bring files you already own, and they stay yours."),
            new FaqEntry(
                "What do I need before I start?",
                "An account, and one audiobook file on your phone or your computer. If the file is on " +
                "your phone already, use Import in the app. If it is on a computer, use the transfer " +
                "tool at audiochoiceapp.com/companion."),
            new FaqEntry(
                "Why does the first play of a book take longer?",
                "Because that recording has not been scanned yet. The scan is what makes filtering " +
                "possible, and it happens once per recording. After that, opening the book is " +
                "immediate."),
        }),

        new FaqSection("Adding audiobooks", new[]
        {
            new FaqEntry(
                "How do I import an audiobook?",
                "Tap Import, choose the file, and AudioChoice copies it into the app's private " +
                "storage on your device. Nothing is uploaded unless that exact recording still needs " +
                "a scan."),
            new FaqEntry(
                "Which file types work?",
                "MP3, M4A, and M4B are the usual audiobook formats. Audible AAX files are supported " +
                "and are converted on your own device using your account's activation. EPUB files are " +
                "imported as reading editions to follow along with, not as audiobooks."),
            new FaqEntry(
                "Where can I buy audiobooks that import cleanly?",
                "Any store that sells a DRM-free download is the simplest route, because the file you " +
                "get is ready to import as-is. Libro.fm is a common choice. Audible works too, with " +
                "one extra conversion step."),
            new FaqEntry(
                "My audiobook is split into many MP3 files. What do I do?",
                "Import them as they are. A single M4B is tidier because it carries chapters and cover " +
                "art in one file, so prefer M4B when a store offers you the choice."),
            new FaqEntry(
                "Can I close the app while a file is converting?",
                "Conversion runs while the app is open. If it is interrupted, importing the same file " +
                "again resumes rather than starting over. Keep the original file until the import has " +
                "finished."),
            new FaqEntry(
                "The title or cover art is wrong or missing.",
                "That comes from the tags inside the file, so a file with sparse tags imports with " +
                "sparse details. Files downloaded straight from a store usually carry full tags; " +
                "files that have been converted by other tools often lose them."),
        }),

        new FaqSection("Audible audiobooks", new[]
        {
            new FaqEntry(
                "Can I listen to my Audible audiobooks?",
                "Yes, for books you own. Audible files are encrypted, so AudioChoice converts them on " +
                "your device using the activation belonging to your own Audible account. The " +
                "conversion never happens on our servers."),
            new FaqEntry(
                "How do I download a book from Audible?",
                "Use a computer rather than the Audible app, because the app keeps its files where " +
                "other apps cannot reach them. Sign in at audible.com, open Library, find the title, " +
                "and choose Download beside it. You will get an AAX file."),
            new FaqEntry(
                "I have the AAX file. What now?",
                "Two routes, both fine. Either move the AAX onto your phone with a cable or your own " +
                "cloud drive and pick it in Import, and AudioChoice converts it there; or convert it " +
                "to M4B on the computer first and send the M4B with the transfer tool. The transfer " +
                "tool itself accepts M4B, M4A, and MP3, so an AAX has to be converted before it can " +
                "be sent that way."),
            new FaqEntry(
                "Why can't I just sign in to Audible inside AudioChoice?",
                "AudioChoice has no access to any store's account, and asking for your Audible " +
                "password is not something we will ever do. You download your own file from the store " +
                "you bought it from, and it goes straight into the app."),
            new FaqEntry(
                "Does converting break Audible's terms or lose my book?",
                "The conversion is for your own listening, on your own device, from a book you " +
                "purchased, and your original file is untouched. AudioChoice does not remove Audible's " +
                "copy or change your Audible library."),
        }),

        new FaqSection("Moving a file from your computer", new[]
        {
            new FaqEntry(
                "What is the transfer tool?",
                "A page at audiochoiceapp.com/companion that sends an audiobook from your computer " +
                "into AudioChoice on your phone. It exists because most audiobooks are easiest to " +
                "download on a computer, and moving a large file by cable or cloud drive is tedious."),
            new FaqEntry(
                "How do I use it, step by step?",
                "On your computer, open audiochoiceapp.com/companion and sign in with the same " +
                "AudioChoice account you use on your phone. Choose the audiobook file. Wait for the " +
                "upload to finish; a QR code appears when it is ready. On your phone, open " +
                "AudioChoice and scan that QR code. The app downloads the file and then imports it " +
                "exactly as though you had picked it on the phone."),
            new FaqEntry(
                "Which formats can the transfer tool send?",
                "M4B, M4A, and MP3. If you select an AAX file, the page pauses and asks you to " +
                "confirm ownership and convert it to M4B first, then send that."),
            new FaqEntry(
                "Do I have to be signed in on both the computer and the phone?",
                "Yes, and to the same account. The transfer is tied to your account, which is what " +
                "stops anyone else's phone from claiming your file."),
            new FaqEntry(
                "How long does a prepared transfer stay available?",
                "Two hours. After that the link expires and you would start it again. Once your phone " +
                "has imported the file, the temporary copy is deleted."),
            new FaqEntry(
                "Is the file safe while it is in transit?",
                "It is encrypted in transit, held only long enough for your phone to collect it, and " +
                "then deleted. Your phone checks the file it received against a fingerprint taken on " +
                "your computer and refuses it if a single byte differs. It is never added to any " +
                "shared or public library."),
            new FaqEntry(
                "I scanned the QR code and nothing happened.",
                "Check that the phone is signed in to the same account, and that the upload on the " +
                "computer actually finished rather than still running. If more than two hours have " +
                "passed the transfer has expired; start it again on the computer."),
            new FaqEntry(
                "The transfer worked but details look wrong on the phone.",
                "Import it again from the transfer page. Metadata comes from inside the file, so a " +
                "file whose tags were stripped by another tool arrives with less information than one " +
                "downloaded straight from a store."),
        }),

        new FaqSection("How filtering works", new[]
        {
            new FaqEntry(
                "How do filters work?",
                "An audiobook is scanned once, and the result records where each kind of content " +
                "occurs. You choose which categories to remove, and playback skips or mutes those " +
                "moments as it reaches them."),
            new FaqEntry(
                "Does filtering change or damage my file?",
                "No. Your file is never rewritten. Filtering happens during playback, so turning a " +
                "filter off brings that content straight back with nothing to restore or re-download."),
            new FaqEntry(
                "Are filters on or off when I import a book?",
                "Every category the scan actually found is switched on to begin with, so the first " +
                "listen is the most filtered one. You then turn off whatever you would rather keep."),
            new FaqEntry(
                "Are the filter settings the same for all my books?",
                "No, each audiobook keeps its own. What you chose for one book does not silently " +
                "change another. You can also save a set of choices as a profile and apply it to " +
                "other books."),
            new FaqEntry(
                "Do my choices follow me to another phone?",
                "Yes. Filter choices, bookmarks, and your place in a book belong to your account, not " +
                "to one device."),
        }),

        new FaqSection("Categories and toggles", new[]
        {
            new FaqEntry(
                "What can be filtered?",
                "Profanity, sexual content, violence, drugs and alcohol, self-harm, and blasphemy. " +
                "You can also add your own words or phrases to remove."),
            new FaqEntry(
                "What is the difference between a category, a subfilter, and an event?",
                "A category is the broad kind, like sexual content. Inside it are subfilters that say " +
                "how much, and inside those are the individual moments the scan found. You can work " +
                "at whichever level suits you: switch off a whole category, or open it and leave most " +
                "of it on while removing one particular moment. Individual moments are described " +
                "plainly, without repeating the content."),
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
                "activity, including short ones."),
            new FaqEntry(
                "If I turn on a stronger filter, does it include the milder ones?",
                "No, and this is the one that surprises people. Each toggle removes only what it " +
                "names. Switching on Complete sex scenes does not switch on Suggestive dialogue, so " +
                "if you want kissing removed as well as scenes, turn on both."),
            new FaqEntry(
                "Kissing was removed and I only wanted sex scenes filtered. Why?",
                "That was a fault in how scenes were graded and it has been corrected. Kissing and " +
                "undressing now sit under Suggestive dialogue and Nudity, not Explicit sexual " +
                "activity. A book scanned before the correction keeps its old grading until it is " +
                "scanned again, so re-import it or use Scan this audiobook on the player to pick up " +
                "the new one."),
            new FaqEntry(
                "Can a whole scene be removed rather than single lines?",
                "Yes. Some categories cover a passage rather than a word, and the passage is removed " +
                "as one piece so you are not dropped into the middle of it."),
            new FaqEntry(
                "How do custom words work?",
                "Add a word or phrase and it is removed wherever it is spoken in that book, alongside " +
                "the categories the scan found. Useful for a name or a term the standard categories " +
                "would not treat as sensitive."),
            new FaqEntry(
                "A filter removed too much, or missed something.",
                "Report it from the player. The report identifies the moment and the control that " +
                "removed it, and never includes your audio or a transcript. Those reports are how the " +
                "grading gets better."),
        }),

        new FaqSection("Scans and editions", new[]
        {
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
                "Why was my book scanned instantly when a friend's took much longer?",
                "Because that exact recording had been scanned before. AudioChoice checks a " +
                "fingerprint of the file first, and if the same recording is already known, the " +
                "existing result is reused and nothing is uploaded at all."),
            new FaqEntry(
                "How do I rescan a book?",
                "Use \"Scan this audiobook\" on the player. Worth doing if the grading has been " +
                "corrected since your book was first scanned, or if a converted copy did not match " +
                "the original."),
            new FaqEntry(
                "I converted or re-tagged a file and it wants to scan again.",
                "Changing a file changes its fingerprint. AudioChoice tries to recognise a converted " +
                "copy of an edition it already knows from the details inside the file, but when the " +
                "tags have been stripped there is nothing left to match on, and a fresh scan is the " +
                "honest answer."),
        }),

        new FaqSection("Parental controls", new[]
        {
            new FaqEntry(
                "How do I stop someone changing the filters?",
                "Set a 4 to 6 digit PIN in Parental Controls, choose the filters for the audiobook, " +
                "then turn the lock on before handing the device over."),
            new FaqEntry(
                "What can someone still do while filters are locked?",
                "Everything except change the filters. They can play, pause, skip chapters, adjust " +
                "speed, and use the sleep timer. Filter choices can be seen but not altered, and " +
                "filtering keeps working normally throughout."),
            new FaqEntry(
                "Does the lock cover every book or just one?",
                "The lock protects filter changes across the app, while the filter choices themselves " +
                "stay per book. So you can set different boundaries for different audiobooks and lock " +
                "all of them at once."),
            new FaqEntry(
                "I forgot the PIN.",
                "The PIN is stored only on that device and cannot be read back by us or by you. " +
                "Contact support@audiochoiceapp.com and we will talk you through resetting it."),
        }),

        new FaqSection("Reading along", new[]
        {
            new FaqEntry(
                "Can I attach an ebook to an audiobook I already have?",
                "Yes. Open the audiobook and attach its EPUB. It lines up word for word with the " +
                "narration, so the reader can highlight and scroll to the passage you are hearing."),
            new FaqEntry(
                "Do my filters apply to the text too?",
                "Yes. Whatever a filter removes from the audio is also hidden from the attached " +
                "text. Turning that filter off brings the words back."),
            new FaqEntry(
                "The reader lost track of where I am. What happened?",
                "The alignment between the text and the audio can have small gaps. The reader keeps " +
                "showing your last known position rather than guessing, and it catches up once the " +
                "audio reaches text it can match again. If you have moved far ahead of the reader, " +
                "look for the button that jumps it straight to where you are listening."),
            new FaqEntry(
                "Does the ebook have to match the audiobook exactly?",
                "It should be the same work. A different translation or a heavily abridged edition " +
                "will not line up well, because the words being spoken are not the words on the page."),
        }),

        new FaqSection("Listening", new[]
        {
            new FaqEntry(
                "What playback controls are there?",
                "Chapters, bookmarks, a sleep timer, and adjustable speed, along with the usual skip " +
                "controls. Your position is saved as you go."),
            new FaqEntry(
                "Does my place in a book sync between devices?",
                "Yes, your progress and bookmarks belong to your account. The audio file itself does " +
                "not travel, so import the same file on the second device and you will be where you " +
                "left off."),
            new FaqEntry(
                "Do chapters work on every file?",
                "Chapters come from the file. M4B files usually carry proper chapter marks, while a " +
                "plain MP3 often does not. Nothing else about filtering or playback depends on them."),
            new FaqEntry(
                "Can I listen with the screen off or in the background?",
                "Yes. Playback and filtering continue with the app in the background or the screen " +
                "locked."),
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
                "Can I sign in with Apple or Google instead of a password?",
                "Yes, either one. Use the same method each time, because signing in a different way " +
                "can create a separate account that does not see your library."),
            new FaqEntry(
                "How do I delete my account?",
                "Open your profile and choose Delete Account. It permanently removes your library, " +
                "your filter choices, and the account itself, and it cannot be undone. If you have a " +
                "paid subscription, cancel it separately in your Apple or Google subscription " +
                "settings, because only the store can stop the billing."),
            new FaqEntry(
                "What does Founder mean on my profile?",
                "Founders are the testers who used AudioChoice before it was released. Their accounts " +
                "have full access at no charge, permanently, and they are never asked to subscribe."),
        }),

        new FaqSection("Subscription", new[]
        {
            new FaqEntry(
                "What do I get, and how am I billed?",
                "A subscription unlocks filtering, the reading edition, and the transfer tool. Apple " +
                "or Google bills it through the account you already use for apps; AudioChoice never " +
                "sees your card details. The current price is shown on the subscribe screen in the app."),
            new FaqEntry(
                "How do I cancel?",
                "In your Apple ID or Google Play subscription settings, not in AudioChoice. Only the " +
                "store can start or stop the billing. Deleting your AudioChoice account does not " +
                "cancel a subscription."),
            new FaqEntry(
                "I subscribed but the app still asks me to.",
                "Use Restore Purchases on the subscribe screen, which asks the store to confirm what " +
                "you own. If it still does not take, close and reopen the app so it re-checks your " +
                "access, and contact support if it persists."),
            new FaqEntry(
                "I paid on my phone. Does it work on my other devices?",
                "Yes, as long as they are signed in to the same AudioChoice account and the same " +
                "store account that made the purchase."),
        }),

        new FaqSection("Privacy and your data", new[]
        {
            new FaqEntry(
                "Does AudioChoice keep my audiobook?",
                "No. Audio stays in the app's private storage on your device. When a scan is needed, " +
                "a fingerprint is checked first so an already-scanned recording is never uploaded " +
                "again, and no transcript is ever sent back to your phone."),
            new FaqEntry(
                "What actually leaves my device during a scan?",
                "Only when that exact recording has never been scanned before: the audio is sent to " +
                "be transcribed and graded, and is not retained afterwards as an AudioChoice " +
                "audiobook collection. Your file is never shared with other listeners and never " +
                "added to any public library."),
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
                "Is my listening used for advertising?",
                "No. What you listen to and what you filter are not sold and are not used to target " +
                "advertising. The full detail is in the privacy policy at " +
                "audiochoiceapp.com/privacy."),
        }),

        new FaqSection("If something goes wrong", new[]
        {
            new FaqEntry(
                "A scan failed or seems stuck.",
                "Your audiobook is not removed when a scan fails. Try importing it again; if it keeps " +
                "failing on the same file, that file may be damaged or only partly downloaded, so " +
                "download it again from the store."),
            new FaqEntry(
                "Playback jumps oddly or skips something it should not.",
                "That is a filter timing problem worth reporting from the player, which records the " +
                "moment and the control responsible. In the meantime, turning off the specific " +
                "subfilter rather than the whole category usually keeps the rest of the book intact."),
            new FaqEntry(
                "How do I contact a person?",
                "Use Support in your profile, or email support@audiochoiceapp.com. Telling us the " +
                "book, roughly where in it, and what you expected to happen makes a real difference."),
        }),
    });
}
