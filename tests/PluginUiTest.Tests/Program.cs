using System.Text.Json;
using System.Text.RegularExpressions;
using PluginUiTest;
using PluginUiTest.Host;

// The pure parts of PluginUiTest (first written as Tensho Assistant's PluginUiTest1032Tests). The whole tool is checked by the CI, which
// runs it on samples/SamplePlugin and checks the three outcomes and the JSON report against docs/report.schema.json.
static void Check(bool value,string message) { if (!value) throw new Exception(message); }
static string Root()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName,"docs","report.schema.json"))) return dir.FullName;
    throw new Exception("repository root not found");
}
static string Read(string file) => File.ReadAllText(Path.Combine(Root(),file)).Replace("\r\n","\n");

var cases = new (string Name,Action Run)[]
{
    ("one DLL named by the user, never a folder nor an installed plugin; options parsed, bad ones refused (exit 2)",() =>
    {
        var installed = DalamudPaths.InstalledPlugins(@"C:\Users\X\AppData\Roaming");
        Check(installed == Path.Combine(@"C:\Users\X\AppData\Roaming","XIVLauncher","installedPlugins"),"installedPlugins under XIVLauncher");
        Check(Options.Refusal(@"D:\dev\bin",false,true,installed)!.Contains("is a folder",StringComparison.Ordinal),"a folder is refused");
        Check(Options.Refusal(Path.Combine(installed,"Other","1.0.0.0","Other.dll"),true,false,installed)!.Contains("never loads installed plugins",StringComparison.Ordinal),"an installed plugin is refused");
        Check(Options.UnderInstalledPlugins(@"E:\Games\XIVLauncher\installedPlugins\Other\1.0\Other.dll",installed),"another XIVLauncher's installedPlugins too");
        Check(Options.Refusal(@"D:\dev\bin\Mine.dll",false,false,installed)!.Contains("no such file",StringComparison.Ordinal),"a missing file is refused");
        Check(Options.Refusal(@"D:\dev\bin\Mine.exe",true,false,installed)!.Contains("not a .dll",StringComparison.Ordinal),"not a DLL is refused");
        Check(Options.Refusal(@"D:\dev\bin\Mine.dll",true,false,installed) is null,"the creator's own build is run");
        var (options,error) = Options.Parse(["Mine.dll","--frames=5","--timeout=9","--run-anyway=Main window, constructor","--no-game-data","--out=r.md","--json=r.json"]);
        Check(error is null && options!.Plugin == "Mine.dll" && options.Frames == 5 && options.Timeout == 9 && options.NoGameData && options.Out == "r.md" && options.Json == "r.json","options read");
        Check(options!.RunAnyway.SequenceEqual(["Main window","constructor"]),"run-anyway names trimmed");
        Check(Options.Parse(["--frames=0","Mine.dll"]).Error is { Length:>0 } && Options.Parse(["Mine.dll","--bogus"]).Error is { Length:>0 } && Options.Parse([]).Error is { Length:>0 }
              && Options.Parse(["A.dll","B.dll"]).Error is { Length:>0 },"bad options refused");
        Check(Options.Matches(["Main window"],"window:Main window###MainId","Main window###MainId") && Options.Matches(["*"],"x") && !Options.Matches(["Other"],"Main window"),"run-anyway names a window with or without its ImGui id");
        Check(Options.WithoutId("Tracker###Id") == "Tracker" && Options.WithoutId("###Id") == "" && Options.WithoutId("Plain") == "Plain","ImGui id removed");
        Check(Options.ExitDrawn == 0 && Options.ExitFailed == 1 && Options.ExitUsage == 2 && Options.ExitNotCreated == 3,"exit codes 0, 1, 2, 3");
    }),
    ("the report carries the banner first, and its exit code says threw, blocked or crashed (1), not created (3)",() =>
    {
        var report = new RunReport{Plugin=@"D:\dev\Mine.dll"};
        report.Put(new Entry{Key="constructor",Kind="constructor",Name="Plugin constructor",Outcome=Outcome.Drawn});
        report.Put(new Entry{Key="window:A",Kind="window",Name="A",Outcome=Outcome.Drawn,Frames=20,Vertices=100});
        report.Put(new Entry{Key="window:B",Kind="window",Name="B",Outcome=Outcome.Skipped,Where="B.Draw → FFXIVClientStructs: Framework.Instance"});
        report.Put(new Entry{Key="window:C",Kind="window",Name="C",Outcome=Outcome.Hidden});
        Check(report.ExitCode == 0 && report.Summary.StartsWith("PASS",StringComparison.Ordinal),"drawn, hidden and skipped: exit 0");
        var markdown = report.Markdown();
        var lines = markdown.Split('\n');
        Check(lines[0].StartsWith("# Plugin UI test report",StringComparison.Ordinal) && lines[2].Contains(Options.Banner,StringComparison.Ordinal),"title then the banner");
        Check(Options.Banner == "Test tool, not affiliated with the Dalamud team, does not replace Dalamud.","the banner's words");
        Check(markdown.Contains("| Plugin constructor | constructor | created |",StringComparison.Ordinal),"a constructor reads created");
        report.Put(new Entry{Key="window:B",Kind="window",Name="B",Outcome=Outcome.Crashed,Detail="access violation"});
        Check(report.ExitCode == 1 && report.Entries.Count == 4,"a part reported again replaces its line; crashed: exit 1");
        report.Fatal = "constructor threw";report.FatalCode = Options.ExitNotCreated;
        Check(report.ExitCode == 3 && report.Summary.StartsWith("NOT CREATED",StringComparison.Ordinal),"not created: exit 3");
    }),
    ("the JSON report has the shape of docs/report.schema.json (schema version 1)",() =>
    {
        var report = new RunReport{Plugin=@"D:\dev\Mine.dll"};
        report.Facts.Add("Frames per window: 20");
        report.Put(new Entry{Key="constructor",Kind="constructor",Name="Plugin constructor",Outcome=Outcome.Drawn});
        report.Put(new Entry{Key="window:B",Kind="window",Name="B",Outcome=Outcome.Crashed,Detail="access violation",Network=["blocked: GET https://example.com/"]});
        using var json = JsonDocument.Parse(report.Json());
        using var schema = JsonDocument.Parse(Read("docs/report.schema.json"));
        var root = json.RootElement;
        var schemaRoot = schema.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == RunReport.SchemaVersion
              && RunReport.SchemaVersion == schemaRoot.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32(),"schema version");
        Check(root.GetProperty("$schema").GetString() == RunReport.SchemaUrl && RunReport.SchemaUrl == schemaRoot.GetProperty("$id").GetString(),"schema URL");
        var top = schemaRoot.GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToHashSet();
        Check(root.EnumerateObject().All(p=>top.Contains(p.Name)),"no top-level field outside the schema");
        Check(schemaRoot.GetProperty("required").EnumerateArray().All(r=>root.TryGetProperty(r.GetString()!,out _)),"every required top-level field");
        var entryProps = schemaRoot.GetProperty("$defs").GetProperty("entry").GetProperty("properties");
        var outcomes = entryProps.GetProperty("outcome").GetProperty("enum").EnumerateArray().Select(e=>e.GetString()).ToList();
        Check(outcomes.SequenceEqual(Enum.GetNames<Outcome>().Select(n=>n.ToLowerInvariant())),"every outcome in the schema, in order");
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            Check(entry.EnumerateObject().Select(p=>p.Name).ToHashSet().SetEquals(entryProps.EnumerateObject().Select(p=>p.Name)),"entry fields as the schema says");
            Check(outcomes.Contains(entry.GetProperty("outcome").GetString()),"outcome in lower case");
        }
        Check(root.GetProperty("entries")[0].GetProperty("label").GetString() == "created" && root.GetProperty("entries")[1].GetProperty("outcome").GetString() == "crashed","labels and outcomes");
    }),
    ("README in English then French with the banner; GPL v3; sample plugin with three windows; the CI example points at this repository",() =>
    {
        var halves = Read("README.md").Split("\n---\n");
        Check(halves.Length == 2,"README: English, ---, French");
        Check(halves[0].Contains(Options.Banner,StringComparison.Ordinal) && halves[1].Contains("non affilié à l'équipe de Dalamud",StringComparison.Ordinal),"the banner in both halves");
        int Headings(string half) => Regex.Matches(half,@"^#{1,3} ",RegexOptions.Multiline).Count;
        Check(Headings(halves[0]) == Headings(halves[1]) && Headings(halves[0]) >= 8,"same headings in both halves");
        var licence = Read("LICENSE");
        Check(licence.StartsWith("GNU GENERAL PUBLIC LICENSE",StringComparison.Ordinal) && licence.Contains("Version 3, 29 June 2007",StringComparison.Ordinal),"GPL v3");
        var sample = Read("samples/SamplePlugin/Plugin.cs");
        Check(Regex.Matches(sample,@":Window\(""").Count == 3 && sample.Contains("throw new InvalidOperationException",StringComparison.Ordinal)
              && sample.Contains("FFXIVClientStructs",StringComparison.Ordinal),"three windows: fine, throws, reads game memory");
        var example = Read("docs/github-actions-example.yml");
        Check(example.Contains("Shigu330/PluginUiTest",StringComparison.Ordinal) && !example.Contains("Tensho",StringComparison.Ordinal),"the CI example uses this repository");
    }),
};

var failed = 0;
foreach (var (name,run) in cases)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} passed");
return failed == 0 ? 0 : 1;
