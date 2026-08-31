package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.PasswordResetConfirmRequest
import com.audiochoice.mobile.data.PasswordResetRequest
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Account recovery on Android, matching iOS.
 *
 * Without a reset, a listener who could not sign in had no route back to their library: their only
 * option was a second account on another address, abandoning the books in the first. One tester reached
 * exactly that after signing in on a second device.
 */
class PasswordResetAndroidTest {

    /**
     * The confirm request must call its field `token`.
     *
     * That is the server's name for it. Renaming it to something clearer here would serialise a field
     * the server ignores, and the reset would fail with a valid code.
     */
    @Test
    fun `the confirm request serialises the field names the server reads`() {
        val body = Json.encodeToString(
            PasswordResetConfirmRequest(token = "123456", newPassword = "BrandNewPassword1"),
        )
        assertTrue("the code is not sent as 'token'", body.contains("\"token\":\"123456\""))
        assertTrue("the password is not sent as 'newPassword'", body.contains("\"newPassword\""))
        assertEquals(
            """{"email":"someone@example.com"}""",
            Json.encodeToString(PasswordResetRequest("someone@example.com")),
        )
    }

    /** Six digits, matching the server, and only digits. */
    @Test
    fun `the code field accepts six digits and nothing else`() {
        val app = source(APP)
        assertTrue(
            "the code length no longer matches the server's six-digit code",
            app.contains("private const val RESET_CODE_LENGTH = 6"),
        )
        assertTrue(
            "the field no longer restricts entry to digits, so a pasted code carrying stray " +
                "characters could be submitted and spend an attempt for an invisible reason",
            app.contains("filter(Char::isDigit).take(RESET_CODE_LENGTH)"),
        )
        assertTrue(
            "the button no longer requires a complete code, so a partial one would be spent",
            app.contains("code.length == RESET_CODE_LENGTH"),
        )
    }

    /**
     * The new password is confirmed.
     *
     * A mistyped password here is worse than at sign-up: the reset succeeds, the old password stops
     * working, and the listener is locked out again by the thing they used to get back in.
     */
    @Test
    fun `the reset requires the new password twice`() {
        val app = source(APP)
        assertTrue(
            "the reset no longer confirms the new password",
            app.contains("\"Confirm new password\""),
        )
        assertTrue(
            "the reset button no longer requires the two to match",
            app.contains("newPassword == confirmPassword"),
        )
        assertTrue(
            "the reset no longer enforces the server's minimum length",
            app.contains("newPassword.length >= 12"),
        )
    }

    /** Sign-up asks for email, password and a confirmation. No display name. */
    @Test
    fun `sign up collects a confirmation and no display name`() {
        val app = source(APP)
        assertTrue("sign-up no longer confirms the password", app.contains("\"Confirm password\""))
        assertFalse(
            "the display name field is back; the server derives a name from the address, so " +
                "collecting one asks for something that changes nothing",
            app.contains("AudioField(name,"),
        )
        assertFalse(
            "register sends a display name again",
            source(AUTH_VIEW_MODEL).contains("name.trim()"),
        )
    }

    /**
     * Neither screen reveals whether an address has an account.
     *
     * The server answers the same either way, deliberately. A client that said "unknown email" would
     * undo that and let anyone discover who is registered.
     */
    @Test
    fun `the reset never says whether an address is registered`() {
        val app = source(APP)
        assertTrue(
            "the notice no longer hedges on whether the address exists",
            app.contains("If that address has an account"),
        )
        listOf("No account", "not registered", "Unknown email", "no such account").forEach { leak ->
            assertFalse(
                "the reset screen discloses whether an address is registered: found '$leak'",
                app.contains(leak),
            )
        }
    }

    /** Recovery is offered on the sign-in path only, where there is a password to recover. */
    @Test
    fun `the reset is offered from sign in`() {
        assertTrue(
            "there is no way to reach the reset, so the feature is unreachable",
            source(APP).contains("Text(\"Forgot password?\""),
        )
    }

    private fun source(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }

    private companion object {
        const val APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val AUTH_VIEW_MODEL = "src/main/java/com/audiochoice/mobile/auth/AuthViewModel.kt"
    }
}
