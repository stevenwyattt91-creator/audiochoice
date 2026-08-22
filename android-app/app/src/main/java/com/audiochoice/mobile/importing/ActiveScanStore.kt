package com.audiochoice.mobile.importing

import android.content.Context
import android.net.Uri

data class ActiveScan(
    val scanID: String,
    val audioUri: Uri,
    val fileName: String,
)

class ActiveScanStore(context: Context) {
    private val applicationContext = context.applicationContext
    private val preferences = context.getSharedPreferences("audiochoice_active_scan", Context.MODE_PRIVATE)

    fun load(): ActiveScan? {
        val scanID = preferences.getString("scan_id", null) ?: return null
        val uri = preferences.getString("audio_uri", null)?.let(Uri::parse) ?: return null
        val fileName = preferences.getString("file_name", null) ?: "Imported audiobook"
        return ActiveScan(scanID, uri, fileName)
    }

    fun save(scanID: String, audioUri: Uri, fileName: String) {
        preferences.edit()
            .putString("scan_id", scanID)
            .putString("audio_uri", audioUri.toString())
            .putString("file_name", fileName)
            .apply()
        ScanStatusWorker.schedule(applicationContext)
    }

    fun clear() {
        preferences.edit().clear().apply()
        ScanStatusWorker.cancel(applicationContext)
    }

    fun complete(fileName: String) {
        if (load() != null) ScanCompletionNotifier(applicationContext).notifyReady(fileName)
        clear()
    }
}
