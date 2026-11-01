$ErrorActionPreference = 'Stop'
if (-not $env:JAVA_HOME) { throw 'Set JAVA_HOME to your JDK 17 installation.' }
if (-not $env:ANDROID_HOME) { throw 'Set ANDROID_HOME to your Android SDK installation.' }
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
