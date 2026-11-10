using System.Security.Cryptography;
using Dalamud.Bindings.ImGui;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PluginUiTest.Host;

/// <summary>
/// Dalamud's font statics (UiBuilder.IconFont, IconFontFixedWidth, DefaultFont, MonoFont) wait for Dalamud's interface service, which never
/// starts outside the game: the first call blocks forever (measured 2026-11-10: InterfaceManager.WhenFontsReady → Service&lt;InterfaceManager&gt;,
/// docs/evidence/x118-desktop-probe.md). A host copies the built plugin to a folder of its own (never the repository, never the installed
/// plugin) and points those calls at the host's font (HostFonts) with Mono.Cecil, read from Dalamud's install, not shipped. Moved from
/// src/TenshoAssistant.Desktop in X-120; the desktop application's use of it is temporary (see its Program.cs), a generic plugin needs it.
/// </summary>
public static class Rewrite
{
    public static readonly Dictionary<string,int> Sites=[];

    /// <summary>
    /// The rewritten copy of the plugin, in a folder of <paramref name="root"/> named after the DLL's content, <paramref name="salt"/> and this library's build
    /// (two runs can go at once, a copy in use is never overwritten; the other folders of root are removed when free). <paramref name="siblings"/>:
    /// the plugin's own files copied beside it (the rest of its folder is found by the caller's resolver). <paramref name="windowSystems"/>:
    /// the plugin's own calls to WindowSystem.Draw (which blocks outside the game: it reads Dalamud's configuration service) go to
    /// HostWindows.Draw (the test tool; the desktop application never runs the plugin's Draw handler).
    /// </summary>
    public static string Plugin(string plugin,string root,string hooks,string salt,IEnumerable<string> siblings,bool windowSystems=false)
    {
        var hash=Convert.ToHexString(SHA256.HashData([..File.ReadAllBytes(plugin),..System.Text.Encoding.UTF8.GetBytes(salt+windowSystems+typeof(HostFonts).Module.ModuleVersionId)]))[..16];
        var folder=Path.Combine(root,hash);
        Directory.CreateDirectory(folder);
        foreach(var old in Directory.GetDirectories(root).Where(d=>!string.Equals(d,folder,StringComparison.OrdinalIgnoreCase)))
            try{Directory.Delete(old,true);}catch(IOException){}catch(UnauthorizedAccessException){}// in use by another run: left for later
        foreach(var sibling in siblings)
        {
            var source=Path.Combine(Path.GetDirectoryName(plugin)!,sibling);
            var copy=Path.Combine(folder,sibling);
            if(File.Exists(source) && !File.Exists(copy))File.Copy(source,copy);
        }
        var target=Path.Combine(folder,Path.GetFileName(plugin));
        var resolver=new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(hooks);resolver.AddSearchDirectory(Path.GetDirectoryName(plugin)!);
        // The plugin's portable .pdb, when beside it, is carried into the copy (an exception's stack then names the plugin's file and line).
        var symbols=File.Exists(Path.ChangeExtension(plugin,".pdb"));
        ModuleDefinition module;
        try{module=ModuleDefinition.ReadModule(plugin,new ReaderParameters{AssemblyResolver=resolver,InMemory=true,ReadSymbols=symbols});}
        catch(Exception) when(symbols){symbols=false;module=ModuleDefinition.ReadModule(plugin,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});}
        using var owned=module;
        var map=new Dictionary<string,MethodReference>
        {
            ["get_IconFont"]=module.ImportReference(typeof(HostFonts).GetProperty(nameof(HostFonts.IconFont))!.GetMethod!),
            ["get_IconFontFixedWidth"]=module.ImportReference(typeof(HostFonts).GetProperty(nameof(HostFonts.IconFont))!.GetMethod!),
            ["get_DefaultFont"]=module.ImportReference(typeof(HostFonts).GetProperty(nameof(HostFonts.DefaultFont))!.GetMethod!),
            ["get_MonoFont"]=module.ImportReference(typeof(HostFonts).GetProperty(nameof(HostFonts.DefaultFont))!.GetMethod!),
        };
        var windowDraw=module.ImportReference(typeof(HostWindows).GetMethod(nameof(HostWindows.Draw))!);
        foreach(var type in module.GetTypes())
            foreach(var method in type.Methods.Where(m=>m.HasBody))
                foreach(var instruction in method.Body.Instructions)
                    if(windowSystems && instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference { Name:"Draw",HasParameters:false } draw
                       && draw.DeclaringType.FullName=="Dalamud.Interface.Windowing.WindowSystem")
                    {
                        // The instance on the stack becomes the static method's argument.
                        instruction.OpCode=OpCodes.Call;instruction.Operand=windowDraw;
                        Sites["WindowSystem.Draw"]=Sites.GetValueOrDefault("WindowSystem.Draw")+1;
                    }
                    else if(instruction.OpCode.Code is Code.Call && instruction.Operand is MethodReference called
                       && called.DeclaringType.FullName is "Dalamud.Interface.UiBuilder" or "Dalamud.Interface.Internal.InterfaceManager"
                       && map.TryGetValue(called.Name,out var replacement))
                    {
                        instruction.Operand=replacement;
                        var key=called.DeclaringType.Name+"."+called.Name.Replace("get_","");
                        Sites[key]=Sites.GetValueOrDefault(key)+1;
                    }
        if(!File.Exists(target))module.Write(target,new WriterParameters{WriteSymbols=symbols});
        return target;
    }
}

/// <summary>The host's fonts: one atlas font (Noto Sans CJK JP Medium with Font Awesome merged, or ImGui's default), standing for Dalamud's.</summary>
public static class HostFonts
{
    public static ImFontPtr Main;
    public static ImFontPtr IconFont=>Main;
    public static ImFontPtr DefaultFont=>Main;
}
