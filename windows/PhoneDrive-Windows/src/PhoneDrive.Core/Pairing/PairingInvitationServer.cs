using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;

namespace PhoneDrive.Core.Pairing;

/// <summary>A short-lived TLS invitation lets an Android camera pair this controller without typing a host pin.</summary>
public sealed class PairingInvitationServer : IDisposable
{
    public sealed record Request(string DeviceId, string Name, string Type, string ClientFingerprint);
    private readonly ClientIdentity _identity;
    private readonly DeviceStore _store;
    private readonly Func<Request, CancellationToken, Task<bool>> _approve;
    private readonly RSA _key;
    private readonly X509Certificate2 _certificate;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(2));
    private readonly TaskCompletionSource<PairedDevice> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string _code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
    private readonly string _publicKey;
    private readonly string _fingerprint;
    private readonly string _host;
    private Task? _loop;
    private TcpClient? _active;
    private int _attempts;
    private int _disposed;

    public PairingInvitationServer(ClientIdentity identity, DeviceStore store, string host,
        Func<Request, CancellationToken, Task<bool>> approve)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("请选择有效的本机 IPv4 地址");
        _identity = identity; _store = store; _approve = approve; _host = host;
        _key = identity.GetOrCreateClientKey();
        _publicKey = ClientIdentity.ExportOpenSshPublicKey(_key);
        _fingerprint = ClientIdentity.FingerprintSha256(_key);
        using var generated = new CertificateRequest("CN=PhoneDrive invitation", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel requires an imported temporary key container. No certificate is installed in a trust store.
        _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet);
        _listener = new TcpListener(address, 0);
    }

    public Task<PairedDevice> Completion => _completion.Task;
    public string Code => _code;
    public DateTimeOffset ExpiresAt { get; private set; }
    public PairingQrContent Start()
    {
        if (_loop != null) throw new InvalidOperationException("邀请已启动");
        _listener.Start(4);
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        _stop.Token.Register(() => _completion.TrySetCanceled());
        _loop = RunAsync();
        return new PairingQrContent {
            Ip = _host, Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Code = _code,
            DeviceId = _identity.GetOrCreateDeviceId(), Name = Environment.MachineName,
            Type = "windows", Fingerprint = _fingerprint,
        };
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && !_completion.Task.IsCompleted)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _active = socket;
                try { await HandleAsync(socket).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch { /* One malformed request does not authorize or end the invitation. */ }
                finally { _active = null; }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _completion.TrySetException(ex); }
        finally { _listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var ssl = new SslStream(client.GetStream());
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
            ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12,
            ClientCertificateRequired = false,
        }, readTimeout.Token).ConfigureAwait(false);
        var header = new List<byte>(); var one = new byte[1];
        while (header.Count < 16384)
        {
            if (await ssl.ReadAsync(one, readTimeout.Token).ConfigureAwait(false) == 0) return;
            header.Add(one[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        if (header.Count >= 16384) { await Reply(ssl, 400, new { ok = false, error = "bad_request" }); return; }
        if (lines[0].StartsWith("GET /info ", StringComparison.Ordinal))
        {
            await Reply(ssl, 200, new { v = 2, deviceid = _identity.GetOrCreateDeviceId(), name = Environment.MachineName,
                type = "windows", fp = _fingerprint, nonce = _nonce, sftpport = 0 });
            return;
        }
        if (!lines[0].StartsWith("POST /pair ", StringComparison.Ordinal)) { await Reply(ssl, 400, new { ok = false, error = "bad_request" }); return; }
        var lengthLine = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (lengthLine == null || !int.TryParse(lengthLine[15..].Trim(), out var length) || length is < 1 or > 65536)
        { await Reply(ssl, 400, new { ok = false, error = "bad_request" }); return; }
        var bytes = new byte[length];
        await ssl.ReadExactlyAsync(bytes, readTimeout.Token).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<PairingRequest>(bytes);
        if (request?.V != 2 || request.Nonce != _nonce ||
            string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 ||
            request.Type is not ("phone" or "tablet") ||
            new[] { request.Name, request.DeviceId, request.HostFingerprint, request.HostPublicKey, request.ClientPublicKey }.Any(s => s == null || s.Contains('\n') || s.Contains('\r')))
        { await Reply(ssl, 400, new { ok = false, error = "bad_request" }); return; }
        if (request.Code != _code)
        {
            var attempts = Interlocked.Increment(ref _attempts);
            await Reply(ssl, attempts >= 3 ? 409 : 401, new { ok = false, error = attempts >= 3 ? "code_expired" : "bad_code" });
            if (attempts >= 3) _stop.Cancel();
            return;
        }
        using var hostKey = PairingProof.ParseRsa(request.HostPublicKey!);
        using var clientKey = PairingProof.ParseRsa(request.ClientPublicKey!);
        var transcript = PairingProof.Transcript(request);
        bool Verify(RSA key, string? signature)
        {
            try { return key.VerifyData(transcript, Convert.FromBase64String(signature ?? ""), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
            catch { return false; }
        }
        if (ClientIdentity.FingerprintSha256(hostKey) != request.HostFingerprint || !Verify(hostKey, request.HostProof) || !Verify(clientKey, request.ClientProof))
        { await Reply(ssl, 400, new { ok = false, error = "bad_proof" }); return; }
        var approved = await _approve(new Request(request.DeviceId, request.Name, request.Type,
            ClientIdentity.FingerprintSha256(clientKey)), _stop.Token).ConfigureAwait(false);
        if (!approved) { await Reply(ssl, 403, new { ok = false, error = "not_approved" }); return; }
        _stop.Token.ThrowIfCancellationRequested();
        var device = _store.Find(request.DeviceId) ?? new PairedDevice {
            DeviceId = request.DeviceId, Name = request.Name, Type = request.Type, HostKeyFingerprint = request.HostFingerprint!,
        };
        device.Name = request.Name; device.Type = request.Type; device.HostKeyFingerprint = request.HostFingerprint!;
        device.PeerClientPublicKey = request.ClientPublicKey;
        device.LastKnownIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        device.LastKnownPort = 2222; device.PairedAt = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(device).ConfigureAwait(false);
        await Reply(ssl, 200, new { ok = true, v = 2, nonce = _nonce, deviceid = _identity.GetOrCreateDeviceId(),
            name = Environment.MachineName, type = "windows", hostFp = _fingerprint, hostPub = _publicKey, clientPub = _publicKey });
        _completion.TrySetResult(device);
    }

    private async Task Reply(SslStream ssl, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token).ConfigureAwait(false);
        await ssl.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
        await ssl.FlushAsync(_stop.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener.Stop(); _active?.Dispose();
        // The worker must stop using the certificate before its temporary key is released.
        if (_loop != null) _ = _loop.ContinueWith(_ => { _certificate.Dispose(); _key.Dispose(); }, TaskScheduler.Default);
        else { _certificate.Dispose(); _key.Dispose(); }
    }
}
