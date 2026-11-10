// X-120: PluginUiTest, "draw every window of your plugin without the game and tell me what breaks", for Dalamud plugin creators, on their
// PC and in CI. Test tool, not affiliated with the Dalamud team, does not replace Dalamud. README.md beside the tool; evidence:
// docs/evidence/x120-plugin-ui-test.md. Exit codes: 0 all drawn, 1 something threw, blocked or crashed, 2 usage or refusal, 3 the plugin
// could not be created.
using System.Text;
using PluginUiTest;
using PluginUiTest.Host;

Console.OutputEncoding=Encoding.UTF8;
var (options,error)=Options.Parse(args);
if(options is null)
{
    if(error is { Length:>0 })Console.Error.WriteLine("PluginUiTest: "+error);
    Console.WriteLine(Options.Usage);
    return error is { Length:>0 } ? Options.ExitUsage : 0;
}
var appData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var hooks=Path.GetFullPath(options.Dalamud ?? DalamudPaths.Hooks(Environment.GetEnvironmentVariable("DALAMUD_HOME"),appData));
if(options.Child)return Child.Run(options,hooks,appData);

Console.WriteLine($"PluginUiTest {typeof(Options).Assembly.GetName().Version?.ToString(3)}");
Console.WriteLine(Options.Banner);
Console.WriteLine(Options.DataNote);
Console.WriteLine();
var plugin=Path.GetFullPath(options.Plugin);
if(Options.Refusal(plugin,File.Exists(plugin),Directory.Exists(plugin),DalamudPaths.InstalledPlugins(appData)) is { } refusal)
{
    Console.Error.WriteLine("Refused: "+refusal);
    return Options.ExitUsage;
}
if(DalamudRuntime.Missing(hooks) is { } missing)
{
    Console.Error.WriteLine($"Refused: no Dalamud in {hooks} ({missing} missing). Install XIVLauncher with Dalamud, set DALAMUD_HOME, or pass --dalamud=<folder> "
        +"(in CI: the folder where your build downloaded Dalamud's distribution).");
    return Options.ExitUsage;
}
Console.WriteLine($"Plugin: {plugin}");
return Parent.Run(options,hooks,plugin);
