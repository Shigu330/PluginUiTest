using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PluginUiTest.Host;

namespace PluginUiTest;

/// <summary>A constructor parameter or [PluginService] member that is neither a Dalamud service nor given: the plugin cannot be created.</summary>
internal sealed class ServiceMissingException(string message):Exception(message);

/// <summary>
/// The plugin created as Dalamud creates it: an uninitialised instance of the IDalamudPlugin type, its [PluginService] properties and fields
/// (static and instance) filled, then its constructor run with IDalamudPluginInterface and the services it asks for. Services: IDataManager
/// over Lumina when the game data is there (else AbsentDataManager), IFramework, IPluginLog and ITextureProvider stand-ins, any other Dalamud
/// interface an absent stand-in (PluginUiTest.Host's StandIns). IDalamudPluginInterface.Create&lt;T&gt; and Inject do the same for the
/// plugin's own classes. The plugin's configuration folder is a fresh folder of this tool, never the user's pluginConfigs.
/// </summary>
internal sealed class PluginFactory
{
    public readonly Assembly Plugin;
    public readonly string OriginalPath;
    public readonly string ConfigFolder;
    public readonly IDataManager Data;
    public readonly UiBuilderProxy Ui;
    public readonly IDalamudPluginInterface Interface;
    private readonly Dictionary<Type,object?> services=[];

    public PluginFactory(Assembly plugin,string originalPath,string configFolder,IDataManager data)
    {
        Plugin=plugin;OriginalPath=originalPath;ConfigFolder=configFolder;Data=data;
        Ui=(UiBuilderProxy)(object)UiBuilderProxy.Create();
        Interface=PluginInterfaceProxy.Create(this);
    }

    public object? Service(Type type)
    {
        if(services.TryGetValue(type,out var known))return known;
        object? value=type.FullName switch
        {
            "Dalamud.Plugin.IDalamudPluginInterface"=>Interface,
            "Dalamud.Plugin.Services.IDataManager"=>Data,
            "Dalamud.Plugin.Services.IFramework"=>FrameworkProxy.Create(),
            "Dalamud.Plugin.Services.IPluginLog"=>LogProxy.Create(),
            "Dalamud.Plugin.Services.ITextureProvider"=>TextureProviderProxy.Create(),
            "Dalamud.Interface.IUiBuilder"=>Ui,
            "Dalamud.Plugin.Services.ICommandManager"=>CommandManagerProxy.Create(),
            _=>type.IsInterface && type.Assembly==typeof(IDalamudPlugin).Assembly ? AbsentProxy.Create(type) : null,
        };
        if(value is null)return null;
        services[type]=value;
        return value;
    }

    private static bool IsService(MemberInfo member)=>member.GetCustomAttributes(false).Any(a=>a.GetType().FullName=="Dalamud.IoC.PluginServiceAttribute");

    /// <summary>Fills the [PluginService] properties and fields of this type (static ones when instance is null).</summary>
    public void Inject(object? instance,Type type,object[] scoped)
    {
        var flags=BindingFlags.Public|BindingFlags.NonPublic|(instance is null ? BindingFlags.Static : BindingFlags.Instance|BindingFlags.Static);
        for(var current=type;current is not null && current!=typeof(object);current=current.BaseType)
        {
            foreach(var property in current.GetProperties(flags|BindingFlags.DeclaredOnly).Where(IsService))
            {
                var value=Value(property.PropertyType,scoped,$"[PluginService] {current.Name}.{property.Name}");
                var target=property.GetMethod?.IsStatic==true ? null : instance;
                if(property.GetSetMethod(true) is { } setter)setter.Invoke(target,[value]);
                else current.GetField($"<{property.Name}>k__BackingField",BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic)?.SetValue(target,value);
            }
            foreach(var field in current.GetFields(flags|BindingFlags.DeclaredOnly).Where(IsService))
                field.SetValue(field.IsStatic ? null : instance,Value(field.FieldType,scoped,$"[PluginService] {current.Name}.{field.Name}"));
        }
    }

    private object? Value(Type type,object[] scoped,string what)
        => scoped.FirstOrDefault(type.IsInstanceOfType) ?? Service(type) ?? throw new ServiceMissingException($"{what}: {type.FullName} is not a Dalamud service this tool can stand in for");

    /// <summary>An object of the plugin as Dalamud's IoC makes it: uninitialised, injected, then its constructor run on it.</summary>
    public object Construct(Type type,object[] scoped)
    {
        var ctor=type.GetConstructors(BindingFlags.Instance|BindingFlags.Public).OrderByDescending(c=>c.GetParameters().Length).FirstOrDefault()
                 ?? throw new ServiceMissingException($"{type.FullName} has no public constructor");
        var args=ctor.GetParameters().Select(p=>Value(p.ParameterType,scoped,$"constructor parameter {p.Name}")).ToArray();
        var instance=RuntimeHelpers.GetUninitializedObject(type);
        Inject(instance,type,scoped);
        try{ctor.Invoke(instance,args);}
        catch(TargetInvocationException error) when(error.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();}
        return instance;
    }

    public static ConstructorInfo? PluginConstructor(Type type)
        => type.GetConstructors(BindingFlags.Instance|BindingFlags.Public).OrderByDescending(c=>c.GetParameters().Length).FirstOrDefault();
}

/// <summary>IDalamudPluginInterface stand-in: the plugin's own place, a fresh configuration folder, UiBuilder, Create&lt;T&gt; and Inject.</summary>
public class PluginInterfaceProxy:DispatchProxy
{
    private PluginFactory factory=null!;
    private readonly Dictionary<string,object?> data=[];
    internal static IDalamudPluginInterface Create(PluginFactory factory)
    {
        var proxy=Create<IDalamudPluginInterface,PluginInterfaceProxy>();
        ((PluginInterfaceProxy)(object)proxy).factory=factory;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;
        switch(name)
        {
            case "get_UiBuilder":return factory.Ui;
            case "get_Assembly":return factory.Plugin;
            case "get_AssemblyLocation":return new FileInfo(factory.OriginalPath);
            case "get_InternalName":return factory.Plugin.GetName().Name ?? "";
            case "get_ConfigDirectory":return new DirectoryInfo(factory.ConfigFolder);
            case "get_ConfigFile":return new FileInfo(factory.ConfigFolder+".json");
            case "GetPluginConfigDirectory":return factory.ConfigFolder;
            case "GetPluginLocDirectory":return Path.Combine(factory.ConfigFolder,"loc");
            case "get_IsDev":return true;
            case "get_UiLanguage":return "en";
            case "get_LoadTime":return DateTime.Now;
            case "get_LoadTimeUTC":return DateTime.UtcNow;
            case "get_DalamudAssetDirectory":
                return new DirectoryInfo(Path.Combine(DalamudPaths.XivLauncher(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),"dalamudAssets","dev"));
            case "GetPluginConfig":AbsentProxy.Note("IDalamudPluginInterface.GetPluginConfig (new install: none)");return null;
            case "SavePluginConfig":AbsentProxy.Note("IDalamudPluginInterface.SavePluginConfig (not saved)");return null;
            case "Create":return factory.Construct(method.ReturnType,(object[])args![0]!);
            case "CreateAsync":
            {
                var type=method.ReturnType.GetGenericArguments()[0];
                var made=factory.Construct(type,(object[])args![0]!);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null,[made]);
            }
            case "Inject":factory.Inject(args![0],args[0]!.GetType(),(object[])args[1]!);return true;
            case "InjectAsync":factory.Inject(args![0],args[0]!.GetType(),(object[])args[1]!);return Task.CompletedTask;
            case "GetOrCreateData":
                if(!data.TryGetValue((string)args![0]!,out var value))data[(string)args[0]!]=value=((Delegate)args[1]!).DynamicInvoke();
                return value;
            case "GetIpcSubscriber":
            case "GetIpcProvider":
                AbsentProxy.Note($"IDalamudPluginInterface.{name} {args?[0]}");
                return AbsentProxy.Create(method.ReturnType);
        }
        if(name.StartsWith("add_",StringComparison.Ordinal) || name.StartsWith("remove_",StringComparison.Ordinal))return null;
        AbsentProxy.Note("IDalamudPluginInterface."+name);
        var parameters=method.GetParameters();
        for(var i=0;i<parameters.Length;i++)
            if(parameters[i].IsOut)args![i]=parameters[i].ParameterType.GetElementType() is { IsValueType:true } element ? Activator.CreateInstance(element):null;
        return method.ReturnType==typeof(bool) ? false : StandInDefaults.For(method.ReturnType);
    }
}

/// <summary>IUiBuilder stand-in: records the Draw, OpenMainUi and OpenConfigUi subscribers; fonts are the host's atlas font.</summary>
public class UiBuilderProxy:DispatchProxy
{
    public readonly List<Action> Draw=[],OpenMainUi=[],OpenConfigUi=[];
    public ulong FrameCount;
    public static IUiBuilder Create()=>Create<IUiBuilder,UiBuilderProxy>();

    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;
        List<Action>? list=name[(name.IndexOf('_')+1)..] switch{"Draw"=>Draw,"OpenMainUi"=>OpenMainUi,"OpenConfigUi"=>OpenConfigUi,_=>null};
        if(name.StartsWith("add_",StringComparison.Ordinal)){if(list is not null && args![0] is Action add)list.Add(add);return null;}
        if(name.StartsWith("remove_",StringComparison.Ordinal)){if(list is not null && args![0] is Action remove)list.Remove(remove);return null;}
        switch(name)
        {
            case "get_FontDefault":case "get_FontIcon":case "get_FontMono":case "get_FontIconFixedWidth":return HostFonts.Main;
            case "get_FontDefaultSizePx":return 17f;
            case "get_FontDefaultSizePt":return 17f*3/4;
            case "get_UiPrepared":return true;
            case "get_FrameCount":return FrameCount;
            case "WaitForUi":return Task.CompletedTask;
            case "RunWhenUiPrepared":
            {
                var result=((Delegate)args![0]!).DynamicInvoke();
                if(result is Task)return result;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null,[result]);
            }
        }
        if(name.StartsWith("set_",StringComparison.Ordinal))return null;
        AbsentProxy.Note("IUiBuilder."+name);
        return StandInDefaults.For(method.ReturnType) ?? (method.ReturnType.IsInterface ? AbsentProxy.Create(method.ReturnType) : null);
    }
}

/// <summary>ICommandManager stand-in: AddHandler and RemoveHandler succeed (recorded, never run), Commands is empty.</summary>
public class CommandManagerProxy:DispatchProxy
{
    public static ICommandManager Create()=>Create<ICommandManager,CommandManagerProxy>();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        switch(method!.Name)
        {
            case "AddHandler":AbsentProxy.Note($"ICommandManager.AddHandler {args![0]}");return true;
            case "RemoveHandler":return true;
            case "get_Commands":
                var pair=method.ReturnType.GetGenericArguments();
                return pair.Length==2 ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(pair)) : null;
        }
        AbsentProxy.Note("ICommandManager."+method.Name);
        return StandInDefaults.For(method.ReturnType);
    }
}
