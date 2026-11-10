using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PluginUiTest.Host;

/// <summary>
/// Dalamud's assemblies (Dalamud, Dalamud.Bindings.ImGui, Lumina, Mono.Cecil…) and its native ImGui build (cimgui.dll) are loaded from the
/// user's install at run time, never copied (R05): the default load context asks here for what it cannot find next to the exe. Dalamud's
/// folder comes first (as in the game, where Dalamud's assemblies are shared with every plugin), then the folders the caller names at the
/// time of the request (the plugin copy, the plugin's own folder for its dependencies). Must be installed before any Dalamud type is touched.
/// Moved from src/TenshoAssistant.Desktop/Program.cs (X-120).
/// </summary>
public static class DalamudRuntime
{
    private static bool installed;

    /// <summary>The files a run needs in Dalamud's folder.</summary>
    public static readonly string[] Needed=["Dalamud.dll","Dalamud.Bindings.ImGui.dll","cimgui.dll"];

    /// <summary>Null when the folder holds Dalamud, else the first file that is missing.</summary>
    public static string? Missing(string hooks)=>Needed.FirstOrDefault(file=>!File.Exists(Path.Combine(hooks,file)));

    public static void Install(string hooks,Func<IEnumerable<string?>> extra)
    {
        if(installed)return;
        installed=true;
        IEnumerable<string> Folders()=>new[]{hooks}.Concat(extra()).OfType<string>();
        AssemblyLoadContext.Default.Resolving+=(context,name)=>
        {
            foreach(var dir in Folders())
            {
                var path=Path.Combine(dir,name.Name+".dll");
                if(File.Exists(path))return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll+=(_,name)=>
        {
            var file=name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) ? name : name+".dll";
            foreach(var dir in Folders())
                if(File.Exists(Path.Combine(dir,file)))return NativeLibrary.Load(Path.Combine(dir,file));
            return IntPtr.Zero;
        };
    }
}
