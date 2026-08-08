using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IScreenBoundsService
{
    bool IntersectsVirtualScreen(WindowBounds bounds);
}
