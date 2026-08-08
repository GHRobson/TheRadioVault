namespace TheRadioVault.Application.Models;

public sealed record WindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}
