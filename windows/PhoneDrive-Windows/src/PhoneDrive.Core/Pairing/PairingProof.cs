using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhoneDrive.Core.Pairing;

public static class PairingProof
{
    public static RSA ParseRsa(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-rsa") throw new FormatException("不支持的公钥格式");
        var blob = Convert.FromBase64String(parts[1]);
        if (blob.Length is < 256 or > 2048) throw new FormatException("公钥长度非法");
        int position = 0;
        byte[] Read()
        {
            if (position + 4 > blob.Length) throw new FormatException();
            var size = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(position, 4)); position += 4;
            if (size <= 0 || size > 2048 || position + size > blob.Length) throw new FormatException();
            var value = blob.AsSpan(position, size).ToArray(); position += size;
            if (value.Length > 1 && value[0] == 0) value = value[1..];
            return value;
        }
        if (Encoding.ASCII.GetString(Read()) != "ssh-rsa") throw new FormatException();
        var exponent = Read(); var modulus = Read();
        if (position != blob.Length || modulus.Length < 256) throw new FormatException();
        var rsa = RSA.Create();
        try { rsa.ImportParameters(new RSAParameters { Exponent = exponent, Modulus = modulus }); return rsa; }
        catch { rsa.Dispose(); throw; }
    }
    public static byte[] Transcript(PairingRequest request) => Encoding.UTF8.GetBytes(string.Join("\n", new[] {
        "PhoneDrive-Pair-v2", request.Nonce, request.DeviceId, request.Name, request.Type,
        request.HostFingerprint, request.HostPublicKey, request.ClientPublicKey,
    }));
    public static bool ValidFingerprint(string? fp)
    {
        try { return fp?.StartsWith("SHA256:", StringComparison.Ordinal) == true && Convert.FromBase64String(fp[7..] + "=").Length == 32; }
        catch { return false; }
    }
}
