# Keep MINA SSHD service-loader entries and security providers
-keep class org.apache.sshd.** { *; }
-keep class org.bouncycastle.** { *; }
-dontwarn org.apache.sshd.**
-dontwarn org.slf4j.**
-dontwarn java.nio.file.**
