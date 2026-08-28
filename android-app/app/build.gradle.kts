import com.google.firebase.appdistribution.gradle.firebaseAppDistribution
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    // Upload-only. No Firebase SDK is added to the app and no google-services.json
    // is required, so this adds nothing to the APK and collects no user data.
    alias(libs.plugins.firebase.appdistribution)
}

val privateProperties = Properties().apply {
    val privateFile = rootProject.file("local.properties")
    if (privateFile.exists()) privateFile.inputStream().use { load(it) }
}

fun privateValue(name: String): String =
    (privateProperties.getProperty(name) ?: "").replace("\\", "\\\\").replace("\"", "\\\"")

android {
    namespace = "com.audiochoice.mobile"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.audiochoice.mobile"
        minSdk = 26
        targetSdk = 37
        versionCode = 20
        versionName = "1.0"

        ndk {
            abiFilters += listOf("arm64-v8a", "x86_64")
        }

        buildConfigField("String", "API_BASE_URL", "\"${privateValue("audiochoice.apiBaseUrl")}\"")
        buildConfigField("String", "GOOGLE_SERVER_CLIENT_ID", "\"${privateValue("audiochoice.googleServerClientId")}\"")
        buildConfigField("boolean", "BETA_BUILD", "false")
        buildConfigField("boolean", "EXPERIMENTAL_BUILD", "false")
        buildConfigField("String", "BETA_VERSION", "\"\"")
        buildConfigField("String", "BETA_DISCORD_URL", "\"\"")
        buildConfigField("String", "BETA_FEEDBACK_FORM_URL", "\"\"")
        manifestPlaceholders["companionTransferScheme"] = "audiochoice"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        externalNativeBuild {
            cmake { cppFlags += "-O3" }
        }
    }

    signingConfigs {
        // A stable signing identity for distributed builds. The debug keystore is
        // generated per machine, so signing beta with it meant every build had a
        // different signature and testers hit INSTALL_FAILED_UPDATE_INCOMPATIBLE
        // ("app not installed") when updating -- recoverable only by uninstalling
        // and losing their local library mappings and reading positions.
        //
        // Configured through local.properties so neither the keystore nor its
        // password ever enters version control. When those values are absent (a
        // fresh clone, or CI without the secret) the build falls back to debug
        // signing rather than failing.
        val betaKeystorePath = privateProperties.getProperty("audiochoice.betaKeystoreFile")
        if (!betaKeystorePath.isNullOrBlank() && file(betaKeystorePath).isFile) {
            create("betaSigning") {
                storeFile = file(betaKeystorePath)
                storePassword = privateProperties.getProperty("audiochoice.betaKeystorePassword")
                keyAlias = privateProperties.getProperty("audiochoice.betaKeyAlias")
                keyPassword = privateProperties.getProperty("audiochoice.betaKeyPassword")
                    ?: privateProperties.getProperty("audiochoice.betaKeystorePassword")
            }
        }
    }

    buildTypes {
        create("beta") {
            initWith(getByName("release"))
            matchingFallbacks += listOf("release")
            applicationIdSuffix = ".beta"
            versionNameSuffix = "-beta"
            resValue("string", "app_name", "AudioChoice Beta")
            buildConfigField("boolean", "BETA_BUILD", "true")
            buildConfigField("String", "BETA_VERSION", "\"1.5\"")
            buildConfigField("String", "BETA_DISCORD_URL", "\"https://discord.gg/Nr5p6Vhes\"")
            buildConfigField("String", "BETA_FEEDBACK_FORM_URL", "\"REPLACE_WITH_FEEDBACK_FORM_URL\"")
            manifestPlaceholders["companionTransferScheme"] = "audiochoice-beta"
            signingConfig = signingConfigs.findByName("betaSigning")
                ?: signingConfigs.getByName("debug")

            firebaseAppDistribution {
                // The Firebase App ID for com.audiochoice.mobile.beta. Not a
                // secret: this value ships inside every APK that uses Firebase.
                appId = "1:105248861745:android:3ebfa5d3097fe0c60c2f10"
                artifactType = "APK"
                // Release notes come from the file below so they can be edited
                // without touching the build script. Set testers or groups on the
                // command line, or in the Firebase console after upload.
                releaseNotesFile = "$rootDir/app/beta-release-notes.txt"
                // Optional. Point audiochoice.firebaseCredentialsFile at a service
                // account JSON in local.properties (now gitignored) to upload
                // without an interactive Firebase login. Left unset, the plugin
                // falls back to the Firebase CLI login or
                // GOOGLE_APPLICATION_CREDENTIALS.
                privateProperties.getProperty("audiochoice.firebaseCredentialsFile")
                    ?.takeIf { it.isNotBlank() }
                    ?.let { serviceCredentialsFile = it }
            }
        }
        create("experimental") {
            initWith(getByName("beta"))
            matchingFallbacks += listOf("beta", "release")
            applicationIdSuffix = ".experimental"
            versionNameSuffix = "-experimental"
            resValue("string", "app_name", "AudioChoice Experimental")
            buildConfigField("String", "BETA_VERSION", "\"Experimental 1\"")
            buildConfigField("boolean", "EXPERIMENTAL_BUILD", "true")
            manifestPlaceholders["companionTransferScheme"] = "audiochoice-experimental"
            // Same reasoning as beta. Its applicationId differs, so signature
            // compatibility is tracked independently, but testers still update
            // in place.
            signingConfig = signingConfigs.findByName("betaSigning")
                ?: signingConfigs.getByName("debug")
        }
        release {
            optimization {
                enable = false
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        compose = true
        buildConfig = true
        resValues = true
    }

    sourceSets["main"].kotlin.directories.add(
        rootProject.file("../android-contract/src/main/kotlin").path
    )
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "4.1.2"
        }
    }
    ndkVersion = "30.0.15729638"
}

dependencies {
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.androidx.credentials)
    implementation(libs.androidx.credentials.play.services.auth)
    implementation(libs.googleid)
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.media3.session)
    implementation(libs.androidx.media3.ui)
    implementation(libs.androidx.work.runtime.ktx)
    implementation(libs.androidx.documentfile)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    debugImplementation(libs.androidx.compose.ui.tooling)
}
