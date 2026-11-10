using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PluginUiTest.Host;

/// <summary>
/// The IL call graph of a plugin, read with Mono.Cecil (from Dalamud's install, not shipped), breadth first from a start method (moved from
/// src/TenshoAssistant.Desktop/Inventory.cs in X-120 and made generic). Followed: calls, constructors, delegates and lambdas (ldftn,
/// ldvirtftn) unless <see cref="FollowDelegate"/> says no, async and iterator state machines (their MoveNext), and, with
/// <see cref="Overrides"/>, the overrides and implementations of a virtual or interface method declared inside. Only methods for which
/// <see cref="Inside"/> answers true are walked; every instruction of a walked method is shown to the caller's visitor, which can stop the
/// walk. Not followed: delegates stored elsewhere and invoked later, reflection, native code. "Reached" is therefore an upper bound of what
/// the followed paths can run (a branch may never run), not a proof that other code is never run.
/// </summary>
public sealed class CallGraph(Func<MethodDefinition,bool> inside)
{
    public Func<MethodDefinition,bool> Inside=>inside;
    /// <summary>False: the method is not walked (the caller keeps it as a stop). Asked once per method, after Inside.</summary>
    public Func<MethodDefinition,bool> Enter=_=>true;
    /// <summary>False: the delegate made at this ldftn/ldvirtftn is not followed (handed to a host that never calls it).</summary>
    public Func<Instruction,bool> FollowDelegate=_=>true;
    /// <summary>A cheap filter before resolving a reference (resolving a framework type costs a file lookup): false, not followed.</summary>
    public Func<MethodReference,bool> MayBeInside=_=>true;
    /// <summary>Follow the overrides and implementations (same name and parameter count, inside) of a virtual or interface method called inside.</summary>
    public bool Overrides;
    /// <summary>Who led to each walked method (null for the start): the path of a finding.</summary>
    public readonly Dictionary<MethodDefinition,MethodDefinition?> Parent=[];
    private Dictionary<string,List<MethodDefinition>>? implementations;

    /// <summary>Walks from start; visitor(method, instruction) returns true to stop. The number of methods walked.</summary>
    public int Walk(MethodDefinition start,Func<MethodDefinition,Instruction,bool>? visitor=null)
    {
        var queue=new Queue<MethodDefinition>();
        var seen=new HashSet<MethodDefinition>();
        void Visit(MethodDefinition? method,MethodDefinition? from)
        {
            if(method is null || seen.Contains(method) || !inside(method))return;
            seen.Add(method);
            if(!Enter(method))return;
            Parent.TryAdd(method,from);
            queue.Enqueue(method);
        }
        Visit(start,null);
        var walked=0;
        while(queue.TryDequeue(out var method))
        {
            walked++;
            // Async and iterator methods: their body is the state machine's MoveNext.
            foreach(var attribute in method.CustomAttributes.Where(a=>a.AttributeType.Name is "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute"))
                if(attribute.ConstructorArguments[0].Value is TypeReference machine)Visit(machine.Resolve()?.Methods.FirstOrDefault(m=>m.Name=="MoveNext"),method);
            if(!method.HasBody)continue;
            foreach(var instruction in method.Body.Instructions)
            {
                if(visitor?.Invoke(method,instruction)==true)return walked;
                if(instruction.Operand is not MethodReference called)continue;
                if(instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn && !FollowDelegate(instruction))continue;
                if(!MayBeInside(called))continue;
                var definition=Resolve(called);
                if(definition is null || !inside(definition))continue;
                Visit(definition,method);
                if(Overrides && instruction.OpCode.Code is Code.Callvirt or Code.Ldvirtftn && (definition.IsVirtual || definition.DeclaringType.IsInterface))
                    foreach(var other in Implementations(definition))Visit(other,method);
            }
        }
        return walked;
    }

    /// <summary>The methods from the start to this one, start first.</summary>
    public List<MethodDefinition> PathTo(MethodDefinition method)
    {
        var path=new List<MethodDefinition>();
        for(MethodDefinition? at=method;at is not null && path.Count<64;at=Parent.GetValueOrDefault(at))path.Add(at);
        path.Reverse();
        return path;
    }

    private IEnumerable<MethodDefinition> Implementations(MethodDefinition method)
    {
        implementations??=method.Module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.IsVirtual && inside(m))
            .GroupBy(Key).ToDictionary(g=>g.Key,g=>g.ToList());
        return implementations.GetValueOrDefault(Key(method)) ?? [];
    }

    private static string Key(MethodDefinition method)=>method.Name[(method.Name.LastIndexOf('.')+1)..]+"/"+method.Parameters.Count;

    public static MethodDefinition? Resolve(MethodReference method)
    {
        try{return method.Resolve();}catch{return null;}
    }

    public static ModuleDefinition? Owner(TypeReference type)
    {
        try{return type.Resolve()?.Module;}catch{return null;}
    }

    /// <summary>The assembly a member belongs to, by name ("Dalamud", "FFXIVClientStructs", "System.Runtime"…).</summary>
    public static string Scope(TypeReference type)
    {
        while(type.IsNested)type=type.DeclaringType;
        if(type is TypeSpecification specification)return Scope(specification.ElementType);
        return (type.Scope as AssemblyNameReference)?.Name ?? type.Module?.Assembly?.Name?.Name ?? "";
    }

    /// <summary>A short name: Type.Method (nested types with '/', generics without arity).</summary>
    public static string Short(MemberReference member)
        => (member.DeclaringType is { } type ? type.Name.Split('`')[0]+"." : "")+member.Name.Replace("get_","").Replace("set_","");
}
