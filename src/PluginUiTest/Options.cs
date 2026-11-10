namespace PluginUiTest;

/// <summary>
/// The command line of PluginUiTest and the refusals that keep it to the creator's own build (pure: no Dalamud type, linked in tests/PluginUiTest.Tests; was linked in Tensho's
/// tests). Usage: PluginUiTest &lt;path\to\YourPlugin.dll&gt; [--dalamud=&lt;folder&gt;] [--sqpack=&lt;folder&gt;] [--no-game-data] [--frames=N]
/// [--timeout=S] [--out=report.md] [--json=report.json] [--run-anyway=WindowName,...]. --child and --exclude= are internal (the drawing
/// child process).
/// </summary>
public sealed record Options
{
    public const int ExitDrawn=0,ExitFailed=1,ExitUsage=2,ExitNotCreated=3;
    public const string Banner="Test tool, not affiliated with the Dalamud team, does not replace Dalamud.";
    public const string DataNote="Game data and Dalamud are loaded from your install or the folder you give; nothing is redistributed.";
    /// <summary>The --run-anyway name of the plugin's constructor.</summary>
    public const string Constructor="constructor";

    public const string Usage="""
        Usage: PluginUiTest <path\to\YourPlugin.dll> [options]
          --dalamud=<folder>      Dalamud's assemblies (default: DALAMUD_HOME, else %APPDATA%\XIVLauncher\addon\Hooks\dev)
          --sqpack=<folder>       the game's sqpack folder (default: XIVLauncher's game path, else Steam's; none: sheets answer absent)
          --no-game-data          do not look for the game data (as in CI)
          --frames=N              frames drawn per window (default 20)
          --timeout=S             seconds a window may take before it is reported blocked (default 30)
          --out=report.md         write the markdown report
          --json=report.json      write the JSON report
          --run-anyway=A,B        draw these windows (or "constructor", or "*") even if they can reach game memory: a window's
                                  title, its ###id, or its full name; repeat the option or separate with commas (\, for a comma)
        Exit codes: 0 all drawn, 1 something threw, blocked or crashed, 2 usage or refusal, 3 the plugin could not be created.
        """;

    public string Plugin { get; init; }="";
    public string? Dalamud { get; init; }
    public string? Sqpack { get; init; }
    public bool NoGameData { get; init; }
    public int Frames { get; init; }=20;
    public int Timeout { get; init; }=30;
    public string? Out { get; init; }
    public string? Json { get; init; }
    public IReadOnlyList<string> RunAnyway { get; init; }=[];
    public bool Child { get; init; }
    public string? Exclude { get; init; }

    /// <summary>The options, or the reason they are refused (exit 2).</summary>
    public static (Options? Options,string? Error) Parse(IReadOnlyList<string> args)
    {
        var options=new Options();
        var plugin=new List<string>();
        foreach(var arg in args)
        {
            static string Value(string arg)=>arg[(arg.IndexOf('=')+1)..];
            if(!arg.StartsWith("--",StringComparison.Ordinal)){plugin.Add(arg);continue;}
            var name=arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            switch(name)
            {
                case "--dalamud":options=options with{Dalamud=Value(arg)};break;
                case "--sqpack":options=options with{Sqpack=Value(arg)};break;
                case "--no-game-data":options=options with{NoGameData=true};break;
                case "--out":options=options with{Out=Value(arg)};break;
                case "--json":options=options with{Json=Value(arg)};break;
                case "--child":options=options with{Child=true};break;
                case "--exclude":options=options with{Exclude=Value(arg)};break;
                case "--run-anyway":options=options with{RunAnyway=[..options.RunAnyway,..SplitNames(Value(arg))]};break;
                case "--frames":
                    if(!int.TryParse(Value(arg),out var frames) || frames is <1 or >10_000)return (null,"--frames=N: a number from 1 to 10000");
                    options=options with{Frames=frames};break;
                case "--timeout":
                    if(!int.TryParse(Value(arg),out var timeout) || timeout is <1 or >3600)return (null,"--timeout=S: a number of seconds from 1 to 3600");
                    options=options with{Timeout=timeout};break;
                case "--help" or "-h":return (null,"");
                default:return (null,$"unknown option {name}");
            }
        }
        if(plugin.Count!=1)return (null,plugin.Count==0 ? "no plugin DLL given" : "one plugin DLL only: "+string.Join(" ",plugin));
        return (options with{Plugin=plugin[0]},null);
    }

    /// <summary>
    /// Why this path is not run, or null. Only one DLL, named by the user: never a folder, never a plugin installed by Dalamud
    /// (installedPlugins: other authors' plugins are never loaded by this tool).
    /// </summary>
    public static string? Refusal(string fullPath,bool fileExists,bool directoryExists,string installedPlugins)
    {
        if(directoryExists)return $"{fullPath} is a folder: pass your plugin's DLL itself (PluginUiTest loads only the DLL you name, nothing else from a folder).";
        if(UnderInstalledPlugins(fullPath,installedPlugins))
            return $"{fullPath} is a plugin installed by Dalamud (installedPlugins). PluginUiTest never loads installed plugins (other authors' code): point it at your own build output, e.g. bin\\Release\\YourPlugin.dll.";
        if(!fileExists)return $"{fullPath}: no such file.";
        if(!fullPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))return $"{fullPath} is not a .dll: pass your plugin's DLL.";
        return null;
    }

    public static bool UnderInstalledPlugins(string fullPath,string installedPlugins)
    {
        static string Folder(string path)=>path.Replace('/','\\').TrimEnd('\\')+"\\";
        return Folder(fullPath).StartsWith(Folder(installedPlugins),StringComparison.OrdinalIgnoreCase)
               || fullPath.Replace('/','\\').Contains("\\XIVLauncher\\installedPlugins\\",StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The names of one --run-anyway value: split on commas (a backslash before a comma keeps it in the name), trimmed.</summary>
    public static IReadOnlyList<string> SplitNames(string value)
    {
        var names=new List<string>();
        var current=new System.Text.StringBuilder();
        for(var i=0;i<value.Length;i++)
        {
            if(value[i]=='\\' && i+1<value.Length && value[i+1]==','){current.Append(',');i++;continue;}
            if(value[i]==','){names.Add(current.ToString().Trim());current.Clear();continue;}
            current.Append(value[i]);
        }
        names.Add(current.ToString().Trim());
        return names.Where(n=>n.Length>0).ToArray();
    }

    /// <summary>True when a name given to --run-anyway designates this entry: its key, a window's full name, its title (without the ImGui id),
    /// its ###id (with or without the ###), its type, or "*".</summary>
    public static bool Matches(IReadOnlyList<string> runAnyway,params string?[] names)
        => runAnyway.Any(given=>given=="*" || names.Any(name=>name is { Length:>0 } && Designates(given,name)));

    private static bool Designates(string given,string name)
    {
        static bool Same(string a,string b)=>a.Length>0 && string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        if(Same(given,name) || Same(given,WithoutId(name)))return true;
        return Id(name) is { } id && (Same(given,id) || Same(given,"###"+id));
    }

    /// <summary>An ImGui window name without its id part ("Main###id" → "Main", "Main##id" → "Main").</summary>
    public static string WithoutId(string name)=>name.IndexOf("##",StringComparison.Ordinal) is var at and >=0 ? name[..at] : name;

    /// <summary>The ###id of an ImGui window name ("Main###main" → "main"), or null.</summary>
    public static string? Id(string name)=>name.IndexOf("###",StringComparison.Ordinal) is var at and >=0 && at+3<name.Length ? name[(at+3)..] : null;

    /// <summary>What to give --run-anyway for this window, quoted for a command line: its title when no other window has it, else its ###id,
    /// else its full name; a comma in it written \,.</summary>
    public static string RunAnywayName(string name,IEnumerable<string> others)
    {
        var title=WithoutId(name);
        var shared=others.Count(other=>string.Equals(WithoutId(other),title,StringComparison.OrdinalIgnoreCase))>1;
        var chosen=title.Length>0 && !shared ? title : Id(name) is { } id ? "###"+id : name;
        return "\""+chosen.Replace(",","\\,")+"\"";
    }
}
