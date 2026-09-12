using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFLogsUploaderPlugin.Integration;
using FFLogsUploaderPlugin.Windows;

namespace FFLogsUploaderPlugin;

public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
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
    
    internal FfLogsManager FfLogs { get; init; }
    internal EngageTimer EngageTimer { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        FfLogs = new FfLogsManager(this);
        MainWindow = new MainWindow(this);
        EngageTimer = new EngageTimer();

        PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }
    
    public Task LoadAsync(CancellationToken cancellationToken)
    {
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

        PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        await FfLogs.DisposeAsync();
        
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CallWipeCommandName);
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (args.AffectedInternalNames.Contains("EngageTimer"))
            EngageTimer.ReloadTypes();
    }

    private void OnCommand(string command, string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            // In response to the slash command, toggle the display status of our main ui
            MainWindow.Toggle();
            return;
        }

        if (string.Equals(args, "engagetimerstart", StringComparison.InvariantCultureIgnoreCase))
        {
            EngageTimer.CombatStart = DateTime.Now;
            ChatGui.Print($"Overridden CombatStart={EngageTimer.CombatStart}", "FF Logs Uploader");
        }
    }

    private void OnCallWipe(string command, string args)
    {
        if (!FfLogs.IsLiveLogging)
        {
            ChatGui.PrintError("[FF Logs Uploader] Currently not live logging, cannot call wipe.");
            return;
        }
        
        Task.Run(async () =>
        {
            await FfLogs.LogParser.CallWipeAsync();
            ChatGui.Print("[FF Logs Uploader] Called a wipe.");
        });
    }
    
    public void ToggleMainUi() => MainWindow.Toggle();
}
