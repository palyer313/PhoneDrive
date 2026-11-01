using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;

namespace PhoneDrive.Core.Tests;

public sealed class PairingInvitationTests
{
    [Theory]
    [InlineData("success", 200, 1)]
    [InlineData("deny", 403, 1)]
    [InlineData("replace_client_key", 400, 0)]
    [InlineData("wrong_code", 401, 0)]
    [InlineData("wrong_nonce", 400, 0)]
    public async Task PhoneScansPcInvitation_RequiresFreshProofAndPcApproval(string scenario, int status, int approvals)
    {
        var dir = IdentityAndStoreTests.TempDir(); int prompted = 0;
        try
        {
            var store = new DeviceStore(dir);
            using var server = new PairingInvitationServer(new ClientIdentity(dir), store, "127.0.0.1", (request, ct) => {
                prompted++; Assert.Equal("测试手机", request.Name); return Task.FromResult(scenario != "deny");
            });
            var invitation = server.Start();
            using var handler = new HttpClientHandler { UseProxy = false };
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => {
                using var rsa = cert?.GetRSAPublicKey();
                return rsa != null && ClientIdentity.FingerprintSha256(rsa) == invitation.Fingerprint;
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{invitation.Port}"), Timeout = TimeSpan.FromSeconds(10) };
            var info = await http.GetFromJsonAsync<PairingInfo>("/info");
            Assert.Equal("windows", info!.Type); Assert.Equal(0, info.SftpPort);
            Assert.Equal(invitation.Fingerprint, info.Fingerprint);
            using var hostKey = RSA.Create(2048); using var clientKey = RSA.Create(2048); using var other = RSA.Create(2048);
            var request = new PairingRequest {
                V = 2, Nonce = info.Nonce, Code = invitation.Code, DeviceId = "android-fixture", Name = "测试手机", Type = "phone",
                HostFingerprint = ClientIdentity.FingerprintSha256(hostKey), HostPublicKey = ClientIdentity.ExportOpenSshPublicKey(hostKey), ClientPublicKey = ClientIdentity.ExportOpenSshPublicKey(clientKey),
            };
            var transcript = PairingProof.Transcript(request);
            request.HostProof = Convert.ToBase64String(hostKey.SignData(transcript,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
            request.ClientProof = Convert.ToBase64String(clientKey.SignData(transcript,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
            if (scenario == "replace_client_key") request.ClientPublicKey = ClientIdentity.ExportOpenSshPublicKey(other);
            if (scenario == "wrong_code") request.Code = invitation.Code == "000000" ? "111111" : "000000";
            if (scenario == "wrong_nonce") request.Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            using var result = await http.PostAsync("/pair",new StringContent(json,Encoding.UTF8,"application/json"));
            Assert.Equal(status,(int)result.StatusCode); Assert.Equal(approvals,prompted);
            if (status == 200)
            {
                var ack = await result.Content.ReadFromJsonAsync<PairingSuccess>();
                Assert.Equal(invitation.Fingerprint,ack!.HostFingerprint); Assert.Equal(info.Nonce,ack.Nonce);
                var device = await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal("127.0.0.1", device.LastKnownIp); Assert.Equal(2222,device.LastKnownPort);
                Assert.Single(store.GetAll()); Assert.Equal(request.HostFingerprint,store.GetAll()[0].HostKeyFingerprint);
            }
            else Assert.Empty(store.GetAll());
        }
        finally { IdentityAndStoreTests.DeleteDir(dir); }
    }

    [Fact]
    public async Task ClosingPcInvitationStopsListeningAndCancelsPendingCompletion()
    {
        var dir = IdentityAndStoreTests.TempDir();
        try
        {
            var server = new PairingInvitationServer(new ClientIdentity(dir),new DeviceStore(dir),"127.0.0.1",(_,_)=>Task.FromResult(true));
            var invite = server.Start(); server.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.Completion);
            using var socket = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(()=>socket.ConnectAsync("127.0.0.1",invite.Port));
        }
        finally { IdentityAndStoreTests.DeleteDir(dir); }
    }
}
