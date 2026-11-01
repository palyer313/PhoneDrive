using PhoneDrive.Core.Models;
using PhoneDrive.Core.Mount;

namespace PhoneDrive.Wpf.Services;

/// <summary>
/// 盘符挂载编排：把"确保会话已通过指纹校验 → 预置 known_hosts → 调 sshfs-win"这套
/// 安全前置步骤收敛到一处，供设备管理与自动挂载共用。
/// 任何路径都不会跳过 Host Key 校验：PinHostKey 用的指纹只能来自 SftpSession 校验通过后的
/// VerifiedHostKeyName/Base64，取不到则直接放弃挂载。
/// </summary>
public sealed class DeviceMountService : IDisposable
{
    public sealed record MountOutcome(bool Success, string Message);

    private readonly ConnectionManager _connections;
    private readonly DriveMountService _driver = new();

    public DeviceMountService(ConnectionManager connections) => _connections = connections;

    public async Task<MountOutcome> MountAsync(
        PairedDevice device, string letter, string? host = null, int port = 0,
        CancellationToken ct = default)
    {
        var normalized = letter.TrimEnd(':').ToUpperInvariant();
        if (normalized.Length != 1 || !char.IsLetter(normalized[0]))
            return new MountOutcome(false, $"非法盘符: {letter}");
        normalized += ":";

        if (DriveMountService.FindSshfsWin() == null)
            return new MountOutcome(false, "未找到 sshfs-win.exe，请先安装 SSHFS-Win 官方包");
        if (!DriveMountService.IsWinFspInstalled())
            return new MountOutcome(false, "未检测到 WinFsp，请先安装 WinFsp");

        if (IsDrivePresent(normalized))
            return new MountOutcome(false, $"盘符 {normalized} 已被占用");

        try
        {
            var session = _connections.GetSession(device.DeviceId);
            if (session is null || !session.IsConnected)
            {
                if (string.IsNullOrWhiteSpace(host))
                    host = device.LastKnownIp;
                if (string.IsNullOrWhiteSpace(host))
                    return new MountOutcome(false, "设备地址未知，无法连接后再挂载");
                if (port <= 0) port = device.LastKnownPort;

                var (connectHost, connectPort) = (host!, port <= 0 ? 2222 : port);
                await _connections.ConnectAsync(device, connectHost, connectPort, ct)
                    .ConfigureAwait(true);
                session = _connections.GetSession(device.DeviceId);
            }

            if (session?.VerifiedHostKeyName == null || session.VerifiedHostKeyBase64 == null)
                return new MountOutcome(false, "未取得经校验的 Host Key，出于安全考虑取消挂载");

            // 先固定指纹（写 known_hosts），再挂载 —— 顺序不可调换
            DriveMountService.PinHostKey(
                session.Host, session.Port, session.VerifiedHostKeyName, session.VerifiedHostKeyBase64);

            var keyPath = PhoneDrive.Core.Security.MountIdentity.CreateKeyFile(_connections.ClientKey);
            device.MountLetter = normalized;
            var result = await _driver.MountAsync(device, session.Host, session.Port, keyPath, ct).ConfigureAwait(false);
            return new MountOutcome(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            return new MountOutcome(false, "挂载失败: " + ex.Message);
        }
    }

    public async Task<MountOutcome> UnmountAsync(string driveLetter, CancellationToken ct = default)
    {
        var result = await _driver.UnmountAsync(driveLetter, ct).ConfigureAwait(true);
        return new MountOutcome(result.Success, result.Message);
    }

    public void Dispose() => _driver.Dispose();

    public static bool IsDrivePresent(string letter) =>
        DriveInfo.GetDrives().Any(d => string.Equals(
            d.Name, letter.TrimEnd(':') + ":\\", StringComparison.OrdinalIgnoreCase));
}
