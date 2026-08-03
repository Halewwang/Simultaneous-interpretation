using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using EMKE.Platform.Driver;
using EMKE.Platform.Security;

namespace EMKE.Setup;

internal sealed class WindowsSetupSignatureProbe : ISetupSignatureProbe
{
    private const int MaximumCertificateBytes = 1024 * 1024;

    public static WindowsSetupSignatureProbe Instance { get; } = new();

    private WindowsSetupSignatureProbe()
    {
    }

#pragma warning disable CA1031 // Malformed signed containers and certificates fail closed.
    public SetupMsixSignatureEvidence VerifyMsix(VerifiedSetupPayload msix)
    {
        ArgumentNullException.ThrowIfNull(msix);
        try
        {
            WindowsHandleTrustEvidence trust = msix.Lease.UseHandle(
                handle => WindowsHandleAuthenticodeTrust.Verify(
                    handle,
                    msix.DisplayPath,
                    WindowsHandleAuthenticodeTrust.GenericVerifyV2));
            if (trust.Status is not (
                    WindowsHandleTrustStatus.Trusted
                    or WindowsHandleTrustStatus.ChainOnly)
                || trust.SignerCertificate is null)
            {
                return new SetupMsixSignatureEvidence(false, null, null);
            }

            string signerSha256 = Convert.ToHexStringLower(
                SHA256.HashData(trust.SignerCertificate));
            using Stream msixView = msix.Lease.OpenReadView();
            string publisher = ReadMsixPublisher(msixView);
            return new SetupMsixSignatureEvidence(
                signatureValid: true,
                signerSha256,
                publisher);
        }
        catch (Exception)
        {
            return new SetupMsixSignatureEvidence(false, null, null);
        }
    }

    public SetupCertificateEvidence ReadCertificate(
        VerifiedSetupPayload certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        try
        {
            if (certificate.Length is <= 0 or > MaximumCertificateBytes)
            {
                throw new InvalidDataException(
                    "Certificate payload has an invalid length.");
            }

            byte[] certificateBytes = new byte[checked((int)certificate.Length)];
            using (Stream certificateView = certificate.Lease.OpenReadView())
            {
                certificateView.ReadExactly(certificateBytes);
                if (certificateView.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        "Certificate payload exceeds its verified length.");
                }
            }

            using X509Certificate2 parsedCertificate = X509CertificateLoader
                .LoadCertificate(certificateBytes);
            DateTime now = DateTime.UtcNow;
            return new SetupCertificateEvidence(
                parsedCertificate.Subject,
                parsedCertificate.NotBefore.ToUniversalTime() <= now
                    && parsedCertificate.NotAfter.ToUniversalTime() >= now,
                Convert.ToHexStringLower(
                    parsedCertificate.GetCertHash(HashAlgorithmName.SHA256)));
        }
        catch (Exception)
        {
            return new SetupCertificateEvidence(null, false, null);
        }
    }

    public SetupDriverCatalogEvidence VerifyDriverCatalog(
        VerifiedSetupPayload catalog,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inf);
        ArgumentNullException.ThrowIfNull(sys);
        WindowsCatalogEvidence evidence = WindowsCatalogTrustVerifier.Instance.Verify(
            catalog.DisplayPath,
            inf.DisplayPath,
            sys.DisplayPath);
        return new SetupDriverCatalogEvidence(evidence.ChainValid);
    }
#pragma warning restore CA1031

    internal static string ReadMsixPublisher(Stream msixStream)
    {
        ArgumentNullException.ThrowIfNull(msixStream);
        using ZipArchive archive = new(
            msixStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        ZipArchiveEntry entry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        using Stream manifestStream = entry.Open();
        XDocument document = XDocument.Load(manifestStream, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException("MSIX identity manifest is missing.");
        XElement? identity = root.Element(root.Name.Namespace + "Identity");
        return identity?.Attribute("Publisher")?.Value
            ?? throw new InvalidDataException("MSIX publisher is missing.");
    }
}
