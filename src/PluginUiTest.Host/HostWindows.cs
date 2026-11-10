using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PluginUiTest.Host;

/// <summary>
/// A plugin window drawn the way Dalamud's WindowSystem draws it, without Dalamud's services (X-120): WindowSystem.Draw reads Dalamud's
/// configuration and window persistence services, which never start outside the game, and blocks (measured on Tensho's own Draw handler).
/// One frame of one window: Update, DrawConditions, PreDraw, the window's position, size, constraints, collapse and background alpha,
/// Begin, Draw, End, PostDraw. The caller makes the frame (NewFrame, error recovery, Render) and catches the plugin's exception. A plugin
/// copy rewritten with <see cref="Rewrite"/>'s windowSystems option calls <see cref="Draw"/> instead of WindowSystem.Draw.
/// </summary>
public static class HostWindows
{
    /// <summary>The exceptions the rewritten WindowSystem.Draw caught, per window (Dalamud too keeps one window's failure from the others).</summary>
    public static readonly List<(string Window,Exception Error)> Errors=[];

    /// <summary>One frame of this window; false when DrawConditions answered false (nothing drawn). The plugin's exceptions go to the caller.</summary>
    public static bool DrawOnce(IWindow window)
    {
        window.Update();
        if(!window.DrawConditions())return false;
        try
        {
            window.PreDraw();
            if(window.Position is { } position)ImGui.SetNextWindowPos(position,window.PositionCondition);
            if(window.Size is { } size)ImGui.SetNextWindowSize(size,window.SizeCondition);
            if(window.SizeConstraints is { } constraints)ImGui.SetNextWindowSizeConstraints(constraints.MinimumSize,constraints.MaximumSize);
            if(window.Collapsed is { } collapsed)ImGui.SetNextWindowCollapsed(collapsed,window.CollapsedCondition);
            if(window.BgAlpha is { } alpha)ImGui.SetNextWindowBgAlpha(alpha);
            var open=true;
            var shown=window.ShowCloseButton ? ImGui.Begin(window.WindowName,ref open,window.Flags) : ImGui.Begin(window.WindowName,window.Flags);
            try{if(shown)window.Draw();}
            finally{ImGui.End();}
        }
        finally{window.PostDraw();}
        return true;
    }

    /// <summary>The rewritten target of WindowSystem.Draw: each open window once, a failure recorded and the next window drawn.</summary>
    public static void Draw(WindowSystem system)
    {
        foreach(var window in system.Windows.ToArray())
        {
            if(!window.IsOpen)continue;
            try{DrawOnce(window);}
            catch(Exception error){lock(Errors)Errors.Add((window.WindowName,error));}
        }
    }
}
