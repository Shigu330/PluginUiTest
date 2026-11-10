using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;

namespace PluginUiTest.Host;

// Dalamud service stand-ins for drawing a plugin without the game (moved from src/TenshoAssistant.Desktop/Host.cs in X-120). IDataManager
// over Lumina when the game data is there (else AbsentDataManager), IFramework and IPluginLog as in tools/ClientSnapshot, ITextureProvider
// reading the game icons (.tex) through Lumina; any other Dalamud interface is an "absent" stand-in that answers default values and records
// each member the plugin reached.

/// <summary>A pointer to pixels handed to a renderer (an icon's BGRA data).</summary>
public readonly unsafe struct PixelPointer(byte* pointer){public readonly byte* Pointer=pointer;}

/// <summary>Game icons as Dalamud would give them, read by Lumina from the game data (none without it) and handed to the renderer, if any.</summary>
public static class GameIcons
{
    public static GameData? Data;
    /// <summary>The renderer's upload (width, height, BGRA pixels) → texture id; null in a headless run (the icon's id stands for it).</summary>
    public static Func<int,int,PixelPointer,ulong>? Upload;
    public static readonly Dictionary<uint,(ulong Id,int W,int H)?> Icons=[];

    /// <summary>ui/icon/XXX000/XXXXXX_hr1.tex (high resolution, else the normal one), or null.</summary>
    public static unsafe (ulong Id,int W,int H)? Icon(uint icon)
    {
        if(Icons.TryGetValue(icon,out var known))return known;
        (ulong,int,int)? result=null;
        var folder=$"ui/icon/{icon/1000*1000:000000}/{icon:000000}";
        var tex=Data?.GetFile<TexFile>(folder+"_hr1.tex") ?? Data?.GetFile<TexFile>(folder+".tex");
        if(tex is not null)
        {
            var bgra=tex.ImageData;
            fixed(byte* pixels=bgra)
                result=Upload is { } upload ? (upload(tex.Header.Width,tex.Header.Height,new PixelPointer(pixels)),tex.Header.Width,tex.Header.Height) : (icon,tex.Header.Width,tex.Header.Height);
        }
        Icons[icon]=result;
        return result;
    }
}

/// <summary>The default answer of a stand-in for a member it does not serve: false, 0, null, "", empty lists, completed tasks.</summary>
public static class StandInDefaults
{
    public static object? For(Type type)
    {
        if(type==typeof(void))return null;
        if(type==typeof(string))return "";
        if(type.IsValueType)return Activator.CreateInstance(type);
        if(type.IsArray)return Array.CreateInstance(type.GetElementType()!,0);
        if(type==typeof(Task))return Task.CompletedTask;
        if(type.IsGenericType && type.GetGenericTypeDefinition()==typeof(Task<>))
        {
            var value=type.GetGenericArguments()[0] is { IsValueType:true } inner ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null,[value]);
        }
        if(type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() is var g && (g==typeof(IEnumerable<>) || g==typeof(IReadOnlyList<>) || g==typeof(IReadOnlyCollection<>)))
            return Array.CreateInstance(type.GetGenericArguments()[0],0);
        return null;
    }
}

/// <summary>IDataManager stand-in, as tools/ClientSnapshot (a real class: DispatchProxy cannot emit GetExcelSheet's self-referencing constraint).</summary>
public sealed class DataManagerStub(GameData data,Language language):Dalamud.Plugin.Services.IDataManager
{
    private static Language? Lumina(Dalamud.Game.ClientLanguage? given)=>given is { } value ? Enum.Parse<Language>(value.ToString()) : null;
    public Dalamud.Game.ClientLanguage Language=>Enum.Parse<Dalamud.Game.ClientLanguage>(language.ToString());
    public GameData GameData=>data;
    public Lumina.Excel.ExcelModule Excel=>data.Excel;
    public bool HasModifiedGameDataFiles=>false;
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelRow<T>
        =>data.Excel.GetSheet<T>(Lumina(language) ?? Current,name);
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelSubrow<T>
        =>data.Excel.GetSubrowSheet<T>(Lumina(language) ?? Current,name);
    private Language Current=>language;
    public FileResource? GetFile(string path)=>data.GetFile(path);
    public T? GetFile<T>(string path) where T:FileResource=>data.GetFile<T>(path);
    public Task<T> GetFileAsync<T>(string path,CancellationToken cancellationToken) where T:FileResource=>Task.Run(()=>data.GetFile<T>(path)!,cancellationToken);
    public bool FileExists(string path)=>data.FileExists(path);
}

/// <summary>Thrown by the IDataManager stand-in of a run without game data when a sheet is asked (recorded, and named in the report).</summary>
public sealed class GameDataAbsentException(string member):InvalidOperationException($"no game data in this run ({member}): pass --sqpack=<game\\sqpack folder> to read the sheets");

/// <summary>IDataManager without game data (CI, where only Dalamud is downloaded): every member recorded; sheets throw GameDataAbsentException.</summary>
public sealed class AbsentDataManager:Dalamud.Plugin.Services.IDataManager
{
    private static T Absent<T>(string member,T value){AbsentProxy.Note("IDataManager."+member);return value;}
    public Dalamud.Game.ClientLanguage Language=>Absent("Language",Dalamud.Game.ClientLanguage.English);
    public GameData GameData=>Absent<GameData>("GameData",null!);
    public Lumina.Excel.ExcelModule Excel=>Absent<Lumina.Excel.ExcelModule>("Excel",null!);
    public bool HasModifiedGameDataFiles=>Absent("HasModifiedGameDataFiles",false);
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelRow<T>
        =>throw new GameDataAbsentException(Absent("GetExcelSheet",$"IDataManager.GetExcelSheet<{typeof(T).Name}>"));
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language=null,string? name=null) where T:struct,Lumina.Excel.IExcelSubrow<T>
        =>throw new GameDataAbsentException(Absent("GetSubrowExcelSheet",$"IDataManager.GetSubrowExcelSheet<{typeof(T).Name}>"));
    public FileResource? GetFile(string path)=>Absent<FileResource?>("GetFile",null);
    public T? GetFile<T>(string path) where T:FileResource=>Absent<T?>("GetFile",null);
    public Task<T> GetFileAsync<T>(string path,CancellationToken cancellationToken) where T:FileResource
        =>Task.FromException<T>(new GameDataAbsentException(Absent("GetFileAsync","IDataManager.GetFileAsync "+path)));
    public bool FileExists(string path)=>Absent("FileExists",false);
}

/// <summary>IFramework stand-in: always on the framework thread; keeps the Update handlers so the host can tick them.</summary>
public class FrameworkProxy:DispatchProxy
{
    public Dalamud.Plugin.Services.IFramework.OnUpdateDelegate? Handlers;
    public static Dalamud.Plugin.Services.IFramework Create()=>Create<Dalamud.Plugin.Services.IFramework,FrameworkProxy>();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        switch(method!.Name)
        {
            case "get_IsInFrameworkUpdateThread":return true;
            case "get_IsFrameworkUnloading":return false;
            case "add_Update":Handlers+=(Dalamud.Plugin.Services.IFramework.OnUpdateDelegate)args![0]!;return null;
            case "remove_Update":Handlers-=(Dalamud.Plugin.Services.IFramework.OnUpdateDelegate)args![0]!;return null;
        }
        AbsentProxy.Note("IFramework."+method.Name);
        return method.ReturnType.IsValueType && method.ReturnType!=typeof(void) ? Activator.CreateInstance(method.ReturnType) : StandInDefaults.For(method.ReturnType);
    }
}

/// <summary>IPluginLog stand-in: warnings and errors printed and counted; every message handed to Listener when set.</summary>
public class LogProxy:DispatchProxy
{
    public static int Warnings;public static string? LastError;
    /// <summary>(level, text) of every message of the plugin, when a host wants them (the test tool's report).</summary>
    public static Action<string,string>? Listener;
    public static Dalamud.Plugin.Services.IPluginLog Create()=>Create<Dalamud.Plugin.Services.IPluginLog,LogProxy>();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;
        if(name.StartsWith("get_",StringComparison.Ordinal))return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType):null;
        var text=string.Join(" | ",(args ?? []).Select(a=>a switch{Exception e=>e.GetType().Name+": "+e.Message,object[] list=>string.Join(",",list),_=>a?.ToString()}));
        Listener?.Invoke(name,text);
        if(name is "Warning"){Warnings++;if(Environment.GetEnvironmentVariable("PROBE_LOG") is not null)Console.WriteLine("  log warning: "+text);}
        else if(name is "Error" or "Fatal"){LastError=text;Console.WriteLine("  log error: "+text);}
        return null;
    }
}

/// <summary>Any other Dalamud interface: default answers (false, 0, null, empty, completed tasks), each member reached recorded.</summary>
public class AbsentProxy:DispatchProxy
{
    public static readonly HashSet<string> Reached=[];
    public static void Note(string member){lock(Reached)Reached.Add(member);}
    /// <summary>When set, a member returning a Dalamud interface answers another absent stand-in instead of null (IDtrBar.Get → an entry):
    /// the test tool sets it so a plugin's constructor gets further; the desktop application keeps null (its pages were measured that way).</summary>
    public static bool Nested;
    public string Name="";
    public static object? Create(Type type)
    {
        try{var proxy=DispatchProxy.Create(type,typeof(AbsentProxy));((AbsentProxy)proxy).Name=type.Name;return proxy;}
        catch(Exception e){Note($"({type.Name}: no stand-in, {e.GetType().Name})");return null;}
    }
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;
        if(!name.StartsWith("add_",StringComparison.Ordinal) && !name.StartsWith("remove_",StringComparison.Ordinal))Note(Name+"."+name);
        var parameters=method.GetParameters();
        for(var i=0;i<parameters.Length;i++)
            if(parameters[i].IsOut)args![i]=parameters[i].ParameterType.GetElementType() is { IsValueType:true } element ? Activator.CreateInstance(element):null;
        var type=method.ReturnType;
        if(Nested && type.IsInterface && type.Assembly==typeof(Dalamud.Plugin.IDalamudPlugin).Assembly)return Create(type);
        return StandInDefaults.For(type);
    }
}

/// <summary>ITextureProvider stand-in: game icons from Lumina (.tex), handed to the renderer; anything else recorded as absent.</summary>
public class TextureProviderProxy:DispatchProxy
{
    public static Dalamud.Plugin.Services.ITextureProvider Create()=>Create<Dalamud.Plugin.Services.ITextureProvider,TextureProviderProxy>();
    public static int Served,Missing;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        switch(method!.Name)
        {
            case "TryGetFromGameIcon":
            case "GetFromGameIcon":
                var lookup=(Dalamud.Interface.Textures.GameIconLookup)args![0]!;
                var shared=SharedTextureProxy.Create(lookup.IconId);
                if(method.Name=="GetFromGameIcon")return shared;
                args[1]=shared;return true;
        }
        AbsentProxy.Note("ITextureProvider."+method.Name);
        return method.ReturnType==typeof(bool) ? false : method.ReturnType.IsValueType && method.ReturnType!=typeof(void) ? Activator.CreateInstance(method.ReturnType):null;
    }
}

public class SharedTextureProxy:DispatchProxy
{
    private uint icon;
    public static Dalamud.Interface.Textures.ISharedImmediateTexture Create(uint icon)
    {var proxy=Create<Dalamud.Interface.Textures.ISharedImmediateTexture,SharedTextureProxy>();((SharedTextureProxy)(object)proxy).icon=icon;return proxy;}
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var loaded=GameIcons.Icon(icon);
        if(method!.Name is "TryGetWrap")
        {
            args![1]=null;
            if(loaded is not { } found){args[0]=null;TextureProviderProxy.Missing++;return false;}
            TextureProviderProxy.Served++;
            args[0]=WrapProxy.Create(found.Id,found.W,found.H);return true;
        }
        if(method.Name is "GetWrapOrEmpty" or "GetWrapOrDefault")return loaded is { } f ? WrapProxy.Create(f.Id,f.W,f.H) : null;
        AbsentProxy.Note("ISharedImmediateTexture."+method.Name);
        return method.ReturnType.IsValueType && method.ReturnType!=typeof(void) ? Activator.CreateInstance(method.ReturnType):null;
    }
}

public class WrapProxy:DispatchProxy
{
    private ulong id;private int w,h;
    public static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap Create(ulong id,int w,int h)
    {var proxy=Create<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap,WrapProxy>();var self=(WrapProxy)(object)proxy;self.id=id;self.w=w;self.h=h;return proxy;}
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
    {
        "get_Handle"=>(ImTextureID)id,"get_Width"=>w,"get_Height"=>h,"get_Size"=>new Vector2(w,h),"Dispose"=>null,
        _=>method.ReturnType.IsValueType && method.ReturnType!=typeof(void) ? Activator.CreateInstance(method.ReturnType):null,
    };
}
