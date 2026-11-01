using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PhoneDrive.Core.Tests;

public sealed class PairingV2Tests
{
    private sealed class Peer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly RSA key = RSA.Create(2048);
        private readonly X509Certificate2 cert;
        private readonly string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public int Status { get; set; } = 200;
        public bool ReplaceHostKey { get; set; }
        public bool ReplayNonce { get; set; }
        public bool SawValidProof { get; private set; }
        public string? Error { get; private set; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public string Pin => ClientIdentity.FingerprintSha256(key);
        public Peer()
        {
            using var generated = new CertificateRequest("CN=PhoneDrive test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Windows Schannel needs a temporary imported key container, not an ephemeral CNG key.
            cert = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet);
            listener.Start();
            _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { try { var c = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => Handle(c)); } catch { return; } } });
        }
        private async Task Handle(TcpClient client)
        {
            using (client) using (var ssl = new SslStream(client.GetStream()))
            {
                try
                {
                    await ssl.AuthenticateAsServerAsync(cert, false, System.Security.Authentication.SslProtocols.Tls12, false);
                    using var reader = new StreamReader(ssl, Encoding.UTF8, false, 4096, true);
                    var first = await reader.ReadLineAsync(); int size = 0;
                    while (await reader.ReadLineAsync() is { Length: > 0 } line)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) size = int.Parse(line[15..].Trim());
                    var chars = new char[size]; int got = 0;
                    while (got < size) { var n = await reader.ReadAsync(chars.AsMemory(got)); if (n == 0) return; got += n; }
                    object body; int status = 200;
                    if (first?.StartsWith("GET ") == true)
                        body = new { v = 2, deviceid = "peer", name = "Test phone", type = "phone", fp = Pin, sftpport = 2222, nonce };
                    else
                    {
                        var request = JsonSerializer.Deserialize<PairingRequest>(new string(chars))!;
                        using var pk = PairingProof.ParseRsa(request.ClientPublicKey!);
                        SawValidProof = request.Nonce == nonce && pk.VerifyData(PairingProof.Transcript(request), Convert.FromBase64String(request.ClientProof!), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        status = Status;
                        if (status != 200) body = new { ok = false, error = status == 401 ? "bad_code" : "code_expired" };
                        else
                        {
                            using var wrong = RSA.Create(2048);
                            body = new { ok = true, v = 2, nonce = ReplayNonce ? "old" : nonce, deviceid = "peer", name = "Test phone", type = "phone", hostFp = Pin,
                                hostPub = ClientIdentity.ExportOpenSshPublicKey(ReplaceHostKey ? wrong : key), clientPub = ClientIdentity.ExportOpenSshPublicKey(key) };
                        }
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                    await ssl.WriteAsync(bytes); await ssl.FlushAsync();
                }
                catch (Exception error) { Error = error.ToString(); }
            }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); cert.Dispose(); key.Dispose(); }
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("wrong_tls_pin", false)]
    [InlineData("same_fp_replaced_key", false)]
    [InlineData("replayed_nonce", false)]
    [InlineData("bad_code", false)]
    [InlineData("expired", false)]
    public async Task PinnedTlsAndPossessionProof(string scenario, bool expected)
    {
        using var peer = new Peer { ReplaceHostKey = scenario == "same_fp_replaced_key", ReplayNonce = scenario == "replayed_nonce", Status = scenario == "bad_code" ? 401 : scenario == "expired" ? 409 : 200 };
        var dir = IdentityAndStoreTests.TempDir(); using var wrong = RSA.Create(2048);
        try
        {
            var store = new DeviceStore(dir);
            var result = await new PairingClient().PairAsync(new ClientIdentity(dir), store, "127.0.0.1", peer.Port, "123456", scenario == "wrong_tls_pin" ? ClientIdentity.FingerprintSha256(wrong) : peer.Pin);
            if (expected) Assert.True(result.Success, result.Message + "\nServer: " + peer.Error);
            else Assert.False(result.Success, result.Message);
            Assert.Equal(expected ? 1 : 0, store.GetAll().Count);
            if (expected) Assert.True(peer.SawValidProof, result.Message);
            if (scenario == "wrong_tls_pin") Assert.False(peer.SawValidProof);
        }
        finally { IdentityAndStoreTests.DeleteDir(dir); }
    }
    [Fact]
    public async Task ManualPairRequiresExplicitPin()
    {
        var dir = IdentityAndStoreTests.TempDir();
        try { var result = await new PairingClient().PairAsync(new ClientIdentity(dir), new DeviceStore(dir), "127.0.0.1", 47821, "123456"); Assert.False(result.Success); Assert.Contains("指纹", result.Message); }
        finally { IdentityAndStoreTests.DeleteDir(dir); }
    }
    [Fact]
    public async Task BadCodeFormatIsRejectedBeforeNetwork()
    {
        var dir = IdentityAndStoreTests.TempDir();
        try { var result = await new PairingClient().PairAsync(new ClientIdentity(dir), new DeviceStore(dir), "127.0.0.1", 47821, "123"); Assert.False(result.Success); Assert.Contains("6 位", result.Message); }
        finally { IdentityAndStoreTests.DeleteDir(dir); }
    }
}
