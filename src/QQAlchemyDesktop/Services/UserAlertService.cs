using System.Media;

namespace QQAlchemyDesktop.Services;

public sealed class UserAlertService
{
    public event Action<string, string>? AlertRaised;

    public void Raise(string title, string message)
    {
        try { SystemSounds.Exclamation.Play(); } catch { /* best effort */ }
        AlertRaised?.Invoke(title, message);
    }
}

