using System.IO;
using System.Text;
using LXMusicAccessible.Core.MusicApi;

namespace LXMusicAccessible.App;

public static class LoginDiagnosticReportStore
{
    public static string? Write(
        string directory,
        string platform,
        string credentials,
        LoginDiagnosticOutcome outcome,
        LoginDiagnosticError error,
        string correlationId)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, LoginDiagnostics.ReportFileName);
            File.WriteAllText(path,
                LoginDiagnostics.CreateReport(platform, credentials, outcome, error, correlationId),
                Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static bool Delete(string reportPath)
    {
        try
        {
            File.Delete(reportPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
