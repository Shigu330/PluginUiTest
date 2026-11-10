using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace PluginUiTest.Host;

/// <summary>
/// One ImGui context with its font atlas built, for a host that draws a plugin's windows (moved from src/TenshoAssistant.Desktop/Ui.cs in
/// X-120). Fonts: Dalamud's own free fonts from its installed assets when they are there (Noto Sans CJK JP Medium, OFL, with Font Awesome
/// Free Solid, OFL, merged in so FontAwesomeIcon glyphs draw), else ImGui's default font. Nothing is shipped (R05).
/// </summary>
public static unsafe class ImGuiHeadless
{
    private static ushort* textRanges,iconRanges;

    /// <summary>True when the last Start found Dalamud's fonts (false: ImGui's default font, icons draw as '?').</summary>
    public static bool DalamudFonts { get; private set; }

    /// <summary>The context, its fonts (HostFonts.Main) and the atlas: (width, height, RGBA pixels) for a renderer to upload.</summary>
    public static (int W,int H,IntPtr Pixels) Start(float size,string fontFolder)
    {
        ImGui.CreateContext();
        var io=ImGui.GetIO();
        io.IniFilename=null;
        io.DisplaySize=new Vector2(1280,800);
        io.DeltaTime=1/60f;
        io.BackendFlags|=ImGuiBackendFlags.HasMouseCursors;
        var text=Path.Combine(fontFolder,"NotoSansCJKjp-Medium.otf");
        var icons=Path.Combine(fontFolder,"FontAwesomeFreeSolid.otf");
        // Latin with extensions (French: œ, «, …), general punctuation, arrows and symbols; Font Awesome's private use area.
        textRanges=Ranges([0x0020,0x024F,0x2000,0x206F,0x20A0,0x20CF,0x2100,0x21FF,0x2200,0x22FF,0x2460,0x24FF,0x25A0,0x26FF,0x2700,0x27BF,0x3000,0x30FF,0xFF00,0xFFEF]);
        iconRanges=Ranges([0xE000,0xF8FF]);
        DalamudFonts=File.Exists(text);
        HostFonts.Main=DalamudFonts ? io.Fonts.AddFontFromFileTTF(text.AsSpan(),size,default,textRanges) : io.Fonts.AddFontDefault();
        if(DalamudFonts && File.Exists(icons))
        {
            var config=ImGui.ImFontConfig();
            config.MergeMode=true;config.GlyphMinAdvanceX=size;
            io.Fonts.AddFontFromFileTTF(icons.AsSpan(),size*0.85f,config,iconRanges);
        }
        io.Fonts.Build();
        byte* pixels;int w,h;
        io.Fonts.GetTexDataAsRGBA32(0,&pixels,&w,&h);
        return (w,h,(IntPtr)pixels);
    }

    private static ushort* Ranges(int[] values)
    {
        var p=(ushort*)NativeMemory.Alloc((nuint)(values.Length+1),sizeof(ushort));
        for(var i=0;i<values.Length;i++)p[i]=(ushort)values[i];
        p[values.Length]=0;
        return p;
    }

    public static void SetFontTexture(ulong id)=>ImGui.GetIO().Fonts.SetTexID(0,id);

    /// <summary>Dalamud's interface scale, set by its interface manager in the game; 1 here (no DPI scaling).</summary>
    public static void GlobalScale(float scale)
        => typeof(Dalamud.Interface.Utility.ImGuiHelpers).GetProperty("GlobalScale",BindingFlags.Static|BindingFlags.Public)!.GetSetMethod(true)?.Invoke(null,[scale]);
}
