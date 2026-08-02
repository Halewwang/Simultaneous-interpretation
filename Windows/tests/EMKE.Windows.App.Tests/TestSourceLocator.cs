namespace EMKE.Windows.App.Tests;

internal static class TestSourceLocator
{
    public static string FindWindowsSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 12 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Windows",
                "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Unable to locate Windows/src.");
    }

    public static string Find(string appRelativePath)
    {
        string candidate = Path.Combine(
            FindWindowsSourceRoot(),
            "EMKE.Windows.App",
            appRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"Unable to locate Windows/src/EMKE.Windows.App/{appRelativePath}.");
    }
}
