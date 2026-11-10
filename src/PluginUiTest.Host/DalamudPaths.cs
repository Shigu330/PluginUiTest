using System.Text.Json;

namespace PluginUiTest.Host;

/// <summary>
/// Where a tool that draws a plugin without the game finds what it does not ship (R05): Dalamud's assemblies and cimgui.dll in the
/// XIVLauncher install (DALAMUD_HOME if set, as Dalamud.NET.Sdk's build, else %APPDATA%\XIVLauncher\addon\Hooks\dev), Dalamud's fonts in its
/// assets, and the game data in the user's sqpack (a folder given, else the game folder of XIVLauncher's launcherConfigV3.json, else Steam's
/// default folder). Pure: the environment is passed in (moved from src/TenshoAssistant.Desktop/DesktopPaths.cs in X-120).
/// </summary>
public static class DalamudPaths
{
    public const string SteamSqpack=@"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack";

    public static string XivLauncher(string appData)=>Path.Combine(appData,"XIVLauncher");

    public static string Hooks(string? dalamudHome,string appData)
        => !string.IsNullOrWhiteSpace(dalamudHome) ? dalamudHome : Path.Combine(XivLauncher(appData),"addon","Hooks","dev");

    /// <summary>Dalamud's interface assets (Noto Sans CJK JP Medium, Font Awesome Free Solid): fonts of the headless context when present.</summary>
    public static string Fonts(string appData)=>Path.Combine(XivLauncher(appData),"dalamudAssets","dev","UIRes");

    /// <summary>The plugins other authors published, as Dalamud installed them: never loaded by the test tool.</summary>
    public static string InstalledPlugins(string appData)=>Path.Combine(XivLauncher(appData),"installedPlugins");

    /// <summary>The sqpack folders to try, in order.</summary>
    public static IReadOnlyList<string> SqpackCandidates(string? option,string? launcherConfigJson)
    {
        var list=new List<string>();
        if(!string.IsNullOrWhiteSpace(option))list.Add(option);
        if(GamePath(launcherConfigJson) is { } game)list.Add(Path.Combine(game,"game","sqpack"));
        list.Add(SteamSqpack);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>GamePath of launcherConfigV3.json (the folder holding game\ and boot\), or null.</summary>
    public static string? GamePath(string? launcherConfigJson)
    {
        if(string.IsNullOrWhiteSpace(launcherConfigJson))return null;
        try
        {
            using var document=JsonDocument.Parse(launcherConfigJson,new JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip});
            return document.RootElement.ValueKind==JsonValueKind.Object && document.RootElement.TryGetProperty("GamePath",out var path)
                   && path.ValueKind==JsonValueKind.String && path.GetString() is { Length:>0 } text ? text : null;
        }
        catch(JsonException){return null;}
    }
}
