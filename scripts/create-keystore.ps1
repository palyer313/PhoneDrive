# 生成 PhoneDrive Android 发布签名密钥
# 生成后 keystore.properties 与 .jks 均不应提交到版本库（已在 .gitignore 中排除）
param(
    [string]$Alias = "phonedrive",
    [string]$StorePassword = "",
    [string]$KeyPassword = "",
    [string]$DName = "CN=PhoneDrive, OU=PhoneDrive, O=PhoneDrive, L=Local, ST=Local, C=CN"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\env.ps1"

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "android\PhoneDrive-Android"
$ksDir = Join-Path $proj "keystore"
$ksPath = Join-Path $ksDir "phonedrive-release.jks"
$propsPath = Join-Path $proj "keystore.properties"

if (Test-Path $ksPath) {
    Write-Warning "密钥已存在: $ksPath（如需重建请先手动删除，重建会导致已安装版本无法升级）"
    exit 0
}

if (-not $StorePassword) {
    $StorePassword = Read-Host "设置 keystore 密码（至少 6 位）"
}
if (-not $KeyPassword) { $KeyPassword = $StorePassword }
if ($StorePassword.Length -lt 6) { throw "密码至少 6 位" }

New-Item -ItemType Directory -Force -Path $ksDir | Out-Null

# keytool 会把进度信息写到 stderr，PowerShell 在 Stop 模式下会误判为失败 —— 这里临时放宽
$ErrorActionPreference = "Continue"
& "$env:JAVA_HOME\bin\keytool.exe" -genkeypair `
    -keystore $ksPath `
    -storetype PKCS12 `
    -alias $Alias `
    -keyalg RSA -keysize 4096 -validity 10000 `
    -storepass $StorePassword -keypass $KeyPassword `
    -dname $DName 2>&1 | Out-Null
$ErrorActionPreference = "Stop"

if (-not (Test-Path $ksPath)) { throw "keytool 生成失败" }

@"
storeFile=keystore/phonedrive-release.jks
storePassword=$StorePassword
keyAlias=$Alias
keyPassword=$KeyPassword
"@ | Set-Content -Path $propsPath -Encoding UTF8

Write-Host "已生成签名密钥:" -ForegroundColor Green
Write-Host "  keystore : $ksPath"
Write-Host "  配置     : $propsPath"
Write-Host "提示：keystore 丢失后无法覆盖安装升级，请妥善备份。" -ForegroundColor Yellow
