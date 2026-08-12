using System.Reflection;
using System.Text;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private static readonly Lazy<string> WebClientHtmlResource = new(
        () => ReadEmbeddedText("TheRadioVault.Web.Assets.web-client.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> ServiceWorkerJavaScriptResource = new(
        () => ReadEmbeddedText("TheRadioVault.Web.Assets.service-worker.js"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> SecureSetupHtmlResource = new(
        () => ReadEmbeddedText("TheRadioVault.Web.Assets.secure-setup.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string WebClientHtml => WebClientHtmlResource.Value;
    private static string ServiceWorkerJavaScript => ServiceWorkerJavaScriptResource.Value;
    private static string SecureSetupHtml => SecureSetupHtmlResource.Value;

    private static string ReadEmbeddedText(string resourceName)
    {
        var assembly = typeof(LocalWebServer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded web asset '{resourceName}' is unavailable in {assembly.GetName().Name}.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
