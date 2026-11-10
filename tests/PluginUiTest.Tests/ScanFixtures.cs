using System.Runtime.InteropServices;

// Plugin-shaped code that the game-memory scan (MemoryScan, CallGraph) reads in this test assembly itself. Marshal.ReadInt32 stands for a
// game memory read (the scan flags it like FFXIVClientStructs); none of this code is ever run by the tests, only read as IL.
namespace PluginUiTest.Tests.Fixtures;

/// <summary>Registers a start and a release: the start runs now, the release is kept for Dispose (as Tensho's RegistrationScope).</summary>
public sealed class Scope:IDisposable
{
    private readonly Stack<Action> cleanup=new();
    public void Register(Action subscribe,Action unsubscribe){subscribe();cleanup.Push(unsubscribe);}
    public void Dispose(){while(cleanup.TryPop(out var release))release();}
}

/// <summary>Reads memory in Dispose (a game window given back on unload) and in Read.</summary>
public sealed class Reader(nint address):IDisposable
{
    public int Read()=>Marshal.ReadInt32(address);
    public void Dispose()=>_=Read();
}

public sealed class Quiet:IDisposable
{
    public void Dispose(){}
}

/// <summary>Not flagged: the reading release is only kept for Dispose; a sealed class's Dispose is not every Dispose of the plugin.</summary>
public sealed class KeptForUnload
{
    public KeptForUnload(Scope scope,nint address)
    {
        Reader? reader=null;
        scope.Register(()=>reader=new Reader(address),()=>reader?.Dispose());
        var quiet=new Quiet();
        scope.Register(()=>{},quiet.Dispose);
        quiet.Dispose();
    }
}

/// <summary>Flagged: the start lambda runs now and reads.</summary>
public sealed class StartReads
{
    public StartReads(Scope scope,nint address)
    {
        Reader? reader=null;
        scope.Register(()=>{reader=new Reader(address);_=reader.Read();},()=>{});
    }
}

/// <summary>Flagged: the constructor itself disposes the scope, which calls the kept releases.</summary>
public sealed class DisposesNow
{
    public DisposesNow(nint address)
    {
        var scope=new Scope();
        Reader? reader=null;
        scope.Register(()=>reader=new Reader(address),()=>reader?.Dispose());
        scope.Dispose();
    }
}

/// <summary>Flagged: a delegate kept in a field and called by a method the constructor calls.</summary>
public sealed class KeptThenCalled
{
    private Action? later;
    public KeptThenCalled(nint address)
    {
        later=()=>Marshal.ReadInt32(address);
        Fire();
    }
    private void Fire()=>later?.Invoke();
}

public abstract class Step
{
    public abstract void Run();
}

public sealed class HarmlessStep:Step
{
    public override void Run(){}
}

public sealed class ReadingStep(nint address):Step
{
    public override void Run()=>_=Marshal.ReadInt32(address);
}

/// <summary>Flagged: an abstract call may run any override, ReadingStep's too.</summary>
public sealed class CallsAbstract
{
    public CallsAbstract(Step step)=>step.Run();
}

public interface IGreeter
{
    void Greet();
}

public interface IOther
{
    void Greet();
}

public sealed class PoliteGreeter:IGreeter
{
    public void Greet(){}
}

/// <summary>Same method name and arity, another interface: not an implementation of IGreeter.</summary>
public sealed class ReadingOther(nint address):IOther
{
    public void Greet()=>_=Marshal.ReadInt32(address);
}

/// <summary>Not flagged: only PoliteGreeter implements IGreeter.</summary>
public sealed class CallsInterface
{
    public CallsInterface(IGreeter greeter)=>greeter.Greet();
}
