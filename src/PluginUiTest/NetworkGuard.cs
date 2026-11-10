using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;

namespace PluginUiTest;

/// <summary>Thrown into the plugin's web request by the network guard: the request is reported, never made.</summary>
internal sealed class NetworkBlockedException(string request):Exception($"network request blocked by PluginUiTest: {request}");

/// <summary>
/// Safeguard « no network », in the drawing child process only. The tool itself makes no request and opens no socket. What the plugin asks:
/// (1) every HttpClient request that goes through .NET's diagnostics handler (the default for HttpClient, HttpClientHandler and
/// SocketsHttpHandler) is stopped before it connects: the guard's listener of "HttpHandlerDiagnosticListener" records the method and address
/// and throws NetworkBlockedException into the request; (2) as a second net, the process' default proxy (HttpClient.DefaultProxy) points at
/// a closed local port, so a request that skips the diagnostics (System.Net.Http.EnableActivityPropagation switched off) fails without
/// leaving the PC, unless the plugin sets its own proxy or turns proxies off; (3) raw socket connections (TcpClient, Socket, WebSocket)
/// are only recorded (System.Net.Sockets events), not blocked. Recorded attempts go to the report of the part that made them.
/// </summary>
internal static class NetworkGuard
{
    private static readonly List<string> attempts=[];
    private static SocketEvents? sockets;

    public static void Install()
    {
        HttpClient.DefaultProxy=new WebProxy("http://127.0.0.1:9"){BypassProxyOnLocal=false};
        DiagnosticListener.AllListeners.Subscribe(new Listeners());
        sockets=new SocketEvents();
    }

    public static void Record(string what){lock(attempts)attempts.Add(what);}

    /// <summary>The attempts since the last call, distinct, in order.</summary>
    public static string[] Take(){lock(attempts){var list=attempts.Distinct().ToArray();attempts.Clear();return list;}}

    private sealed class Listeners:IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if(listener.Name=="HttpHandlerDiagnosticListener")listener.Subscribe(new Requests());
        }
        public void OnCompleted(){}
        public void OnError(Exception error){}
    }

    private sealed class Requests:IObserver<KeyValuePair<string,object?>>
    {
        public void OnNext(KeyValuePair<string,object?> value)
        {
            if(value.Key is not ("System.Net.Http.HttpRequestOut.Start" or "System.Net.Http.Request"))return;
            var request=value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
            var text=$"{request?.Method} {request?.RequestUri}";
            Record("blocked: "+text);
            throw new NetworkBlockedException(text);
        }
        public void OnCompleted(){}
        public void OnError(Exception error){}
    }

    private sealed class SocketEvents:EventListener
    {
        protected override void OnEventSourceCreated(EventSource source)
        {
            if(source.Name=="System.Net.Sockets")EnableEvents(source,EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if(data.EventName=="ConnectStart")Record("socket connect (recorded, not blocked): "+string.Join(" ",data.Payload ?? []));
        }
    }
}
