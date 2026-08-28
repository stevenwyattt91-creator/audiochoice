package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent
import java.io.File
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The taxonomy table in this app is one of four copies of the same thing: the contract file
 * declares it, the backend implements it, and each mobile client mirrors it.
 *
 * A group missing from this copy is a filter the listener has no switch for. A group present
 * here that the scanner no longer reports is a control that does nothing. A group named
 * differently is one they cannot recognise. None of those fail visibly at runtime, which is
 * why they are checked here.
 */
class TaxonomyContractTest {

    private val contract by lazy {
        // Walk up from the working directory: Gradle runs unit tests with the module as the
        // working directory, but that is not guaranteed across invocations.
        var directory: File? = File(".").absoluteFile
        var found: File? = null
        while (directory != null && found == null) {
            val candidate = File(directory, "contracts/content-taxonomy.v2.json")
            if (candidate.isFile) found = candidate
            directory = directory.parentFile
        }
        assertNotNull("The taxonomy contract file could not be found.", found)
        Json.parseToJsonElement(found!!.readText()).jsonObject
    }

    private data class DeclaredGroup(val groupID: String, val categoryID: String, val name: String)

    private fun scanEvent(categoryID: String, groupID: String, start: Double) = ScanEvent(
        id = "event-$groupID",
        startTime = start,
        endTime = start + 1,
        categoryID = categoryID,
        groupID = groupID,
        eventID = "event-id-$groupID",
        confidence = 0.9,
        stableKey = "stable-$groupID",
        safeDescription = "A described moment",
    )

    private fun enforcedGroups(): List<DeclaredGroup> =
        contract["categories"]!!.jsonArray.flatMap { category ->
            val digit = category.jsonObject["digit"]!!.jsonPrimitive.content.toInt()
            val categoryID = category.jsonObject["categoryID"]!!.jsonPrimitive.content.lowercase()
            category.jsonObject["groups"]!!.jsonArray
                .map { it.jsonObject }
                .filter { it["enforced"]!!.jsonPrimitive.content.toBoolean() }
                .map { group ->
                    val index = group["index"]!!.jsonPrimitive.content.toInt()
                    DeclaredGroup(
                        groupID = "%d1000000-0000-0000-0000-%012d".format(digit, index),
                        categoryID = categoryID,
                        name = group["name"]!!.jsonPrimitive.content,
                    )
                }
        }

    /** Every group, as the app's own table describes it. */
    private fun appGroups(): Map<String, String> {
        val declared = enforcedGroups()
        val events = declared.mapIndexed { index, group ->
            scanEvent(
                categoryID = group.categoryID,
                groupID = group.groupID,
                start = index.toDouble(),
            )
        }
        return PlaybackFilterTaxonomy.available(events)
            .flatMap { parent -> parent.children }
            .associate { child -> child.id to child.label }
    }

    @Test
    fun `every enforced group has a switch in the app`() {
        val missing = enforcedGroups().map { it.groupID } - appGroups().keys
        assertTrue("Groups declared by the contract but absent from the app: $missing", missing.isEmpty())
    }

    @Test
    fun `the app defines no group the contract does not enforce`() {
        // The reverse direction matters just as much: a switch for a group the scanner is no
        // longer allowed to report would remove content on the strength of nothing.
        val extra = appGroups().keys - enforcedGroups().map { it.groupID }.toSet()
        assertTrue("Groups defined by the app but not enforced by the contract: $extra", extra.isEmpty())
    }

    @Test
    fun `every group is named as the contract names it`() {
        val app = appGroups()
        val mismatched = enforcedGroups()
            .filter { app[it.groupID] != null && app[it.groupID] != it.name }
            .map { "${it.groupID}: contract '${it.name}' vs app '${app[it.groupID]}'" }
        assertTrue("Group names disagree with the contract: $mismatched", mismatched.isEmpty())
    }

    @Test
    fun `the broad violence groups have no switch`() {
        // These exist in the taxonomy so older scans still resolve, but the scanner must not
        // produce them and the app must not offer them. This is the over-filtering the narrow
        // violence policy exists to prevent.
        val unenforced = listOf(
            "31000000-0000-0000-0000-000000000001",
            "31000000-0000-0000-0000-000000000002",
            "31000000-0000-0000-0000-000000000005",
        )
        val app = appGroups().keys
        unenforced.forEach { groupID ->
            assertFalse("The app offers a switch for unenforced group $groupID", app.contains(groupID))
        }
    }

    @Test
    fun `the contract and the app agree on how many controls exist`() {
        assertEquals(enforcedGroups().size, appGroups().size)
    }
}
