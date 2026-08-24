import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
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
        versionCode = 8
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
            signingConfig = signingConfigs.getByName("debug")
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
            signingConfig = signingConfigs.getByName("debug")
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
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    debugImplementation(libs.androidx.compose.ui.tooling)
}
