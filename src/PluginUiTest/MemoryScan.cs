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
/// are not followed: the stand-ins never call them (UiBuilder's handlers are scanned on their own). Since 0.2.0, a delegate made on the
/// path and only kept in a field of the plugin (stored, or added to a collection held in a field, directly or by the plugin's own methods,
/// e.g. RegistrationScope.Register(subscribe, unsubscribe) keeping unsubscribe for Dispose) is followed only when a walked method reads that
/// field otherwise than to add to it (it may call it then): the scan is walked again with it. The result is an upper bound of the
/// followed paths (a branch may never run), not a proof: the child process is the second safeguard.
/// </summary>
internal sealed class MemoryScan:IDisposable
{
    private static readonly HashSet<string> GameAssemblies=["FFXIVClientStructs","InteropGenerator.Runtime"];
    private static readonly HashSet<string> RawAddressTypes=["ISigScanner","SigScanner","IGameInteropProvider"];
    private static readonly HashSet<string> RawAddressMethods=["GetAddonByName","GetAddonById","FindAgentInterface","GetUIModule","GetAgentById"];
    private static readonly HashSet<string> CalledBack=["RunWhenUiPrepared","GetOrCreateData"];
    /// <summary>Collection members that keep a value given to them without calling it.</summary>
    private static readonly HashSet<string> Storers=["Add","Push","Enqueue","Insert","AddLast","AddFirst","TryAdd","set_Item"];
    /// <summary>Collection members that only compare a value given to them (a delegate removed or looked for is not called).</summary>
    private static readonly HashSet<string> Lookups=["Remove","Contains","ContainsKey","ContainsValue","IndexOf"];
    /// <summary>Collection members that hand out none of what the collection holds: reading a field to call one of them calls no delegate.</summary>
    private static readonly HashSet<string> SafeReads=[..Storers,"get_Count","Clear","Contains","ContainsKey"];
    private static readonly HashSet<Code> PointerCodes=[Code.Ldind_I,Code.Ldind_I1,Code.Ldind_I2,Code.Ldind_I4,Code.Ldind_I8,Code.Ldind_R4,Code.Ldind_R8,Code.Ldind_Ref,
        Code.Ldind_U1,Code.Ldind_U2,Code.Ldind_U4,Code.Stind_I,Code.Stind_I1,Code.Stind_I2,Code.Stind_I4,Code.Stind_I8,Code.Stind_R4,Code.Stind_R8,Code.Stind_Ref,
        Code.Ldobj,Code.Stobj,Code.Cpblk,Code.Initblk];

    private static readonly bool Trace=Environment.GetEnvironmentVariable("PLUGINUITEST_TRACE") is not null;
    private readonly DefaultAssemblyResolver resolver=new();
    private readonly ModuleDefinition module;
    private readonly string folder;
    private readonly string? copyFolder;
    private readonly HashSet<string> ownAssemblies;
    private readonly Dictionary<MethodDefinition,bool> pointerMethods=[];

    /// <summary>The plugin as it runs: the rewritten copy (its metadata tokens are those of the loaded assembly), its other DLLs rewritten
    /// beside the copy (the libraries that use Dalamud, since 0.2.0) or in its own folder.</summary>
    public MemoryScan(string copy,string pluginFolder,string hooks)
    {
        folder=Path.GetFullPath(pluginFolder);
        copyFolder=Path.GetDirectoryName(Path.GetFullPath(copy));
        if(string.Equals(copyFolder,folder,StringComparison.OrdinalIgnoreCase))copyFolder=null;
        // Dalamud first (as at run time), then the copies, then the plugin's folder, then .NET's own assemblies (resolved, never walked).
        resolver.AddSearchDirectory(hooks);
        if(copyFolder is not null)resolver.AddSearchDirectory(copyFolder);
        resolver.AddSearchDirectory(folder);resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
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
            // Fields whose kept delegates a walked method may call: those delegates are followed on the next walk.
            var called=new HashSet<string>();
            for(var walk=0;walk<4;walk++)
            {
                var kept=new HashSet<string>();
                var graph=new CallGraph(Inside)
                {
                    MayBeInside=reference=>ownAssemblies.Contains(CallGraph.Scope(reference.DeclaringType)),
                    FollowDelegate=(method,instruction)=>Follow(method,instruction,called,kept),
                    Overrides=true,
                };
                Finding? found=null;
                graph.Walk(start,(method,instruction)=>
                {
                    if(Flag(method,instruction) is not { } what)return false;
                    found=new Finding(what,string.Join(" → ",graph.PathTo(method).Select(Name))+" → "+what);
                    return true;
                });
                if(Trace)Console.WriteLine($"scan {Name(start)} (walk {walk+1}): {graph.Parent.Count} methods walked, {kept.Count} fields of kept delegates, {(found is null ? "nothing found" : found.Path)}");
                if(found is not null)return found;
                var reread=kept.Where(field=>!called.Contains(field) && graph.Parent.Keys.Any(method=>ReadsToCall(method,field))).ToList();
                if(reread.Count==0)break;
                called.UnionWith(reread);
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the delegate made at this ldftn is followed: not when it is handed to Dalamud (unless Dalamud calls it back at once), nor
    /// when it is only kept in fields of the plugin that no walked method reads to call (those fields go to kept).
    /// </summary>
    private bool Follow(MethodDefinition method,Instruction ldftn,HashSet<string> called,HashSet<string> kept)
    {
        if(ldftn.Next is not { OpCode.Code:Code.Newobj } make || CallGraph.Consumer(make,method) is not { } use)return true;
        HashSet<string>? fields;
        if((use.At.OpCode.Code==Code.Stfld && use.Slot==1 || use.At.OpCode.Code==Code.Stsfld) && use.At.Operand is FieldReference field && Own(field))fields=[Key(field)];
        else if(use.At.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && use.At.Operand is MethodReference consumer)
        {
            if(CallGraph.Scope(consumer.DeclaringType)=="Dalamud")return CalledBack.Contains(consumer.Name);
            var argument=use.Slot-(consumer.HasThis && use.At.OpCode.Code!=Code.Newobj ? 1 : 0);
            if(argument<0 || CallGraph.Resolve(consumer) is not { } callee || !Inside(callee))return true;
            fields=KeptBy(callee,argument,0);
        }
        else return true;
        if(fields is null || fields.Any(called.Contains))return true;
        kept.UnionWith(fields);
        return false;
    }

    /// <summary>The fields where this method only keeps its argument (a delegate), through the plugin's own calls; null when it may call it
    /// or hand it elsewhere.</summary>
    private HashSet<string>? KeptBy(MethodDefinition method,int argument,int depth)
    {
        if(depth>4 || !method.HasBody)return null;
        var index=argument+(method.HasThis ? 1 : 0);
        var fields=new HashSet<string>();
        foreach(var instruction in method.Body.Instructions)
        {
            if(Argument(instruction,method) is not { } at || at.Index!=index)continue;
            if(!at.Load)return null;// its address taken, or written over
            if(CallGraph.Consumer(instruction,method) is not { } use)return null;
            switch(use.At.OpCode.Code)
            {
                case Code.Stfld when use.Slot==1 && use.At.Operand is FieldReference field && Own(field):
                case Code.Stsfld when use.At.Operand is FieldReference staticField && Own(staticField):
                    fields.Add(Key((FieldReference)use.At.Operand));
                    break;
                case Code.Call or Code.Callvirt or Code.Newobj when use.At.Operand is MethodReference called:
                    if(use.Slot>=1 && use.At.OpCode.Code!=Code.Newobj && Collection(called,Lookups))break;
                    if(use.Slot>=1 && use.At.OpCode.Code!=Code.Newobj && Collection(called,Storers))
                    {
                        // The collection it is added to: a field read in this method straight into this call's receiver.
                        var holder=method.Body.Instructions.FirstOrDefault(i=>i.OpCode.Code is Code.Ldfld or Code.Ldsfld && i.Operand is FieldReference f && Own(f)
                            && CallGraph.Consumer(i,method) is { } taken && taken.At==use.At && taken.Slot==0);
                        if(holder is null)return null;
                        fields.Add(Key((FieldReference)holder.Operand));
                        break;
                    }
                    // Handed on to Dalamud (an event, a command, the framework): the stand-ins never call it, unless Dalamud calls it back at once.
                    if(CallGraph.Scope(called.DeclaringType)=="Dalamud")
                    {
                        if(CalledBack.Contains(called.Name))return null;
                        break;
                    }
                    var parameter=use.Slot-(called.HasThis && use.At.OpCode.Code!=Code.Newobj ? 1 : 0);
                    if(parameter<0 || CallGraph.Resolve(called) is not { } callee || !Inside(callee) || KeptBy(callee,parameter,depth+1) is not { } deeper)return null;
                    fields.UnionWith(deeper);
                    break;
                default:return null;
            }
        }
        return fields;
    }

    /// <summary>True when this method reads the field otherwise than to add to the collection it holds (it may then call what it keeps).</summary>
    private static bool ReadsToCall(MethodDefinition method,string field)
    {
        if(!method.HasBody)return false;
        foreach(var instruction in method.Body.Instructions)
        {
            if(instruction.OpCode.Code is not (Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda) || instruction.Operand is not FieldReference read || Key(read)!=field)continue;
            if(instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld && CallGraph.Consumer(instruction,method) is { Slot:0,At: { OpCode.Code:Code.Call or Code.Callvirt,Operand:MethodReference keeper } } && Collection(keeper,SafeReads))continue;
            return true;
        }
        return false;
    }

    private static bool Collection(MethodReference method,HashSet<string> names)
        => names.Contains(method.Name) && (method.DeclaringType.Namespace ?? "").StartsWith("System.Collections",StringComparison.Ordinal);

    private bool Own(FieldReference field)=>ownAssemblies.Contains(CallGraph.Scope(field.DeclaringType));

    private static string Key(FieldReference field)=>field.DeclaringType.GetElementType().FullName+"::"+field.Name;

    /// <summary>The argument an ldarg, ldarga or starg names (index counting `this`), and whether it only loads it.</summary>
    private static (int Index,bool Load)? Argument(Instruction instruction,MethodDefinition method)
    {
        int Of(object? operand)=>operand is ParameterDefinition parameter ? parameter.Index+(method.HasThis ? 1 : 0) : -1;
        return instruction.OpCode.Code switch
        {
            Code.Ldarg_0=>(0,true),Code.Ldarg_1=>(1,true),Code.Ldarg_2=>(2,true),Code.Ldarg_3=>(3,true),
            Code.Ldarg or Code.Ldarg_S=>(Of(instruction.Operand),true),
            Code.Ldarga or Code.Ldarga_S or Code.Starg or Code.Starg_S=>(Of(instruction.Operand),false),
            _=>null,
        };
    }

    private bool Inside(MethodDefinition method)
    {
        if(method.Module==module)return true;
        var file=method.Module.FileName;
        if(string.IsNullOrEmpty(file))return false;
        var directory=Path.GetDirectoryName(Path.GetFullPath(file));
        return (string.Equals(directory,folder,StringComparison.OrdinalIgnoreCase) || string.Equals(directory,copyFolder,StringComparison.OrdinalIgnoreCase))
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
}
