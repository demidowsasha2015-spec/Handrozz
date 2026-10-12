using UniMouse.Core.Model;

namespace UniMouse.Core.Runtime;

/// <summary>Abstracts the physical input layer so Core stays platform-neutral and testable.</summary>
public interface IInputExecutor
{
    Task PressKeyAsync(int keyCode, CancellationToken ct);
    Task ReleaseKeyAsync(int keyCode, CancellationToken ct);
    Task TypeTextAsync(string text, CancellationToken ct);
    Task MoveMouseAsync(int x, int y, CancellationToken ct);
    Task ClickMouseAsync(MouseButton button, int clicks, CancellationToken ct);
    Task ScrollMouseAsync(ScrollDirection direction, int amount, CancellationToken ct);

    /// <summary>Safety net: release everything we might have left pressed.</summary>
    Task ReleaseAllAsync();
}
