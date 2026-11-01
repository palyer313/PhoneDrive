# 下载 PhoneDrive 挂载功能依赖（WinFsp + SSHFS-Win 官方安装包）
#
# 为什么不由 PhoneDrive 安装包内嵌？
#   WinFsp 为 GPLv3 + FLOSS 例外（另售商业许可），SSHFS-Win 的 sshfs 部分为 GPL-2.0+。
#   以「独立官方安装包 + 独立进程调用」方式使用，可避免被认定为衍生作品；
#   直接内嵌其二进制会带来许可证义务争议。
#
# 本脚本只负责下载并校验大小，不静默安装。安装需要管理员权限，由用户自行运行。

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root "third_party\installers"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$targets = @(
    @{
        Name = "winfsp-2.1.25156.msi"
        Url  = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi"
        Page = "https://github.com/winfsp/winfsp/releases"
        Desc = "WinFsp 2.1 —— Windows 用户态文件系统框架（挂载的底层依赖）"
    },
    @{
        Name = "sshfs-win-3.5.20357-x64.msi"
        Url  = "https://github.com/winfsp/sshfs-win/releases/download/v3.5.20357/sshfs-win-3.5.20357-x64.msi"
        Page = "https://github.com/winfsp/sshfs-win/releases"
        Desc = "SSHFS-Win 3.5 —— 把 SFTP 映射为文件系统（提供 sshfs-win.exe）"
    }
)

foreach ($t in $targets) {
    $path = Join-Path $outDir $t.Name
    if (Test-Path $path) {
        Write-Host "已存在，跳过下载: $($t.Name)"
        continue
    }
    Write-Host "下载 $($t.Desc) ..." -ForegroundColor Cyan
    try {
        Invoke-WebRequest -Uri $t.Url -OutFile $path -UseBasicParsing -TimeoutSec 300
        $sizeMB = [math]::Round((Get-Item $path).Length / 1MB, 1)
        Write-Host "  完成: $path ($sizeMB MB)"
    }
    catch {
        Write-Warning "下载失败：$($_.Exception.Message)"
        Write-Host "  请手动从 $($t.Page) 下载 $($t.Name) 放到 $outDir"
    }
}

Write-Host ""
Write-Host "=== 安装步骤（需要管理员权限，请手动执行）===" -ForegroundColor Yellow
Write-Host "  1) 先装 WinFsp:   msiexec /i `"$outDir\winfsp-2.1.25156.msi`""
Write-Host "  2) 再装 SSHFS-Win: msiexec /i `"$outDir\sshfs-win-3.5.20357-x64.msi`""
Write-Host "  3) 验证: Test-Path '${env:ProgramFiles}\SSHFS-Win\bin\sshfs-win.exe'"
Write-Host "  4) 回到 PhoneDrive → 设备管理 → 挂载为盘符"
