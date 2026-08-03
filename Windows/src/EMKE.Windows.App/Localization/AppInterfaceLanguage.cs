namespace EMKE.Windows.App.Localization;

internal enum AppInterfaceLanguage
{
    System,
    ZhHans,
    English,
}

internal static class AppInterfaceLanguageExtensions
{
    public static string ToStableValue(this AppInterfaceLanguage language)
    {
        return language switch
        {
            AppInterfaceLanguage.System => "system",
            AppInterfaceLanguage.ZhHans => "zhHans",
            AppInterfaceLanguage.English => "english",
            _ => throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "Undefined interface language."),
        };
    }

    public static AppInterfaceLanguage ParseStableValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value switch
        {
            "system" => AppInterfaceLanguage.System,
            "zhHans" => AppInterfaceLanguage.ZhHans,
            "english" => AppInterfaceLanguage.English,
            _ => throw new FormatException(
                $"Unsupported interface language value '{value}'."),
        };
    }
}
