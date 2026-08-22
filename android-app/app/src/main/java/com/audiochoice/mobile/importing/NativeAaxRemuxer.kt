package com.audiochoice.mobile.importing

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

class NativeAaxRemuxer(private val context: Context) {
    suspend fun remux(
        source: Uri,
        sourceFileName: String,
        activation: UInt,
    ): AaxConversionResult.Converted = withContext(Dispatchers.IO) {
        check(libraryLoaded) { "The local M4B conversion component is unavailable on this device." }
        val sourceLength = context.contentResolver.openAssetFileDescriptor(source, "r")
            ?.use { it.length }
            ?.takeIf { it > 0 }
        val outputDirectory = File(context.filesDir, "converted-audiobooks").apply { mkdirs() }
        if (sourceLength != null) {
            require(outputDirectory.usableSpace > sourceLength + MINIMUM_FREE_SPACE_BYTES) {
                "This device needs more free storage before converting this audiobook."
            }
        }
        val safeName = sourceFileName.substringBeforeLast('.', sourceFileName)
            .replace(Regex("[^A-Za-z0-9._ -]"), "_")
            .take(120)
            .ifBlank { "Converted audiobook" }
        val output = uniqueOutput(outputDirectory, safeName)
        val coverOutput = File.createTempFile("aax-cover-", ".image", context.cacheDir)
        context.contentResolver.openFileDescriptor(source, "r").use { descriptor ->
            requireNotNull(descriptor) { "The selected AAX file could not be opened." }
            val errorMessage = nativeRemux(
                descriptor.fd,
                output.absolutePath,
                coverOutput.absolutePath,
                activation.toLong(),
            )
            if (errorMessage != null) {
                output.delete()
                coverOutput.delete()
                error(errorMessage)
            }
        }
        require(output.isFile && output.length() > 0) { "The converted M4B file was empty." }
        val coverBytes = coverOutput.takeIf { it.isFile && it.length() > 0 }?.readBytes()
        coverOutput.delete()
        AaxConversionResult.Converted(Uri.fromFile(output), output.name, coverBytes)
    }

    private fun uniqueOutput(directory: File, baseName: String): File {
        var candidate = File(directory, "$baseName.m4b")
        var suffix = 2
        while (candidate.exists()) candidate = File(directory, "$baseName ($suffix).m4b").also { suffix++ }
        return candidate
    }

    private external fun nativeRemux(
        inputDescriptor: Int,
        outputPath: String,
        coverOutputPath: String,
        activation: Long,
    ): String?

    private companion object {
        const val MINIMUM_FREE_SPACE_BYTES = 100L * 1024 * 1024
        val libraryLoaded = runCatching { System.loadLibrary("audiochoice_aax") }.isSuccess
    }
}
