using QRCoder;

namespace TheRadioVault.Application.Models;

public sealed record PhoneQrCodeCell(string Fill);

public sealed record PhoneQrCodeRow(IReadOnlyList<PhoneQrCodeCell> Cells);

public sealed record PhoneQrCode(string Payload, IReadOnlyList<PhoneQrCodeRow> Rows)
{
    public static PhoneQrCode Empty { get; } = new(string.Empty, Array.Empty<PhoneQrCodeRow>());
    public bool IsAvailable => Rows.Count > 0;

    public static PhoneQrCode Create(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return Empty;

        using var data = QRCodeGenerator.GenerateQrCode(payload.Trim(), QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix
            .Select(row => new PhoneQrCodeRow(row.Cast<bool>()
                .Select(isDark => new PhoneQrCodeCell(isDark ? "#101318" : "#FFFFFF"))
                .ToArray()))
            .ToArray();
        return new PhoneQrCode(payload.Trim(), rows);
    }
}
