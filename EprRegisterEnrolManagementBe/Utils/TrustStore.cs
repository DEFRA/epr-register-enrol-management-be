using System.Collections;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics.CodeAnalysis;

namespace EprRegisterEnrolManagementBe.Utils;

[ExcludeFromCodeCoverage]
public static class TrustStore
{
    public static void LoadCustomTrustStoreFromEnvironment(this IServiceCollection _)
    {
        var certificates = GetCertificates();
        AddCertificates(certificates);
    }

    private static List<byte[]> GetCertificates()
    {
        return ReadTrustStoreEntries(Environment.GetEnvironmentVariables());
    }

    /// <summary>
    /// Test seam (epr-kf1). Pure function: takes a snapshot of
    /// environment variables, returns the decoded certificate byte
    /// arrays for every entry whose key starts with
    /// <c>TRUSTSTORE_</c> and whose value is valid base64. Pulled out
    /// of <see cref="GetCertificates"/> so the env-var filter can be
    /// covered without touching the live machine certificate store.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Covered by TrustStoreTests; the [ExcludeFromCodeCoverage] on the surrounding class is the historical baseline.")]
    internal static List<byte[]> ReadTrustStoreEntries(IDictionary envVars)
    {
        return envVars.Cast<DictionaryEntry>()
            .Where(entry =>
                entry.Key.ToString()!.StartsWith("TRUSTSTORE_", StringComparison.Ordinal)
                && IsBase64String(entry.Value?.ToString() ?? ""))
            .Select(entry => Convert.FromBase64String(entry.Value?.ToString() ?? "")).ToList();
    }

    private static void AddCertificates(List<byte[]> certificates)
    {
        if (certificates.Count == 0) return; // to stop trust store access denied issues on Macs

        // epr-kf1: X509Certificate2 owns an unmanaged certificate
        // handle and implements IDisposable. The previous
        // implementation projected the byte arrays through LINQ into
        // X509Certificate2 instances and never disposed the
        // originals — leaking a managed/unmanaged handle per cert
        // until the GC ran finalizers. Materialise the loaded
        // certificates into a list owned here, install them into the
        // store, and dispose the in-memory copies in a finally block
        // once X509Store.AddRange has imported them.
        var loaded = new List<X509Certificate2>(certificates.Count);
        try
        {
            foreach (var bytes in certificates)
            {
                loaded.Add(X509CertificateLoader.LoadCertificate(bytes));
            }

            var certificateCollection = new X509Certificate2Collection();
            foreach (var certificate2 in loaded)
            {
                certificateCollection.Add(certificate2);
            }

            var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.AddRange(certificateCollection);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Root certificate import failed: " + ex.Message, ex);
            }
            finally
            {
                store.Close();
            }
        }
        finally
        {
            foreach (var certificate2 in loaded)
            {
                certificate2.Dispose();
            }
        }
    }

    private static bool IsBase64String(string str)
    {
        var buffer = new Span<byte>(new byte[str.Length]);
        return Convert.TryFromBase64String(str, buffer, out _);
    }
}