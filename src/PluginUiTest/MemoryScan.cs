using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PluginUiTest.Host;

namespace PluginUiTest;

/// <summary>A path from a plugin entry point to something that can touch game memory.</summary>
public sealed record Finding(string What,string Path);

/// <summary>
/// Safeguard « game memory »: before a part of the plugin runs, its IL call graph (PluginUiTest.Host's CallGraph, Mono.Cecil from Dalamud's
/// install) is walked inside the plugin's own code (its DLL and the DLLs of its folder that are not Dalamud's) from that entry point:
/// the constructor, a UiBuilder handler, a window's DrawConditions, PreDraw, Draw, PostDraw, OnOpen and Update. Flagged: any member of
/// FFXIVClientStructs or InteropGenerator.Runtime; pointer reads and writes (ldind.*, stind.*, ldobj, stobj, cpblk, initblk) in a method
/// that works with pointers (a pointer parameter, local or return, a cast to a pointer, a member taking or giving one), and calli; Marshal.Read*/Write*/Copy/PtrToStructure/StructureToPtr/
/// PtrToString*; Unsafe methods taking pointers; and Dalamud members that hand out raw game addresses (ISigScanner, IGameInteropProvider,
/// IGameGui.GetAddonByName/GetAddonById/FindAgentInterface/GetUIModule, any Address property, any Dalamud member returning a pointer, an
/// IntPtr or a Dalamud.Game.NativeWrapper type). Delegates handed to a Dalamud member (events, commands, IFramework.RunOnFrameworkThread)
/// are not followed: the stand-ins never call them (UiBuilder's handlers are scanned on their own). The result is an upper bound of the
/// followed paths (a branch may never run), not a proof: the child process is the second safeguard.
/// </summary>
internal sealed class MemoryScan:IDisposable
{
    private static readonly HashSet<string> GameAssemblies=["FFXIVClientStructs","InteropGenerator.Runtime"];
    private static readonly HashSet<string> RawAddressTypes=["ISigScanner","SigScanner","IGameInteropProvider"];
    private static readonly HashSet<string> RawAddressMethods=["GetAddonByName","GetAddonById","FindAgentInterface","GetUIModule","GetAgentById"];
    private static readonly HashSet<string> CalledBack=["RunWhenUiPrepared","GetOrCreateData"];
    private static readonly HashSet<Code> PointerCodes=[Code.Ldind_I,Code.Ldind_I1,Code.Ldind_I2,Code.Ldind_I4,Code.Ldind_I8,Code.Ldind_R4,Code.Ldind_R8,Code.Ldind_Ref,
        Code.Ldind_U1,Code.Ldind_U2,Code.Ldind_U4,Code.Stind_I,Code.Stind_I1,Code.Stind_I2,Code.Stind_I4,Code.Stind_I8,Code.Stind_R4,Code.Stind_R8,Code.Stind_Ref,
        Code.Ldobj,Code.Stobj,Code.Cpblk,Code.Initblk];

    private static readonly bool Trace=Environment.GetEnvironmentVariable("PLUGINUITEST_TRACE") is not null;
    private readonly DefaultAssemblyResolver resolver=new();
    private readonly ModuleDefinition module;
    private readonly string folder;
    private readonly HashSet<string> ownAssemblies;
    private readonly Dictionary<MethodDefinition,bool> pointerMethods=[];

    /// <summary>The plugin as it runs: the rewritten copy (its metadata tokens are those of the loaded assembly), its other DLLs in its own folder.</summary>
    public MemoryScan(string copy,string pluginFolder,string hooks)
    {
        folder=Path.GetFullPath(pluginFolder);
        // Dalamud first (as at run time), then the plugin's folder, then .NET's own assemblies (resolved, never walked).
        resolver.AddSearchDirectory(hooks);resolver.AddSearchDirectory(folder);resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
        module=ModuleDefinition.ReadModule(copy,new ReaderParameters{AssemblyResolver=resolver});
        var dalamud=Directory.Exists(hooks) ? Directory.GetFiles(hooks,"*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        ownAssemblies=Directory.GetFiles(folder,"*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(name=>!dalamud.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ownAssemblies.Add(module.Assembly.Name.Name);
    }

    public void Dispose()=>module.Dispose();

    /// <summary>The Cecil method of a reflection method of the plugin (or of a DLL of its folder), or null (Dalamud's, .NET's).</summary>
    public MethodDefinition? Definition(MethodBase method)
    {
        var name=method.Module.Assembly.GetName().Name ?? "";
        if(!ownAssemblies.Contains(name))return null;
        try
        {
            var target=name==module.Assembly.Name.Name ? module : resolver.Resolve(new AssemblyNameReference(name,null)).MainModule;
            return target.LookupToken(method.MetadataToken) as MethodDefinition;
        }
        catch(Exception error){if(Trace)Console.WriteLine($"scan: no IL for {method.Name}: {error.Message}");return null;}
    }

    /// <summary>The first way these entry points can reach game memory (shortest path, breadth first), or null.</summary>
    public Finding? Scan(IEnumerable<MethodDefinition> starts)
    {
        foreach(var start in starts)
        {
            var graph=new CallGraph(Inside)
            {
                MayBeInside=reference=>ownAssemblies.Contains(CallGraph.Scope(reference.DeclaringType)),
                FollowDelegate=instruction=>!HandedToDalamud(instruction),
                Overrides=true,
            };
            Finding? found=null;
            graph.Walk(start,(method,instruction)=>
            {
                if(Flag(method,instruction) is not { } what)return false;
                found=new Finding(what,string.Join(" → ",graph.PathTo(method).Select(Name))+" → "+what);
                return true;
            });
            if(Trace)Console.WriteLine($"scan {Name(start)}: {graph.Parent.Count} methods walked, {(found is null ? "nothing found" : found.Path)}");
            if(found is not null)return found;
        }
        return null;
    }

    private bool Inside(MethodDefinition method)
    {
        if(method.Module==module)return true;
        var file=method.Module.FileName;
        return !string.IsNullOrEmpty(file) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)),folder,StringComparison.OrdinalIgnoreCase)
               && ownAssemblies.Contains(method.Module.Assembly.Name.Name);
    }

    /// <summary>Type.Method, without the namespace, nested types joined by '/'.</summary>
    public static string Name(MethodReference method)
    {
        var type=method.DeclaringType;
        var owner=type.Name.Split('`')[0];
        for(var outer=type.DeclaringType;outer is not null;outer=outer.DeclaringType)owner=outer.Name.Split('`')[0]+"/"+owner;
        return owner+"."+method.Name;
    }

    private static string Member(MemberReference member)
    {
        if(member.DeclaringType is not { } type)return member.Name;
        var owner=type.Name.Split('`')[0];
        for(var outer=type.DeclaringType;outer is not null;outer=outer.DeclaringType)owner=outer.Name.Split('`')[0]+"."+owner;
        return $"{owner}.{member.Name.Replace("get_","").Replace("set_","")} ({type.Namespace ?? type.DeclaringType?.Namespace})";
    }

    /// <summary>What this instruction does to game memory, or null.</summary>
    private string? Flag(MethodDefinition method,Instruction instruction)
    {
        if(instruction.OpCode.Code==Code.Calli)return $"native call through a function pointer (calli) in {Name(method)}";
        if(PointerCodes.Contains(instruction.OpCode.Code) && UsesPointers(method))return $"native pointer {(instruction.OpCode.Code.ToString().StartsWith("St",StringComparison.Ordinal) ? "write" : "read")} ({instruction.OpCode.Name}) in {Name(method)}";
        if(instruction.Operand is not MemberReference member || member.DeclaringType is not { } type)return null;
        var scope=CallGraph.Scope(type);
        if(GameAssemblies.Contains(scope))return $"{scope}: {Member(member)}";
        if(member is not MethodReference called)return null;
        if(type.FullName=="System.Runtime.InteropServices.Marshal"
           && (called.Name.StartsWith("Read",StringComparison.Ordinal) || called.Name.StartsWith("Write",StringComparison.Ordinal) || called.Name.StartsWith("PtrToString",StringComparison.Ordinal)
               || called.Name is "Copy" or "PtrToStructure" or "StructureToPtr"))
            return "Marshal."+called.Name;
        if(type.FullName=="System.Runtime.CompilerServices.Unsafe" && called.Parameters.Any(p=>p.ParameterType.IsPointer))return "Unsafe."+called.Name+" on a pointer";
        if(scope!="Dalamud")return null;
        var typeName=type.Name;
        if(RawAddressTypes.Contains(typeName))return $"Dalamud raw game access: {typeName}.{called.Name}";
        if(RawAddressMethods.Contains(called.Name))return $"Dalamud raw game address: {typeName}.{called.Name}";
        if(called.Name=="get_Address")return $"Dalamud raw game address: {typeName}.Address";
        var returns=called.ReturnType;
        if(returns.IsPointer || returns.FullName is "System.IntPtr" or "System.UIntPtr" || returns.Namespace=="Dalamud.Game.NativeWrapper")
            return $"Dalamud member returning a game address: {typeName}.{called.Name} ({returns.Name})";
        return null;
    }

    /// <summary>A method that works with pointers: a pointer or function pointer parameter, return or local (fixed), a cast to a pointer or
    /// native integer (conv.u, conv.i), or a member taking, returning or holding a pointer.</summary>
    private bool UsesPointers(MethodDefinition method)
    {
        if(pointerMethods.TryGetValue(method,out var known))return known;
        static bool Pointer(TypeReference type)=>type.IsPointer || type.IsFunctionPointer || type is ByReferenceType { ElementType.IsPointer:true } || type is PinnedType;
        var uses=Pointer(method.ReturnType) || method.Parameters.Any(p=>Pointer(p.ParameterType))
                 || method.HasBody && (method.Body.Variables.Any(v=>Pointer(v.VariableType) || v.IsPinned) || method.Body.Instructions.Any(i=>
                     i.OpCode.Code is Code.Conv_U or Code.Conv_I or Code.Conv_Ovf_U or Code.Conv_Ovf_I or Code.Conv_Ovf_U_Un or Code.Conv_Ovf_I_Un
                     || i.Operand is MethodReference m && (Pointer(m.ReturnType) || m.Parameters.Any(p=>Pointer(p.ParameterType)))
                     || i.Operand is FieldReference f && Pointer(f.FieldType)));
        pointerMethods[method]=uses;
        return uses;
    }

    /// <summary>A delegate made here and handed straight to a Dalamud member (an event, a command, the framework): the stand-ins never call it.</summary>
    private static bool HandedToDalamud(Instruction ldftn)
    {
        // ldftn → newobj Delegate::.ctor → (dup, stsfld cache, stloc…) → the member that takes it.
        var at=ldftn.Next;
        if(at?.OpCode.Code!=Code.Newobj)return false;
        at=at.Next;
        for(var i=0;at is not null && i<4;i++,at=at.Next)
        {
            if(at.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && at.Operand is MethodReference consumer)
                return CallGraph.Scope(consumer.DeclaringType)=="Dalamud" && !CalledBack.Contains(consumer.Name);
            if(at.OpCode.Code is not (Code.Dup or Code.Stsfld or Code.Stloc or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 or Code.Stloc_S
               or Code.Ldloc or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc_S))return false;
        }
        return false;
    }
}
