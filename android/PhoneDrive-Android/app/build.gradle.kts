plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// 发布签名配置来自 keystore.properties（由 scripts/create-keystore.ps1 生成，已 gitignore）。
// 文件不存在时 release 退化为 debug 签名，保证无密钥环境下仍可构建。
val ksProps: Map<String, String> = rootProject.file("keystore.properties").let { f ->
    if (!f.exists()) emptyMap()
    else f.readLines()
        .filter { it.contains('=') && !it.trimStart().startsWith("#") }
        .associate { it.substringBefore('=').trim() to it.substringAfter('=').trim() }
}

android {
    namespace = "com.phonedrive.android"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.phonedrive.android"
        minSdk = 26
        targetSdk = 34
        versionCode = 8
        versionName = "0.2.6"
    }

    signingConfigs {
        if (ksProps.isNotEmpty()) {
            create("release") {
                storeFile = rootProject.file(ksProps["storeFile"] ?: "")
                storePassword = ksProps["storePassword"]
                keyAlias = ksProps["keyAlias"]
                keyPassword = ksProps["keyPassword"]
                // minSdk 26 无需 v1（JAR）签名；启用 v2+v3 以支持未来的密钥轮换
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
        }
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (ksProps.isNotEmpty()) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true }
    packaging {
        resources.excludes += setOf(
            "META-INF/DEPENDENCIES", "META-INF/LICENSE", "META-INF/LICENSE.txt",
            "META-INF/license.txt", "META-INF/NOTICE", "META-INF/NOTICE.txt",
            "META-INF/notice.txt", "META-INF/ASL2.0", "META-INF/*.SF",
            "META-INF/*.DSA", "META-INF/*.RSA"
        )
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.03")
    implementation(composeBom)
    androidTestImplementation(composeBom)

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.5")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.datastore:datastore-preferences:1.1.1")

    // SFTP server: Apache MINA SSHD (Apache-2.0)
    implementation("org.apache.sshd:sshd-core:2.12.1")
    implementation("org.apache.sshd:sshd-sftp:2.12.1")
    implementation("org.slf4j:slf4j-api:2.0.13")
    implementation("org.slf4j:slf4j-nop:2.0.13")

    // QR pairing (Phase 4 uses these; declared now to lock versions)
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.google.zxing:core:3.5.3")

    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
