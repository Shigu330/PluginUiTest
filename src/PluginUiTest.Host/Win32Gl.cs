using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace PluginUiTest.Host;

/// <summary>
/// A plain Win32 window with an OpenGL 1.1 context (opengl32.dll, gdi32.dll and user32.dll ship with Windows: no package, no download)
/// and an ImGui renderer in the fixed pipeline, as Dear ImGui's own OpenGL 2 example backend does (written here, not copied): textured
/// triangles with a scissor per draw command. Input: mouse, wheel, characters and the editing keys, enough for the plugin's pages.
/// Moved from src/TenshoAssistant.Desktop in X-120 (generic: no plugin code in it).
/// </summary>
public sealed unsafe class Win32Gl:IDisposable
{
    public IntPtr Hwnd { get; private set; }
    public bool Closed { get; private set; }
    public Vector2 Size { get; private set; }
    private IntPtr dc,glrc;
    private readonly WndProc proc;

    public Win32Gl(string title,int width,int height,bool visible)
    {
        proc=Procedure;
        var instance=GetModuleHandleW(null);
        var name="PluginUiTestHostWindow";
        var cls=new WNDCLASSEXW{cbSize=(uint)Marshal.SizeOf<WNDCLASSEXW>(),style=0x20 /*CS_OWNDC*/,lpfnWndProc=Marshal.GetFunctionPointerForDelegate(proc),hInstance=instance,
            hCursor=LoadCursorW(IntPtr.Zero,32512),lpszClassName=name};
        if(RegisterClassExW(ref cls)==0 && Marshal.GetLastWin32Error()!=1410)throw new InvalidOperationException("RegisterClassEx failed");
        Hwnd=CreateWindowExW(0,name,title,0x00CF0000 /*WS_OVERLAPPEDWINDOW*/,100,100,width,height,IntPtr.Zero,IntPtr.Zero,instance,IntPtr.Zero);
        if(Hwnd==IntPtr.Zero)throw new InvalidOperationException("CreateWindowEx failed");
        dc=GetDC(Hwnd);
        var pfd=new PIXELFORMATDESCRIPTOR{nSize=(ushort)sizeof(PIXELFORMATDESCRIPTOR),nVersion=1,dwFlags=0x4|0x20|0x1 /*DRAW_TO_WINDOW|SUPPORT_OPENGL|DOUBLEBUFFER*/,
            iPixelType=0,cColorBits=32,cAlphaBits=8,cDepthBits=0,iLayerType=0};
        var format=ChoosePixelFormat(dc,ref pfd);
        if(format==0 || !SetPixelFormat(dc,format,ref pfd))throw new InvalidOperationException("no OpenGL pixel format");
        glrc=wglCreateContext(dc);
        if(glrc==IntPtr.Zero || !wglMakeCurrent(dc,glrc))throw new InvalidOperationException("wglCreateContext failed");
        ShowWindow(Hwnd,visible ? 5:0);
        UpdateSize();
    }

    public string GlInfo=>$"{Marshal.PtrToStringAnsi(glGetString(0x1F00))} | {Marshal.PtrToStringAnsi(glGetString(0x1F01))} | OpenGL {Marshal.PtrToStringAnsi(glGetString(0x1F02))}";

    private void UpdateSize(){GetClientRect(Hwnd,out var r);Size=new(Math.Max(1,r.Right-r.Left),Math.Max(1,r.Bottom-r.Top));}

    /// <summary>Messages of the frame, forwarded to ImGui's input queue.</summary>
    public void Pump()
    {
        while(PeekMessageW(out var msg,IntPtr.Zero,0,0,1))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UpdateSize();
    }

    private IntPtr Procedure(IntPtr hwnd,uint message,IntPtr w,IntPtr l)
    {
        if(ImGui.GetCurrentContext().IsNull)return DefWindowProcW(hwnd,message,w,l);
        var io=ImGui.GetIO();
        switch(message)
        {
            case 0x0200:io.AddMousePosEvent((short)(l.ToInt64()&0xFFFF),(short)((l.ToInt64()>>16)&0xFFFF));return 0;// WM_MOUSEMOVE
            case 0x0201:io.AddMouseButtonEvent(0,true);SetCapture(hwnd);return 0;
            case 0x0202:io.AddMouseButtonEvent(0,false);ReleaseCapture();return 0;
            case 0x0204:io.AddMouseButtonEvent(1,true);return 0;
            case 0x0205:io.AddMouseButtonEvent(1,false);return 0;
            case 0x020A:io.AddMouseWheelEvent(0,(short)((w.ToInt64()>>16)&0xFFFF)/120f);return 0;
            case 0x0102:var c=(char)w.ToInt64();if(c>=32)io.AddInputCharacter(c);return 0;// WM_CHAR
            case 0x0100:case 0x0101:// WM_KEYDOWN, WM_KEYUP
                var down=message==0x0100;
                io.AddKeyEvent(ImGuiKey.ModCtrl,(GetKeyState(0x11)&0x8000)!=0);io.AddKeyEvent(ImGuiKey.ModShift,(GetKeyState(0x10)&0x8000)!=0);
                if(Key((int)w.ToInt64()) is { } key)io.AddKeyEvent(key,down);
                return 0;
            case 0x0010:Closed=true;return 0;// WM_CLOSE
        }
        return DefWindowProcW(hwnd,message,w,l);
    }

    private static ImGuiKey? Key(int vk)=>vk switch
    {
        0x08=>ImGuiKey.Backspace,0x09=>ImGuiKey.Tab,0x0D=>ImGuiKey.Enter,0x1B=>ImGuiKey.Escape,0x2E=>ImGuiKey.Delete,0x24=>ImGuiKey.Home,0x23=>ImGuiKey.End,
        0x25=>ImGuiKey.LeftArrow,0x27=>ImGuiKey.RightArrow,0x26=>ImGuiKey.UpArrow,0x28=>ImGuiKey.DownArrow,0x41=>ImGuiKey.A,0x43=>ImGuiKey.C,0x56=>ImGuiKey.V,0x58=>ImGuiKey.X,
        _=>null,
    };

    /// <summary>An RGBA or BGRA texture for ImGui; its OpenGL name is the ImTextureID.</summary>
    public static uint Texture(int width,int height,byte* pixels,bool bgra)
    {
        uint id;glGenTextures(1,&id);
        glBindTexture(0x0DE1,id);
        glTexParameteri(0x0DE1,0x2801,0x2601);glTexParameteri(0x0DE1,0x2800,0x2601);// MIN/MAG = LINEAR
        glPixelStorei(0x0CF5,1);
        glTexImage2D(0x0DE1,0,0x1908 /*RGBA*/,width,height,0,bgra ? 0x80E1u:0x1908u,0x1401 /*UNSIGNED_BYTE*/,pixels);
        return id;
    }

    public void Render(ImDrawDataPtr data,Vector4 clear)
    {
        var w=(int)Size.X;var h=(int)Size.Y;
        glViewport(0,0,w,h);glClearColor(clear.X,clear.Y,clear.Z,clear.W);glClear(0x4000);
        glEnable(0x0BE2);glBlendFunc(0x0302,0x0303);// BLEND, SRC_ALPHA, ONE_MINUS_SRC_ALPHA
        glDisable(0x0B44);glDisable(0x0B71);glDisable(0x0B50);glEnable(0x0C11);glEnable(0x0DE1);// no cull, depth, lighting; scissor, texture
        glEnableClientState(0x8074);glEnableClientState(0x8078);glEnableClientState(0x8076);// vertex, texcoord, color arrays
        glMatrixMode(0x1701);glLoadIdentity();
        var pos=data.DisplayPos;var size=data.DisplaySize;
        glOrtho(pos.X,pos.X+size.X,pos.Y+size.Y,pos.Y,-1,1);
        glMatrixMode(0x1700);glLoadIdentity();
        for(var n=0;n<data.CmdListsCount;n++)
        {
            var list=new ImDrawListPtr(data.CmdLists[n]);
            var vtx=(byte*)list.VtxBuffer.Data;var idx=(ushort*)list.IdxBuffer.Data;
            glVertexPointer(2,0x1406,sizeof(ImDrawVert),vtx);
            glTexCoordPointer(2,0x1406,sizeof(ImDrawVert),vtx+8);
            glColorPointer(4,0x1401,sizeof(ImDrawVert),vtx+16);
            for(var c=0;c<list.CmdBuffer.Size;c++)
            {
                var cmd=list.CmdBuffer.Data[c];
                if(cmd.UserCallback!=null)continue;
                var clip=new Vector4(cmd.ClipRect.X-pos.X,cmd.ClipRect.Y-pos.Y,cmd.ClipRect.Z-pos.X,cmd.ClipRect.W-pos.Y);
                if(clip.Z<=clip.X || clip.W<=clip.Y)continue;
                glScissor((int)clip.X,(int)(h-clip.W),(int)(clip.Z-clip.X),(int)(clip.W-clip.Y));
                glBindTexture(0x0DE1,(uint)cmd.TextureId.Handle);
                glDrawElements(0x0004,(int)cmd.ElemCount,0x1403,idx+cmd.IdxOffset);
            }
        }
        glDisableClientState(0x8074);glDisableClientState(0x8078);glDisableClientState(0x8076);glDisable(0x0C11);
    }

    /// <summary>The back buffer as RGBA rows, top row first (read before SwapBuffers).</summary>
    public byte[] Capture()
    {
        var w=(int)Size.X;var h=(int)Size.Y;var pixels=new byte[w*h*4];
        fixed(byte* p=pixels){glPixelStorei(0x0D05,1);glReadBuffer(0x0405);glReadPixels(0,0,w,h,0x1908,0x1401,p);}
        var flipped=new byte[pixels.Length];
        for(var y=0;y<h;y++)Buffer.BlockCopy(pixels,(h-1-y)*w*4,flipped,y*w*4,w*4);
        return flipped;
    }

    public void Swap()=>SwapBuffers(dc);

    public void Dispose()
    {
        if(glrc!=IntPtr.Zero){wglMakeCurrent(IntPtr.Zero,IntPtr.Zero);wglDeleteContext(glrc);glrc=IntPtr.Zero;}
        if(Hwnd!=IntPtr.Zero){ReleaseDC(Hwnd,dc);DestroyWindow(Hwnd);Hwnd=IntPtr.Zero;}
    }

    private delegate IntPtr WndProc(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct WNDCLASSEXW{public uint cbSize,style;public IntPtr lpfnWndProc;public int cbClsExtra,cbWndExtra;public IntPtr hInstance,hIcon,hCursor,hbrBackground;public string? lpszMenuName;public string lpszClassName;public IntPtr hIconSm;}
    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR{public ushort nSize,nVersion;public uint dwFlags;public byte iPixelType,cColorBits,cRedBits,cRedShift,cGreenBits,cGreenShift,cBlueBits,cBlueShift,cAlphaBits,cAlphaShift,cAccumBits,cAccumRedBits,cAccumGreenBits,cAccumBlueBits,cAccumAlphaBits,cDepthBits,cStencilBits,cAuxBuffers,iLayerType,bReserved;public uint dwLayerMask,dwVisibleMask,dwDamageMask;}
    [StructLayout(LayoutKind.Sequential)] private struct MSG{public IntPtr hwnd;public uint message;public IntPtr wParam,lParam;public uint time;public int x,y;}
    [StructLayout(LayoutKind.Sequential)] private struct RECT{public int Left,Top,Right,Bottom;}

    [DllImport("kernel32",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32",SetLastError=true,CharSet=CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);
    [DllImport("user32",SetLastError=true,CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProcW(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd,int cmd);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr LoadCursorW(IntPtr instance,int id);
    [DllImport("user32")] private static extern bool PeekMessageW(out MSG msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("user32")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern short GetKeyState(int vk);
    [DllImport("gdi32")] private static extern int ChoosePixelFormat(IntPtr dc,ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32")] private static extern bool SetPixelFormat(IntPtr dc,int format,ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32")] private static extern bool SwapBuffers(IntPtr dc);
    [DllImport("opengl32")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32")] private static extern bool wglMakeCurrent(IntPtr dc,IntPtr glrc);
    [DllImport("opengl32")] private static extern bool wglDeleteContext(IntPtr glrc);
    [DllImport("opengl32")] private static extern IntPtr glGetString(uint name);
    [DllImport("opengl32")] private static extern void glViewport(int x,int y,int w,int h);
    [DllImport("opengl32")] private static extern void glClearColor(float r,float g,float b,float a);
    [DllImport("opengl32")] private static extern void glClear(uint mask);
    [DllImport("opengl32")] private static extern void glEnable(uint cap);
    [DllImport("opengl32")] private static extern void glDisable(uint cap);
    [DllImport("opengl32")] private static extern void glBlendFunc(uint s,uint d);
    [DllImport("opengl32")] private static extern void glEnableClientState(uint array);
    [DllImport("opengl32")] private static extern void glDisableClientState(uint array);
    [DllImport("opengl32")] private static extern void glMatrixMode(uint mode);
    [DllImport("opengl32")] private static extern void glLoadIdentity();
    [DllImport("opengl32")] private static extern void glOrtho(double l,double r,double b,double t,double n,double f);
    [DllImport("opengl32")] private static extern void glVertexPointer(int size,uint type,int stride,void* pointer);
    [DllImport("opengl32")] private static extern void glTexCoordPointer(int size,uint type,int stride,void* pointer);
    [DllImport("opengl32")] private static extern void glColorPointer(int size,uint type,int stride,void* pointer);
    [DllImport("opengl32")] private static extern void glScissor(int x,int y,int w,int h);
    [DllImport("opengl32")] private static extern void glBindTexture(uint target,uint texture);
    [DllImport("opengl32")] private static extern void glDrawElements(uint mode,int count,uint type,void* indices);
    [DllImport("opengl32")] private static extern void glGenTextures(int n,uint* textures);
    [DllImport("opengl32")] private static extern void glTexParameteri(uint target,uint name,int value);
    [DllImport("opengl32")] private static extern void glPixelStorei(uint name,int value);
    [DllImport("opengl32")] private static extern void glTexImage2D(uint target,int level,int internalFormat,int w,int h,int border,uint format,uint type,void* pixels);
    [DllImport("opengl32")] private static extern void glReadBuffer(uint mode);
    [DllImport("opengl32")] private static extern void glReadPixels(int x,int y,int w,int h,uint format,uint type,void* pixels);
}
