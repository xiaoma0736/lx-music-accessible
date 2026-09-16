using System.Diagnostics;

namespace LXMusicAccessible.Services;

/// <summary>Boundary for starting browsers, Explorer, and configured external players.</summary>
public interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class SystemProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        FrameworkCompatibility.ThrowIfNull(startInfo);
        Process.Start(startInfo);
    }
}
