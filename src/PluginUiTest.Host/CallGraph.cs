using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PluginUiTest.Host;

/// <summary>
/// The IL call graph of a plugin, read with Mono.Cecil (from Dalamud's install, not shipped), breadth first from a start method (moved from
/// src/TenshoAssistant.Desktop/Inventory.cs in X-120 and made generic). Followed: calls, constructors, delegates and lambdas (ldftn,
/// ldvirtftn) unless <see cref="FollowDelegate"/> says no, async and iterator state machines (their MoveNext), and, with
/// <see cref="Overrides"/>, the overrides and implementations of a virtual or interface method declared inside: only in the types that
/// derive from its type or implement its interface, and never for a final method or a sealed type (0.2.0: before, a call to one class's
/// Dispose followed every Dispose of the plugin). Only methods for which
/// <see cref="Inside"/> answers true are walked; every instruction of a walked method is shown to the caller's visitor, which can stop the
/// walk. Not followed: delegates stored elsewhere and invoked later, reflection, native code. "Reached" is therefore an upper bound of what
/// the followed paths can run (a branch may never run), not a proof that other code is never run.
/// </summary>
public sealed class CallGraph(Func<MethodDefinition,bool> inside)
{
    public Func<MethodDefinition,bool> Inside=>inside;
    /// <summary>False: the method is not walked (the caller keeps it as a stop). Asked once per method, after Inside.</summary>
    public Func<MethodDefinition,bool> Enter=_=>true;
    /// <summary>False: the delegate made at this ldftn/ldvirtftn (in this method) is not followed (handed to a host that never calls it,
    /// or kept for later by code that never calls it on the walked paths).</summary>
    public Func<MethodDefinition,Instruction,bool> FollowDelegate=(_,_)=>true;
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
                if(instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn && !FollowDelegate(method,instruction))continue;
                if(!MayBeInside(called))continue;
                var definition=Resolve(called);
                if(definition is null || !inside(definition))continue;
                Visit(definition,method);
                if(Overrides && instruction.OpCode.Code is Code.Callvirt or Code.Ldvirtftn && Overridable(definition))
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

    /// <summary>A method another one can stand for at run time: an interface method, or a virtual one that is not final in a type that is not sealed.</summary>
    public static bool Overridable(MethodDefinition method)
        => method.DeclaringType.IsInterface || method.IsVirtual && !method.IsFinal && !method.DeclaringType.IsSealed;

    private IEnumerable<MethodDefinition> Implementations(MethodDefinition method)
    {
        implementations??=method.Module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.IsVirtual && inside(m))
            .GroupBy(Key).ToDictionary(g=>g.Key,g=>g.ToList());
        return (implementations.GetValueOrDefault(Key(method)) ?? []).Where(other=>other!=method && Derives(other.DeclaringType,method.DeclaringType));
    }

    /// <summary>True when type is target, derives from it or implements it (through its base types and the interfaces' own interfaces).</summary>
    public static bool Derives(TypeDefinition type,TypeDefinition target)
    {
        static string Id(TypeDefinition t)=>t.FullName+"|"+t.Module?.Assembly?.Name?.Name;
        var wanted=Id(target);
        var seen=new HashSet<string>();
        var queue=new Queue<TypeReference>([type]);
        while(queue.TryDequeue(out var reference))
        {
            TypeDefinition? at;
            try{at=reference.GetElementType().Resolve();}catch{at=null;}
            if(at is null || !seen.Add(Id(at)))continue;
            if(Id(at)==wanted)return true;
            if(at.BaseType is { } baseType)queue.Enqueue(baseType);
            foreach(var implemented in at.Interfaces)queue.Enqueue(implemented.InterfaceType);
        }
        return false;
    }

    private static string Key(MethodDefinition method)=>method.Name[(method.Name.LastIndexOf('.')+1)..]+"/"+method.Parameters.Count;

    /// <summary>
    /// The instruction that takes the value <paramref name="produced"/> pushes, in the same method, and its place among the values that
    /// instruction pops (0 = the deepest: `this` of an instance call, else the first argument), found by following the evaluation stack
    /// forward; null when it cannot be told (a branch, a return or a throw first, a stack the IL does not give). A `dup` whose copy only
    /// fills a compiler-generated cache field (cached lambdas) and an `stloc` read back at once by the matching `ldloc` are seen through.
    /// </summary>
    public static (Instruction At,int Slot)? Consumer(Instruction produced,MethodDefinition method)
    {
        var above=0;
        for(var at=produced.Next;at is not null;at=at.Next)
        {
            // dup then a null test (cached lambdas, "?? throw"): the value is the same on both paths where they meet, the branch target.
            if(at.OpCode.Code==Code.Dup && at.Next is { OpCode.Code:Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S,Operand:Instruction target }
               && target.Offset>at.Next.Offset)
            {
                at=target.Previous;continue;
            }
            var pops=Pops(at,method);var pushes=Pushes(at);
            if(pops<0 || pushes<0)return null;
            if(pops>above)
            {
                // A copy that only fills a compiler-generated cache: dup, stsfld; or dup, stloc, stfld, ldloc (a lambda cached in its closure).
                if(above==0 && at.OpCode.Code==Code.Dup && at.Next is { OpCode.Code:Code.Stsfld,Operand:FieldReference cache } && cache.Name.Contains('<'))
                {
                    at=at.Next;continue;
                }
                if(above==0 && at.OpCode.Code==Code.Dup && at.Next is { } keep && Local(keep,method,store:true) is { } temporary
                   && keep.Next is { OpCode.Code:Code.Stfld,Operand:FieldReference closureCache } && closureCache.Name.Contains('<')
                   && keep.Next.Next is { } back && Local(back,method,store:false)==temporary)
                {
                    at=back;continue;
                }
                if(above==0 && Local(at,method,store:true) is { } stored && at.Next is { } next && Local(next,method,store:false)==stored)
                {
                    at=next;continue;
                }
                return (at,pops-1-above);
            }
            if(at.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)return null;
            above+=pushes-pops;
        }
        return null;
    }

    /// <summary>The local variable an stloc writes (store) or an ldloc reads, or null.</summary>
    private static VariableDefinition? Local(Instruction instruction,MethodDefinition method,bool store)
    {
        var variables=method.Body.Variables;
        VariableDefinition? At(int index)=>index<variables.Count ? variables[index] : null;
        return (instruction.OpCode.Code,store) switch
        {
            (Code.Stloc_0,true) or (Code.Ldloc_0,false)=>At(0),
            (Code.Stloc_1,true) or (Code.Ldloc_1,false)=>At(1),
            (Code.Stloc_2,true) or (Code.Ldloc_2,false)=>At(2),
            (Code.Stloc_3,true) or (Code.Ldloc_3,false)=>At(3),
            (Code.Stloc or Code.Stloc_S,true) or (Code.Ldloc or Code.Ldloc_S,false)=>instruction.Operand as VariableDefinition,
            _=>null,
        };
    }

    /// <summary>How many values an instruction pops (-1: unknown).</summary>
    public static int Pops(Instruction instruction,MethodDefinition method)
    {
        var code=instruction.OpCode;
        switch(code.StackBehaviourPop)
        {
            case StackBehaviour.Pop0:return 0;
            case StackBehaviour.PopAll:return -1;
            case StackBehaviour.Varpop:
                if(code.Code==Code.Ret)return method.ReturnType.MetadataType==MetadataType.Void ? 0 : 1;
                if(instruction.Operand is not MethodReference called || code.Code==Code.Calli)return -1;
                return called.Parameters.Count+(called.HasThis && code.Code!=Code.Newobj ? 1 : 0);
            default:return code.StackBehaviourPop.ToString().Split('_').Length;
        }
    }

    /// <summary>How many values an instruction pushes (-1: unknown).</summary>
    public static int Pushes(Instruction instruction)
    {
        var code=instruction.OpCode;
        switch(code.StackBehaviourPush)
        {
            case StackBehaviour.Push0:return 0;
            case StackBehaviour.Push1_push1:return 2;
            case StackBehaviour.Varpush:
                if(instruction.Operand is not MethodReference called || code.Code==Code.Calli)return -1;
                return code.Code==Code.Newobj || called.ReturnType.MetadataType!=MetadataType.Void ? 1 : 0;
            default:return 1;
        }
    }

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
