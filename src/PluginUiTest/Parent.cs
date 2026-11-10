using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PluginUiTest;

/// <summary>
/// Safeguard « never a crash »: the parent never loads the plugin nor Dalamud. It starts the same exe in --child mode, reads its events,
/// stops it when a part runs over the timeout (blocked), and when it dies (access violation, stack overflow…) names the part that was
/// running in plain words and starts a new child for the parts not reported yet, without that one. Then it writes the report.
/// </summary>
internal static class Parent
{
    public static int Run(Options options,string hooks,string plugin)
    {
        var report=new RunReport{Plugin=plugin};
        report.Facts.Add($"Frames per window: {options.Frames}; timeout: {options.Timeout} s per part (constructor: {options.Timeout*2} s)");
        if(options.RunAnyway.Count>0)report.Facts.Add("Run anyway (game memory): "+string.Join(", ",options.RunAnyway));
        var infos=new List<string>();
        for(var attempt=1;attempt<=50;attempt++)
        {
            var exclude=report.Entries.Where(e=>e.Key!="constructor").Select(e=>e.Key).ToArray();
            var child=RunChild(options,hooks,plugin,exclude,report,infos);
            if(child.Done || report.Fatal is not null)break;
            if(child.Stage is not { } stage)
            {
                report.Fatal=$"the drawing process {(child.TimedOut ? "stopped answering" : "ended")} before the plugin's code ran ({child.Exit}){(child.Unhandled is null ? "" : ": "+child.Unhandled)}";
                report.FatalCode=Options.ExitNotCreated;
                break;
            }
            var detail=child.TimedOut ? $"did not return within {child.Limit} s: the drawing process was stopped" : $"the drawing process died while it ran: {child.Exit}{(child.Unhandled is null ? "" : " ("+child.Unhandled+")")}";
            var name=report.Entries.FirstOrDefault(e=>e.Key==stage.Key)?.Name ?? stage.Key[(stage.Key.IndexOf(':')+1)..];
            Console.WriteLine($"  {(child.TimedOut ? "BLOCKED" : "CRASHED")} {name}: {detail}");
            if(stage.Key=="constructor")
            {
                report.Put(new Entry{Key="constructor",Kind="constructor",Name="constructor",Outcome=child.TimedOut ? Outcome.Blocked : Outcome.Crashed,Detail=detail});
                report.Fatal=$"the plugin constructor {(child.TimedOut ? "blocked" : "crashed the drawing process")}: {detail}";
                report.FatalCode=Options.ExitNotCreated;
                break;
            }
            report.Put(new Entry{Key=stage.Key,Kind=stage.Kind,Name=name,Outcome=child.TimedOut ? Outcome.Blocked : Outcome.Crashed,Detail=detail+"; the other parts were drawn again without it"});
            Console.WriteLine("  starting a new drawing process for the parts not drawn yet");
        }
        foreach(var info in infos.Distinct())report.Facts.Add(info);
        Console.WriteLine();
        Console.WriteLine(report.Summary);
        if(options.Out is { } output)Write(output,report.Markdown());
        if(options.Json is { } json)Write(json,report.Json());
        return report.ExitCode;
    }

    private static void Write(string path,string text)
    {
        try
        {
            var full=Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,text,new UTF8Encoding(false));
            Console.WriteLine("  report: "+full);
        }
        catch(Exception error){Console.WriteLine($"  report not written ({path}): {error.Message}");}
    }

    private sealed record Stage(string Key,string Kind,DateTime Since);

    private sealed class ChildRun
    {
        public bool Done,TimedOut;
        public Stage? Stage;
        public string Exit="";
        public string? Unhandled;
        public int Limit;
        public DateTime LastEvent=DateTime.UtcNow;
    }

    private static ChildRun RunChild(Options options,string hooks,string plugin,string[] exclude,RunReport report,List<string> infos)
    {
        var start=new ProcessStartInfo{UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        var self=Environment.ProcessPath!;
        start.FileName=self;
        // Started as "dotnet PluginUiTest.dll": the child is started the same way.
        if(Path.GetFileNameWithoutExtension(self).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(Parent).Assembly.Location);
        foreach(var arg in new[]{plugin,"--child","--dalamud="+hooks,$"--frames={options.Frames}",$"--timeout={options.Timeout}"})start.ArgumentList.Add(arg);
        if(options.Sqpack is { } sqpack)start.ArgumentList.Add("--sqpack="+sqpack);
        if(options.NoGameData)start.ArgumentList.Add("--no-game-data");
        if(options.RunAnyway.Count>0)start.ArgumentList.Add("--run-anyway="+string.Join(",",options.RunAnyway));
        if(exclude.Length>0)start.ArgumentList.Add("--exclude="+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(exclude)));
        var run=new ChildRun();
        var gate=new object();
        using var process=new Process{StartInfo=start};
        void Line(string? line)
        {
            if(line is null)return;
            lock(gate)
            {
                run.LastEvent=DateTime.UtcNow;
                if(!line.StartsWith(Child.Prefix,StringComparison.Ordinal)){Console.WriteLine("  | "+line);return;}
                using var json=JsonDocument.Parse(line[Child.Prefix.Length..]);
                var root=json.RootElement;
                switch(root.GetProperty("ev").GetString())
                {
                    case "info":
                        var text=root.GetProperty("text").GetString()!;
                        if(!infos.Contains(text)){infos.Add(text);Console.WriteLine("  "+text);}
                        break;
                    case "stage":
                        run.Stage=new Stage(root.GetProperty("key").GetString()!,root.GetProperty("kind").GetString()!,DateTime.UtcNow);
                        break;
                    case "result":
                        var entry=root.GetProperty("entry").Deserialize<Entry>(new JsonSerializerOptions{Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}})!;
                        if(entry.Key=="constructor" && report.Entries.Any(e=>e.Key=="constructor"))break;// created again in a later child: the first report stays
                        report.Put(entry);
                        run.Stage=null;
                        Console.WriteLine($"  {RunReport.Label(entry).ToUpperInvariant(),-8} {entry.Name}{(entry.Detail is null ? "" : ": "+entry.Detail)}{(entry.Where is null ? "" : " [at "+entry.Where+"]")}");
                        break;
                    case "fatal":
                        report.Fatal=root.GetProperty("message").GetString();
                        report.FatalCode=root.GetProperty("code").GetInt32();
                        break;
                    case "unhandled":
                        run.Unhandled=root.GetProperty("text").GetString();
                        break;
                    case "done":
                        run.Done=true;
                        break;
                }
            }
        }
        process.OutputDataReceived+=(_,e)=>Line(e.Data);
        process.ErrorDataReceived+=(_,e)=>{if(e.Data is { Length:>0 } text)lock(gate)Console.WriteLine("  | "+text);};
        process.Start();
        process.BeginOutputReadLine();process.BeginErrorReadLine();
        while(!process.WaitForExit(200))
        {
            lock(gate)
            {
                var now=DateTime.UtcNow;
                run.Limit=run.Stage?.Key=="constructor" ? options.Timeout*2 : options.Timeout;
                var over=run.Stage is { } stage ? now-stage.Since>TimeSpan.FromSeconds(run.Limit)
                    : now-run.LastEvent>TimeSpan.FromSeconds(Math.Max(120,options.Timeout*4));
                if(!over)continue;
                run.TimedOut=true;
            }
            try{process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}
            process.WaitForExit(10_000);
            break;
        }
        process.WaitForExit();// the last lines of the output
        run.Exit=ExitText(process.ExitCode);
        return run;
    }

    /// <summary>A process exit code in plain words.</summary>
    public static string ExitText(int code)=>unchecked((uint)code) switch
    {
        0xC0000005=>"access violation (0xC0000005): native memory was read or written where nothing is, as game memory is outside the game",
        0xC00000FD=>"stack overflow (0xC00000FD): a recursion without end",
        0xC0000409=>"fail-fast (0xC0000409)",
        0xE0434352=>"unhandled .NET exception (0xE0434352)",
        _=>$"exit code {code} (0x{unchecked((uint)code):X8})",
    };
}
