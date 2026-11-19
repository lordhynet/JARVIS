import java.util.Properties
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}
val signing = Properties().apply { val file = rootProject.file(".signing/signing.properties"); if (file.exists()) file.inputStream().use { load(it) } }
android {
    namespace = "uk.jarvis.mobile"
    compileSdk = 35
    defaultConfig { applicationId = "uk.jarvis.mobile"; minSdk = 29; targetSdk = 35; versionCode = 16; versionName = "0.11.1" }
    signingConfigs {
        create("personalTest") {
            if (signing.isNotEmpty()) {
                storeFile = rootProject.file(".signing/jarvis-test.jks")
                storePassword = signing.getProperty("storePassword")
                keyAlias = "jarvis"
                keyPassword = signing.getProperty("storePassword")
            }
        }
    }
    buildTypes {
        release { isMinifyEnabled = false; if (signing.isNotEmpty()) signingConfig = signingConfigs.getByName("personalTest") }
    }
    buildFeatures { compose = true }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    lint { abortOnError = true }
}
dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.04.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.lifecycle:lifecycle-viewmodel-ktx:2.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("com.google.code.gson:gson:2.12.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    testImplementation("junit:junit:4.13.2")
}
