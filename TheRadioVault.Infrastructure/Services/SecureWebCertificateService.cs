using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TheRadioVault.Services;

public sealed class SecureWebCertificateBundle : IDisposable
{
    public required X509Certificate2 ServerCertificate { get; init; }
    public required X509Certificate2 RootCertificate { get; init; }
    public required byte[] RootCertificateDer { get; init; }
    public required byte[] MobileConfigurationProfile { get; init; }
    public required string RootThumbprint { get; init; }
    public required string ServerThumbprint { get; init; }
    public required IReadOnlyList<string> SubjectAlternativeNames { get; init; }

    public void Dispose()
    {
        ServerCertificate.Dispose();
        RootCertificate.Dispose();
    }
}

public sealed record SecureCertificateValidationResult(bool IsValid, IReadOnlyList<string> Checks)
{
    public string Summary => string.Join(Environment.NewLine, Checks);
}

/// <summary>
/// Owns the persistent Radio Vault local root CA and renewable LAN server
/// certificates. The root private key never leaves the Radio Vault data folder.
/// </summary>
public static class SecureWebCertificateService
{
    private const string RootPfxFileName = "radio-vault-local-root.pfx";
    private const string RootCerFileName = "radio-vault-local-root.cer";
    private const string ServerPfxFileName = "radio-vault-local-server.pfx";
    private const string ServerMetadataFileName = "radio-vault-local-server.names";

    public static string CertificateDirectory
    {
        get
        {
            var path = Path.Combine(AppPaths.DataDirectory, "WebCertificates");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static SecureWebCertificateBundle EnsureCertificates(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 24)
            throw new InvalidOperationException("The secure web certificate password is invalid.");

        var rootPfxPath = Path.Combine(CertificateDirectory, RootPfxFileName);
        var rootCerPath = Path.Combine(CertificateDirectory, RootCerFileName);
        var serverPfxPath = Path.Combine(CertificateDirectory, ServerPfxFileName);
        var metadataPath = Path.Combine(CertificateDirectory, ServerMetadataFileName);

        using var signingRoot = LoadOrCreateRoot(rootPfxPath, rootCerPath, password);
        var names = GetSubjectAlternativeNames();
        var namesFingerprint = string.Join("\n", names);

        X509Certificate2 server;
        if (File.Exists(serverPfxPath) && File.Exists(metadataPath) &&
            string.Equals(File.ReadAllText(metadataPath), namesFingerprint, StringComparison.Ordinal))
        {
            try
            {
                server = LoadCertificateWithPrivateKey(File.ReadAllBytes(serverPfxPath), password);
                if (server.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30) || !server.HasPrivateKey)
                {
                    server.Dispose();
                    server = CreateAndPersistServer(signingRoot, names, serverPfxPath, metadataPath, password);
                }
            }
            catch
            {
                server = CreateAndPersistServer(signingRoot, names, serverPfxPath, metadataPath, password);
            }
        }
        else
        {
            server = CreateAndPersistServer(signingRoot, names, serverPfxPath, metadataPath, password);
        }

        var rootDer = File.ReadAllBytes(rootCerPath);
        var publicRoot = new X509Certificate2(rootDer);
        var validation = ValidateCertificate(server, publicRoot, names);
        if (!validation.IsValid)
        {
            server.Dispose();
            publicRoot.Dispose();
            throw new InvalidOperationException("Radio Vault could not create a valid HTTPS certificate:\n" + validation.Summary);
        }

        return new SecureWebCertificateBundle
        {
            ServerCertificate = server,
            RootCertificate = publicRoot,
            RootCertificateDer = rootDer,
            MobileConfigurationProfile = BuildMobileConfigurationProfile(rootDer, signingRoot.Thumbprint ?? string.Empty),
            RootThumbprint = signingRoot.Thumbprint ?? string.Empty,
            ServerThumbprint = server.Thumbprint ?? string.Empty,
            SubjectAlternativeNames = names
        };
    }

    public static SecureCertificateValidationResult ValidateCertificate(
        X509Certificate2 server,
        X509Certificate2 root,
        IReadOnlyList<string> requiredNames)
    {
        var checks = new List<string>();
        var valid = true;
        void Check(bool condition, string success, string failure)
        {
            checks.Add((condition ? "✓ " : "✗ ") + (condition ? success : failure));
            valid &= condition;
        }

        Check(server.HasPrivateKey, "Server private key available", "Server private key is missing");
        Check(server.NotBefore.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(5), "Certificate is active", "Certificate is not active yet");
        Check(server.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30), "Certificate expiry is healthy", "Certificate expires too soon");

        var eku = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        Check(eku?.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.1") == true,
            "Server Authentication usage present", "Server Authentication usage is missing");

        var sanText = server.Extensions.Cast<X509Extension>()
            .FirstOrDefault(x => x.Oid?.Value == "2.5.29.17")?.Format(false) ?? string.Empty;
        foreach (var required in requiredNames)
            Check(sanText.Contains(required, StringComparison.OrdinalIgnoreCase),
                $"SAN contains {required}", $"SAN is missing {required}");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        Check(chain.Build(server), "Certificate chain validates to Radio Vault root",
            "Certificate chain validation failed: " + string.Join(", ", chain.ChainStatus.Select(x => x.StatusInformation.Trim())));

        return new SecureCertificateValidationResult(valid, checks);
    }

    public static void ResetCertificates()
    {
        foreach (var file in new[] { RootPfxFileName, RootCerFileName, ServerPfxFileName, ServerMetadataFileName })
        {
            var path = Path.Combine(CertificateDirectory, file);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static X509Certificate2 CreateAndPersistServer(
        X509Certificate2 root,
        IReadOnlyList<string> names,
        string pfxPath,
        string metadataPath,
        string password)
    {
        using var generated = CreateServerCertificate(root, names);
        var pfx = generated.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, pfx);
        File.WriteAllText(metadataPath, string.Join("\n", names));
        return LoadCertificateWithPrivateKey(pfx, password);
    }

    private static X509Certificate2 LoadCertificateWithPrivateKey(byte[] pfx, string password)
        => new(pfx, password,
            X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.UserKeySet);

    private static X509Certificate2 LoadOrCreateRoot(string pfxPath, string cerPath, string password)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var root = LoadCertificateWithPrivateKey(File.ReadAllBytes(pfxPath), password);
                if (root.HasPrivateKey && root.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddYears(1))
                {
                    if (!File.Exists(cerPath)) File.WriteAllBytes(cerPath, root.Export(X509ContentType.Cert));
                    return root;
                }
                root.Dispose();
            }
            catch { }
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=Radio Vault Local Root CA, O=Radio Vault",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = created.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, pfx);
        File.WriteAllBytes(cerPath, created.Export(X509ContentType.Cert));
        return LoadCertificateWithPrivateKey(pfx, password);
    }

    private static X509Certificate2 CreateServerCertificate(X509Certificate2 root, IReadOnlyList<string> names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Radio Vault Web",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1", "Server Authentication") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(BuildAuthorityKeyIdentifier(root));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var value in names)
        {
            if (IPAddress.TryParse(value, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(value);
        }
        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(365), serial);
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Extension BuildAuthorityKeyIdentifier(X509Certificate2 root)
    {
        var ski = root.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        if (string.IsNullOrWhiteSpace(ski))
            throw new InvalidOperationException("The Radio Vault root certificate has no subject key identifier.");

        var keyIdentifier = Convert.FromHexString(ski);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteOctetString(keyIdentifier, new Asn1Tag(TagClass.ContextSpecific, 0));
        writer.PopSequence();
        return new X509Extension("2.5.29.35", writer.Encode(), false);
    }

    private static IReadOnlyList<string> GetSubjectAlternativeNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "radiovault.local",
            Environment.MachineName,
            Environment.MachineName + ".local",
            IPAddress.Loopback.ToString()
        };

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 169 && bytes[1] == 254))
                    values.Add(address.ToString());
            }
        }

        return values.OrderBy(x => IPAddress.TryParse(x, out _) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static byte[] BuildMobileConfigurationProfile(byte[] rootCertificateDer, string thumbprint)
    {
        var profileUuid = StableUuid("profile:" + thumbprint);
        var certificateUuid = StableUuid("certificate:" + thumbprint);
        var base64 = Convert.ToBase64String(rootCertificateDer, Base64FormattingOptions.InsertLineBreaks);
        const string profileTemplate = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>PayloadContent</key><array><dict>
<key>PayloadCertificateFileName</key><string>RadioVaultLocalRootCA.cer</string>
<key>PayloadContent</key><data>__CERTIFICATE_BASE64__</data>
<key>PayloadDescription</key><string>Trusts the private Radio Vault HTTPS server on your home network.</string>
<key>PayloadDisplayName</key><string>Radio Vault Local Root CA</string>
<key>PayloadIdentifier</key><string>com.radiovault.local.root</string>
<key>PayloadType</key><string>com.apple.security.root</string>
<key>PayloadUUID</key><string>__CERTIFICATE_UUID__</string><key>PayloadVersion</key><integer>1</integer>
</dict></array>
<key>PayloadDescription</key><string>Enables secure local access and cold offline launch for Radio Vault Web.</string>
<key>PayloadDisplayName</key><string>Radio Vault Secure Offline Access</string>
<key>PayloadIdentifier</key><string>com.radiovault.local.configuration</string>
<key>PayloadOrganization</key><string>Radio Vault</string><key>PayloadRemovalDisallowed</key><false/>
<key>PayloadType</key><string>Configuration</string><key>PayloadUUID</key><string>__PROFILE_UUID__</string>
<key>PayloadVersion</key><integer>1</integer></dict></plist>
""";
        var xml = profileTemplate
            .Replace("__CERTIFICATE_BASE64__", base64, StringComparison.Ordinal)
            .Replace("__CERTIFICATE_UUID__", certificateUuid, StringComparison.Ordinal)
            .Replace("__PROFILE_UUID__", profileUuid, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(xml);
    }

    private static string StableUuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString().ToUpperInvariant();
    }
}
