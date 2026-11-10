using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace SampleLibrary;

/// <summary>A window that lives in a library beside the plugin and draws an icon with Dalamud's icon font, as library helpers do.</summary>
public sealed class LibraryWindow():Window("Library window###SampleLibrary")
{
    public override void Draw()
    {
        ImGui.Text("Drawn by a library beside the plugin.");
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.Text(FontAwesomeIcon.Book.ToIconString());
        ImGui.PopFont();
    }
}
