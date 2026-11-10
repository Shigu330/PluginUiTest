using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SamplePlugin;

/// <summary>
/// The sample PluginUiTest runs (X-120): a WindowSystem of three windows. "Fine window" draws text, a button and an icon (and asks one web
/// address once when opened, to show the network guard); "Throwing window" throws in Draw; "Memory reader" reads a game window's state
/// through IGameGui and FFXIVClientStructs in Draw, without checking the address, which outside the game is zero. Expected report: drawn,
/// threw, skipped (game memory). With --run-anyway="Memory reader" it runs in the child process: the read near address zero becomes a
/// NullReferenceException in .NET (measured), so it is reported as threw; a read at another bad address would end that process, and the
/// parent would report it as crashed and draw the other windows again.
/// </summary>
public sealed class Plugin:IDalamudPlugin
{
    [PluginService] internal static IPluginLog Log { get; private set; }=null!;
    [PluginService] internal static IGameGui GameGui { get; private set; }=null!;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows=new("SamplePlugin");

    public Plugin(IDalamudPluginInterface pluginInterface,ICommandManager commands)
    {
        this.pluginInterface=pluginInterface;
        this.commands=commands;
        windows.AddWindow(new FineWindow());
        windows.AddWindow(new ThrowingWindow());
        windows.AddWindow(new MemoryReader());
        commands.AddHandler("/sampleui",new CommandInfo((_,_)=>ToggleAll()){HelpMessage="Opens the sample's windows."});
        pluginInterface.UiBuilder.Draw+=windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi+=ToggleAll;
        Log.Information("Sample plugin created");
    }

    private void ToggleAll()
    {
        foreach(var window in windows.Windows)window.Toggle();
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw-=windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi-=ToggleAll;
        commands.RemoveHandler("/sampleui");
        windows.RemoveAllWindows();
    }
}

/// <summary>Draws without trouble; its OnOpen asks example.com once (PluginUiTest blocks and reports it).</summary>
public sealed class FineWindow():Window("Fine window")
{
    private int clicks;

    public override void OnOpen()
    {
        _=Task.Run(async ()=>
        {
            try
            {
                using var http=new HttpClient();
                await http.GetStringAsync("https://example.com/");
            }
            catch(Exception error){Plugin.Log.Warning($"Fine window: request failed: {error.Message}");}
        });
    }

    public override void Draw()
    {
        ImGui.Text("Everything is fine here.");
        if(ImGui.Button($"Clicked {clicks} times"))clicks++;
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.Text(FontAwesomeIcon.Check.ToIconString());
        ImGui.PopFont();
    }
}

/// <summary>Throws in Draw, as a window with a bug would.</summary>
public sealed class ThrowingWindow():Window("Throwing window")
{
    public override void Draw()
    {
        ImGui.Text("This line is drawn, then the window throws.");
        throw new InvalidOperationException("Sample failure: this window throws in Draw on purpose.");
    }
}

/// <summary>Reads game memory in Draw: the duty list's visibility through its raw address, with no null check (a bug outside the game).</summary>
public sealed unsafe class MemoryReader():Window("Memory reader")
{
    public override void Draw()
    {
        var addon=(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)Plugin.GameGui.GetAddonByName("_ToDoList").Address;
        ImGui.Text($"Duty list visible: {addon->IsVisible}");
    }
}
