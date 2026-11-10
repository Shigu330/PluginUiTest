using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Lumina;
using Lumina.Data;
using PluginUiTest.Host;

namespace PluginUiTest;

/// <summary>
/// The drawing child process (--child): everything that runs the plugin's code happens here, so a crash or a block ends this process, not the
/// tool. It prints one event per line ("@@PUT {json}") for the parent: info, stage (a protected step starts), result, fatal, done. Steps:
/// headless ImGui context; game data or none; the plugin copy with UiBuilder's font statics rewritten; the constructor scanned for game
/// memory, then the plugin created as Dalamud does; OpenMainUi and OpenConfigUi invoked; every WindowSystem reachable from the plugin found
/// and each window drawn alone for N frames (Update, DrawConditions, PreDraw, Begin, Draw, End, PostDraw, Dalamud's error recovery); then
/// the UiBuilder.Draw subscribers that are not WindowSystem.Draw, with every window closed. Parts already reported by an earlier child of the
/// same run (--exclude) are not run again.
/// </summary>
internal static class Child
{
    public const string Prefix="@@PUT ";
    private static readonly object OutputGate=new();
    private static readonly List<string> logErrors=[];

    public static void Emit(object value)
    {
        var line=Prefix+JsonSerializer.Serialize(value);
        lock(OutputGate){Console.Out.WriteLine(line);Console.Out.Flush();}
    }

    [DllImport("kernel32")] private static extern uint SetErrorMode(uint mode);

    public static int Run(Options options,string hooks,string appData)
    {
        // A crash ends this process quietly (no Windows error dialog waiting for a click): the parent reports it.
        SetErrorMode(0x0001|0x0002|0x8000);
        Console.OutputEncoding=Encoding.UTF8;
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>Emit(new{ev="unhandled",text=e.ExceptionObject is Exception x ? $"{x.GetType().Name}: {x.Message}" : "unknown"});
        NetworkGuard.Install();
        var plugin=Path.GetFullPath(options.Plugin);
        var own=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PluginUiTest");
        var name=Path.GetFileNameWithoutExtension(plugin);
        var copyRoot=Path.Combine(own,"copies",name);
        string? copyFolder=null;
        DalamudRuntime.Install(hooks,()=>[copyFolder,Path.GetDirectoryName(plugin)]);
        return Body(options,hooks,appData,plugin,own,copyRoot,folder=>copyFolder=folder);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Body(Options options,string hooks,string appData,string plugin,string own,string copyRoot,Action<string> setCopy)
    {
        var excluded=Exclusions(options.Exclude);
        AbsentProxy.Nested=true;
        LogProxy.Listener=(level,text)=>{if(level is "Error" or "Fatal")lock(logErrors)logErrors.Add(Short(text));};
        var (w,h,_)=ImGuiHeadless.Start(17f,DalamudPaths.Fonts(appData));
        ImGuiHeadless.SetFontTexture(1);
        ImGuiHeadless.GlobalScale(1f);
        Info($"Dalamud: {hooks} (Dalamud.dll {AssemblyName.GetAssemblyName(Path.Combine(hooks,"Dalamud.dll")).Version}), Dear ImGui {ImGui.GetVersion()}, font atlas {w}x{h} ({(ImGuiHeadless.DalamudFonts ? "Dalamud's fonts" : "ImGui's default font: Dalamud's assets not found")})");
        // Game data: the folder given, else the user's install; none in CI: IDataManager answers absent.
        Dalamud.Plugin.Services.IDataManager data;
        var candidates=options.NoGameData ? [] : DalamudPaths.SqpackCandidates(options.Sqpack,ReadText(Path.Combine(DalamudPaths.XivLauncher(appData),"launcherConfigV3.json")));
        var sqpack=candidates.FirstOrDefault(Directory.Exists);
        if(options.Sqpack is not null && !Directory.Exists(options.Sqpack)){Fatal(Options.ExitUsage,$"--sqpack={options.Sqpack}: no such folder");return Options.ExitUsage;}
        if(sqpack is null)
        {
            data=new AbsentDataManager();
            Info("Game data: none (IDataManager answers absent and is recorded; sheets throw GameDataAbsentException)");
        }
        else
        {
            var game=new GameData(sqpack,new LuminaOptions{DefaultExcelLanguage=Language.English,CacheFileResources=false,PanicOnSheetChecksumMismatch=false});
            GameIcons.Data=game;
            data=new DataManagerStub(game,Language.English);
            Info($"Game data: {sqpack} (read-only, English sheets)");
        }
        // The plugin copy: UiBuilder's font statics and WindowSystem.Draw block outside the game; in the copy they read this tool's atlas
        // font and draw through HostWindows.
        var copy=Rewrite.Plugin(plugin,copyRoot,hooks,"PluginUiTest",[],windowSystems:true);
        setCopy(Path.GetDirectoryName(copy)!);
        Info($"Plugin copy: {copy} (rewritten calls: {(Rewrite.Sites.Count==0 ? "none" : string.Join(", ",Rewrite.Sites.Select(p=>$"{p.Key} {p.Value}")))})");
        var assembly=System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(copy);
        Info($"Assembly: {assembly.GetName().Name} {assembly.GetName().Version}");
        Type[] types;
        try{types=assembly.GetTypes();}
        catch(ReflectionTypeLoadException error){types=error.Types.OfType<Type>().ToArray();Info($"Some types could not be loaded: {error.LoaderExceptions.FirstOrDefault()?.Message}");}
        var pluginTypes=types.Where(t=>t is { IsClass:true,IsAbstract:false } && typeof(IDalamudPlugin).IsAssignableFrom(t)).ToArray();
        if(pluginTypes.Length==0){Fatal(Options.ExitNotCreated,$"no class implementing Dalamud.Plugin.IDalamudPlugin in {Path.GetFileName(plugin)}");return Options.ExitNotCreated;}
        var pluginType=pluginTypes[0];
        if(pluginTypes.Length>1)Info($"Several IDalamudPlugin classes: {string.Join(", ",pluginTypes.Select(t=>t.FullName))}; {pluginType.FullName} is created");

        using var scan=new MemoryScan(copy,Path.GetDirectoryName(plugin)!,hooks);
        var config=Path.Combine(own,"config",Path.GetFileNameWithoutExtension(plugin));
        try{if(Directory.Exists(config))Directory.Delete(config,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        Directory.CreateDirectory(config);
        var factory=new PluginFactory(assembly,plugin,config,data);
        Info($"Configuration folder: {config} (fresh for each run, never the user's pluginConfigs)");

        // The constructor: scanned, then created (Dalamud's way).
        var ctor=PluginFactory.PluginConstructor(pluginType);
        var starts=new[]{ctor,(MethodBase?)pluginType.TypeInitializer}.OfType<MethodBase>().Select(scan.Definition).OfType<Mono.Cecil.MethodDefinition>().ToArray();
        var ctorName=$"{pluginType.Name} constructor";
        var found=scan.Scan(starts);
        if(found is not null && !Options.Matches(options.RunAnyway,Options.Constructor,pluginType.Name,pluginType.FullName))
        {
            Result(new Entry{Key="constructor",Kind="constructor",Name=ctorName,Outcome=Outcome.Skipped,Detail="its code can reach game memory: "+found.What,Where=found.Path});
            Fatal(Options.ExitNotCreated,$"the plugin constructor can reach game memory ({found.Path}); it was not run, so nothing could be drawn. "
                +"To create it anyway (still inside the child process, a crash is reported, not suffered): --run-anyway=constructor");
            return Options.ExitNotCreated;
        }
        if(found is not null)Info($"{ctorName}: can reach game memory ({found.Path}); run anyway (--run-anyway), inside the child process");
        Stage("constructor","constructor");
        Begin();
        object instance;
        try{instance=factory.Construct(pluginType,[]);}
        catch(Exception error)
        {
            var inner=Unwrap(error);
            Result(End(new Entry{Key="constructor",Kind="constructor",Name=ctorName,Outcome=Outcome.Threw,Detail=$"{inner.GetType().Name}: {Short(inner.Message)}",Where=PluginFrame(inner,assembly)}));
            Fatal(Options.ExitNotCreated,inner is ServiceMissingException ? inner.Message : $"the plugin constructor threw {inner.GetType().Name}: {Short(inner.Message)}{(PluginFrame(inner,assembly) is { } at ? " (in "+at+")" : "")}");
            return Options.ExitNotCreated;
        }
        Result(End(new Entry{Key="constructor",Kind="constructor",Name=ctorName,Outcome=Outcome.Drawn,
            Detail=$"created; UiBuilder subscribers: Draw {factory.Ui.Draw.Count}, OpenMainUi {factory.Ui.OpenMainUi.Count}, OpenConfigUi {factory.Ui.OpenConfigUi.Count}"}));

        // OpenMainUi and OpenConfigUi, as the installer's buttons would.
        foreach(var (kind,list) in new[]{("OpenMainUi",factory.Ui.OpenMainUi),("OpenConfigUi",factory.Ui.OpenConfigUi)})
            foreach(var handler in list.ToArray())
            {
                var label=$"UiBuilder.{kind} → {handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
                var key=$"{kind}:{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
                if(excluded.Contains(key))continue;
                if(Gate(scan,options,key,kind,label,[handler.Method],[label,handler.Method.Name]))continue;
                Stage(key,kind);Begin();
                Entry entry;
                try{handler();entry=new Entry{Key=key,Kind=kind,Name=label,Outcome=Outcome.Drawn,Detail="ran"};}
                catch(Exception error){var inner=Unwrap(error);entry=new Entry{Key=key,Kind=kind,Name=label,Outcome=Outcome.Threw,Detail=$"{inner.GetType().Name}: {Short(inner.Message)}",Where=PluginFrame(inner,assembly)};}
                Result(End(entry));
            }

        // Every WindowSystem reachable from the plugin, and each window drawn alone.
        var systems=WindowSystems(instance,types,factory.Ui.Draw);
        var windows=systems.SelectMany(s=>s.Windows).Distinct().ToList();
        Info($"Window systems: {systems.Count} ({string.Join(", ",systems.Select(s=>$"{s.Namespace}: {s.Windows.Count} windows"))})");
        foreach(var window in windows)
        {
            var key="window:"+window.WindowName;
            if(excluded.Contains(key))continue;
            var methods=new[]{"Update","DrawConditions","PreDraw","Draw","PostDraw","OnOpen"}
                .Select(m=>window.GetType().GetMethod(m,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,Type.EmptyTypes)).OfType<MethodInfo>().ToArray();
            if(Gate(scan,options,key,"window",window.WindowName,methods,[window.WindowName,window.GetType().Name,window.GetType().FullName]))continue;
            Stage(key,"window");Begin();
            Result(End(DrawWindow(window,key,options.Frames,assembly)));
        }

        // UiBuilder.Draw subscribers that are not a WindowSystem's Draw (overlays, the plugin's own Draw), every window closed.
        foreach(var handler in factory.Ui.Draw.ToArray().Where(d=>d.Target is not WindowSystem))
        {
            var label=$"UiBuilder.Draw → {handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
            var key=$"draw:{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
            if(excluded.Contains(key))continue;
            if(Gate(scan,options,key,"draw-handler",label,[handler.Method],[label,handler.Method.Name]))continue;
            foreach(var window in windows)window.IsOpen=false;
            Stage(key,"draw-handler");Begin();
            Result(End(DrawHandler(handler,key,label,options.Frames,assembly,factory.Ui)));
        }
        Emit(new{ev="done"});
        return 0;
    }

    /// <summary>True (and the part reported skipped) when its code can reach game memory and --run-anyway does not name it.</summary>
    private static bool Gate(MemoryScan scan,Options options,string key,string kind,string name,IEnumerable<MethodBase> methods,string?[] names)
    {
        var starts=methods.Select(scan.Definition).OfType<Mono.Cecil.MethodDefinition>().ToArray();
        if(scan.Scan(starts) is not { } found)return false;
        if(Options.Matches(options.RunAnyway,[key,..names]))
        {
            Info($"{name}: can reach game memory ({found.Path}); run anyway (--run-anyway), inside the child process");
            return false;
        }
        Result(new Entry{Key=key,Kind=kind,Name=name,Outcome=Outcome.Skipped,Detail="its code can reach game memory: "+found.What+$". To draw it anyway: --run-anyway=\"{(Options.WithoutId(name) is { Length:>0 } shown ? shown : name)}\"",Where=found.Path});
        return true;
    }

    private static Entry DrawWindow(IWindow window,string key,int frames,Assembly plugin)
    {
        var errors=new List<(string Text,string? Where)>();
        void Catch(Exception error){var inner=Unwrap(error);var text=$"{inner.GetType().Name}: {Short(inner.Message)}";if(!errors.Any(e=>e.Text==text))errors.Add((text,PluginFrame(inner,plugin)));}
        window.IsOpen=true;
        try{window.OnOpen();}catch(Exception error){Catch(error);}
        var vertices=0;var shownFrames=0;
        for(var frame=0;frame<frames;frame++)
        {
            NewFrame();
            try{if(HostWindows.DrawOnce(window))shownFrames++;}
            catch(Exception error){Catch(error);}
            vertices=Math.Max(vertices,EndFrame());
            if(frame%5==4)Thread.Sleep(20);// the plugin's off-frame work (tasks it started) gets a moment
        }
        if(!window.IsOpen)Info($"{window.WindowName}: closed itself while drawn");
        var outcome=errors.Count>0 ? Outcome.Threw : shownFrames==0 ? Outcome.Hidden : Outcome.Drawn;
        return new Entry{Key=key,Kind="window",Name=window.WindowName,Outcome=outcome,Frames=frames,Vertices=vertices,
            Detail=errors.Count>0 ? string.Join("; ",errors.Select(e=>e.Text)) : outcome==Outcome.Hidden ? "DrawConditions answered false on every frame" : null,
            Where=errors.FirstOrDefault().Where};
    }

    private static Entry DrawHandler(Action handler,string key,string label,int frames,Assembly plugin,UiBuilderProxy ui)
    {
        var errors=new List<(string Text,string? Where)>();
        var vertices=0;
        for(var frame=0;frame<frames;frame++)
        {
            NewFrame();
            ui.FrameCount++;
            lock(HostWindows.Errors)HostWindows.Errors.Clear();
            try{handler();}
            catch(Exception error){var inner=Unwrap(error);var text=$"{inner.GetType().Name}: {Short(inner.Message)}";if(!errors.Any(e=>e.Text==text))errors.Add((text,PluginFrame(inner,plugin)));}
            lock(HostWindows.Errors)
                foreach(var (name,error) in HostWindows.Errors)
                {
                    var inner=Unwrap(error);var text=$"{name}: {inner.GetType().Name}: {Short(inner.Message)}";
                    if(!errors.Any(e=>e.Text==text))errors.Add((text,PluginFrame(inner,plugin)));
                }
            vertices=Math.Max(vertices,EndFrame());
            if(frame%5==4)Thread.Sleep(20);
        }
        return new Entry{Key=key,Kind="draw-handler",Name=label,Outcome=errors.Count>0 ? Outcome.Threw : Outcome.Drawn,Frames=frames,Vertices=vertices,
            Detail=errors.Count>0 ? string.Join("; ",errors.Select(e=>e.Text)) : null,Where=errors.FirstOrDefault().Where};
    }

    private static void NewFrame()
    {
        var io=ImGui.GetIO();
        io.DisplaySize=new Vector2(1920,1080);io.DeltaTime=1/60f;
        ImGui.NewFrame();
    }

    /// <summary>Dalamud's recovery (Begin/End, Push/Pop left unbalanced by an exception), then Render; the vertices of the frame.</summary>
    private static int EndFrame()
    {
        ImGuiP.ErrorCheckEndFrameRecover(null!);
        ImGui.Render();
        return ImGui.GetDrawData().TotalVtxCount;
    }

    /// <summary>The WindowSystems reachable from the plugin: its fields (and its objects' fields, a little), its types' static fields, Draw's targets.</summary>
    private static List<WindowSystem> WindowSystems(object plugin,Type[] types,IEnumerable<Action> draws)
    {
        var found=new List<WindowSystem>();
        var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);
        var asm=plugin.GetType().Assembly;
        void Walk(object? value,int depth)
        {
            if(value is null || depth>4 || value is string || value.GetType().IsPrimitive || !seen.Add(value))return;
            if(value is WindowSystem system){if(!found.Contains(system))found.Add(system);return;}
            var type=value.GetType();
            if(value is IEnumerable list && type.Assembly!=asm && (type.IsArray || type.Namespace?.StartsWith("System.Collections",StringComparison.Ordinal)==true))
            {
                var n=0;
                foreach(var item in list){if(++n>1000)break;Walk(item,depth+1);}
                return;
            }
            if(type.Assembly!=asm)return;
            for(var t=type;t is not null && t.Assembly==asm;t=t.BaseType)
                foreach(var field in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                    if(!field.FieldType.IsValueType || field.FieldType==typeof(object))
                        try{Walk(field.GetValue(value),depth+1);}catch(Exception){}
        }
        foreach(var draw in draws)if(draw.Target is WindowSystem system && !found.Contains(system))found.Add(system);
        Walk(plugin,0);
        foreach(var type in types.Where(t=>!t.ContainsGenericParameters))
            foreach(var field in type.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly).Where(f=>!f.IsLiteral))
                if(field.FieldType==typeof(WindowSystem) || field.FieldType.Assembly==asm)
                    try{Walk(field.GetValue(null),1);}catch(Exception){}
        return found;
    }

    private static HashSet<string> Exclusions(string? given)
    {
        if(string.IsNullOrEmpty(given))return [];
        try{return JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(given))!.ToHashSet();}catch(Exception){return [];}
    }

    // Per part: the absent members reached, the network attempts and the plugin's log errors since Begin.
    private static void Begin()
    {
        lock(AbsentProxy.Reached)AbsentProxy.Reached.Clear();
        NetworkGuard.Take();
        lock(logErrors)logErrors.Clear();
    }

    private static Entry End(Entry entry)
    {
        string[] absent;lock(AbsentProxy.Reached)absent=AbsentProxy.Reached.Order(StringComparer.Ordinal).ToArray();
        string[] errors;lock(logErrors)errors=logErrors.Distinct().Take(10).ToArray();
        return entry with{Absent=absent,Network=NetworkGuard.Take(),LogErrors=errors};
    }

    private static void Info(string text)=>Emit(new{ev="info",text});
    private static void Stage(string key,string kind)=>Emit(new{ev="stage",key,kind});
    private static void Result(Entry entry)=>Emit(new{ev="result",entry=JsonSerializer.SerializeToElement(entry)});
    private static void Fatal(int code,string message)=>Emit(new{ev="fatal",code,message});

    private static Exception Unwrap(Exception error)
    {
        while(error is TargetInvocationException { InnerException:{ } inner })error=inner;
        return error;
    }

    /// <summary>The first frame of the plugin's own code in the stack (with its file and line when the plugin's .pdb is beside it).</summary>
    private static string? PluginFrame(Exception error,Assembly plugin)
    {
        foreach(var frame in new StackTrace(error,true).GetFrames())
            if(frame.GetMethod() is { } method && method.DeclaringType?.Assembly==plugin)
                return $"{method.DeclaringType.Name}.{method.Name}"+(frame.GetFileName() is { } file ? $" ({Path.GetFileName(file)}:{frame.GetFileLineNumber()})" : "");
        return null;
    }

    internal static string Short(string text)=>(text.Length>200 ? text[..200]+"…" : text).Replace("\r"," ").Replace("\n"," ");

    private static string? ReadText(string path)
    {
        try{return File.Exists(path) ? File.ReadAllText(path) : null;}
        catch(IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
    }
}
