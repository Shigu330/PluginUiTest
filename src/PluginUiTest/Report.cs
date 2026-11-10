using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PluginUiTest;

/// <summary>What happened to one part of the plugin: its constructor, a UiBuilder handler, or a window.</summary>
public enum Outcome
{
    /// <summary>Created (constructor) or ran (handler) or drawn for every frame without an exception.</summary>
    Drawn,
    /// <summary>The window's DrawConditions answered false on every frame (nothing to draw; not a failure).</summary>
    Hidden,
    /// <summary>An exception came out of the plugin's code.</summary>
    Threw,
    /// <summary>It did not return within the timeout: the child process was stopped.</summary>
    Blocked,
    /// <summary>The child process died while it ran (access violation, stack overflow…).</summary>
    Crashed,
    /// <summary>Not run: its code can reach game memory (static analysis); --run-anyway overrides.</summary>
    Skipped,
}

/// <summary>One line of the report (pure: linked in tests/PluginUiTest.Tests).</summary>
public sealed record Entry
{
    public string Key { get; init; }="";
    /// <summary>constructor, OpenMainUi, OpenConfigUi, window, draw-handler.</summary>
    public string Kind { get; init; }="";
    public string Name { get; init; }="";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Outcome Outcome { get; init; }
    /// <summary>Exception type and message, the crash's exit code, the block, or the game-memory finding.</summary>
    public string? Detail { get; init; }
    /// <summary>The first frame of the plugin's own code in the exception's stack, or the call path to game memory.</summary>
    public string? Where { get; init; }
    public int Frames { get; init; }
    public int Vertices { get; init; }
    public IReadOnlyList<string> Absent { get; init; }=[];
    public IReadOnlyList<string> Network { get; init; }=[];
    public IReadOnlyList<string> LogErrors { get; init; }=[];
}

/// <summary>The run: header facts, entries in order, the exit code and the two report formats.</summary>
public sealed class RunReport
{
    public string Plugin { get; set; }="";
    public List<string> Facts { get; }=[];
    public List<Entry> Entries { get; }=[];
    /// <summary>Set when the plugin could not be created (exit 3) or the run was refused.</summary>
    public string? Fatal { get; set; }
    public int? FatalCode { get; set; }

    public void Put(Entry entry)
    {
        var at=Entries.FindIndex(e=>e.Key==entry.Key);
        if(at<0)Entries.Add(entry);else Entries[at]=entry;
    }

    public int ExitCode=>FatalCode ?? (Entries.Any(e=>e.Outcome is Outcome.Threw or Outcome.Blocked or Outcome.Crashed) ? Options.ExitFailed : Options.ExitDrawn);

    public string Summary
    {
        get
        {
            if(Fatal is not null)return $"NOT CREATED (exit {ExitCode}): {Fatal}";
            var windows=Entries.Where(e=>e.Kind is "window" or "draw-handler").ToList();
            string Count(Outcome o)=>$"{windows.Count(e=>e.Outcome==o)} {o.ToString().ToLowerInvariant()}";
            return $"{(ExitCode==0 ? "PASS" : "FAIL")} (exit {ExitCode}): {windows.Count} windows and draw handlers: "
                   +string.Join(", ",new[]{Count(Outcome.Drawn),Count(Outcome.Hidden),Count(Outcome.Threw),Count(Outcome.Blocked),Count(Outcome.Crashed),Count(Outcome.Skipped)});
        }
    }

    public string Markdown()
    {
        static string Cell(string? text)=>string.IsNullOrEmpty(text) ? "-" : text.Replace("|","/").Replace("\r"," ").Replace("\n"," ");
        static string List(IReadOnlyList<string> items)=>items.Count==0 ? "-" : Cell(string.Join(", ",items));
        var text=new StringBuilder();
        text.AppendLine("# Plugin UI test report");
        text.AppendLine();
        text.AppendLine("> "+Options.Banner);
        text.AppendLine("> "+Options.DataNote);
        text.AppendLine();
        text.AppendLine($"- Plugin: `{Plugin}`");
        foreach(var fact in Facts)text.AppendLine("- "+fact);
        text.AppendLine($"- Result: **{Summary}**");
        text.AppendLine();
        text.AppendLine("| Part | Kind | Result | Detail | Where | Frames | Vertices (max) | Dalamud members reached (absent stand-ins) | Network | Plugin log errors |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach(var e in Entries)
            text.AppendLine($"| {Cell(e.Name)} | {e.Kind} | {Label(e)} | {Cell(e.Detail)} | {Cell(e.Where)} | {e.Frames} | {e.Vertices} | {List(e.Absent)} | {List(e.Network)} | {List(e.LogErrors)} |");
        text.AppendLine();
        text.AppendLine("Results: drawn (no exception on any frame), hidden (DrawConditions false: nothing to draw), threw, blocked (over the timeout: stopped),");
        text.AppendLine("crashed (the drawing process died: the other windows were drawn again without it), skipped (its code can reach game memory: run it");
        text.AppendLine("with --run-anyway=<name>, still inside the child process).");
        return text.ToString();
    }

    /// <summary>The result word: created (constructor), ran (OpenMainUi, OpenConfigUi), else the outcome.</summary>
    public static string Label(Entry entry)=>entry.Outcome==Outcome.Drawn && entry.Kind=="constructor" ? "created"
        : entry.Outcome==Outcome.Drawn && entry.Kind is "OpenMainUi" or "OpenConfigUi" or "load" or "framework" ? "ran" : entry.Outcome.ToString().ToLowerInvariant();

    /// <summary>The version of the JSON report's shape (docs/report.schema.json). Removing or renaming a field, or changing its meaning,
    /// raises it; a new optional field does not.</summary>
    public const int SchemaVersion=1;
    public const string SchemaUrl="https://raw.githubusercontent.com/Shigu330/PluginUiTest/main/docs/report.schema.json";

    /// <summary>The JSON report as docs/report.schema.json describes it: camelCase names, outcomes in lower case.</summary>
    public string Json()=>JsonSerializer.Serialize(new Dictionary<string,object?>
    {
        ["$schema"]=SchemaUrl,["schemaVersion"]=SchemaVersion,["tool"]="PluginUiTest "+(typeof(Options).Assembly.GetName().Version?.ToString(3) ?? "?"),
        ["banner"]=Options.Banner,["note"]=Options.DataNote,["plugin"]=Plugin,["facts"]=Facts,["result"]=Summary,["exitCode"]=ExitCode,["fatal"]=Fatal,
        ["entries"]=Entries.Select(e=>new Dictionary<string,object?>
        {
            ["key"]=e.Key,["kind"]=e.Kind,["name"]=e.Name,["outcome"]=e.Outcome.ToString().ToLowerInvariant(),["label"]=Label(e),
            ["detail"]=e.Detail,["where"]=e.Where,["frames"]=e.Frames,["vertices"]=e.Vertices,
            ["absent"]=e.Absent,["network"]=e.Network,["logErrors"]=e.LogErrors,
        }).ToList(),
    },new JsonSerializerOptions{WriteIndented=true});
}
