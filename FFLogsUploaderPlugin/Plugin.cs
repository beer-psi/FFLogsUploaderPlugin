using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFLogsUploaderPlugin.Windows;

namespace FFLogsUploaderPlugin;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private const string CommandName = "/pfflogs";
    private const string CallWipeCommandName = "/callwipe";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("FFLogsUploaderPlugin");
    internal MainWindow MainWindow { get; init; }
    
    // ReSharper disable once InconsistentNaming
    internal DtrBarEntry DtrBarEntry { get; init; }
    internal FFLogsManager FFLogs { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        DtrBarEntry = new DtrBarEntry(this); 
        FFLogs = new FFLogsManager(this);
        MainWindow = new MainWindow(this);
    }
    
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        _ = FFLogs.InitAsync(cancellationToken);
        
        WindowSystem.AddWindow(MainWindow);
        
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the FFLogs uploader."
        });
        CommandManager.AddHandler(CallWipeCommandName, new CommandInfo(OnCallWipe)
        {
            HelpMessage = "Calls a wipe when live logging."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        await FFLogs.DisposeAsync();
        
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CallWipeCommandName);
        
        foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "????????.???"))
        {
            var nativeLib = Path.Combine(dir, "ClearScriptV8.win-x64.dll");

            if (!File.Exists(nativeLib))
                continue;

            if (Directory.EnumerateFileSystemEntries(dir).Count() > 1)
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void OnCommand(string command, string args) => MainWindow.Toggle();

    private void OnCallWipe(string command, string args)
    {
        if (!FFLogs.IsLiveLogging)
        {
            ChatGui.PrintError("[FF Logs Uploader] Currently not live logging, cannot call wipe.");
            return;
        }
        
        Task.Run(async () =>
        {
            await FFLogs.CallWipeAsync();
            ChatGui.Print("[FF Logs Uploader] Called a wipe.");
        });
    }
    
    public void ToggleMainUi() => MainWindow.Toggle();
}
