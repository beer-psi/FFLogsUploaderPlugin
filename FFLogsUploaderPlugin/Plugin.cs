using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFLogsUploaderPlugin.FFLogs;
using FFLogsUploaderPlugin.Windows;

namespace FFLogsUploaderPlugin;

public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/pfflogs";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("FFLogsUploaderPlugin");
    private MainWindow MainWindow { get; init; }
    
    internal DesktopClient FfLogsDesktopClient { get; init; }
    internal DesktopClient.LoginResponse? FfLogsUser { get; set; } = null;
    internal LogParser FfLogParser { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MainWindow = new MainWindow(this);
        FfLogsDesktopClient = new DesktopClient();
        FfLogParser = new LogParser();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        WindowSystem.AddWindow(MainWindow);
        
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the FFLogs uploader."
        });
        CommandManager.AddHandler("/callwipe", new CommandInfo(OnCallWipe)
        {
            HelpMessage = "Calls a wipe when live logging."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        await DoAutomaticLoginAsync();
    }

    public async ValueTask DisposeAsync()
    {
        FfLogsUser = null;
        FfLogParser.Dispose();
        FfLogsDesktopClient.Dispose();
        
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        // PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();
        
        await MainWindow.DisposeAsync();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler("/callwipe");
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    private void OnCallWipe(string command, string args)
    {
        Task.Run(async () =>
        {
            await FfLogParser.CallWipeAsync();
            ChatGui.Print("[FFLogs Uploader] Called a wipe.");
        });
    }

    private async Task DoAutomaticLoginAsync()
    {
        if (Configuration.FfLogsAutomaticLogin && !Configuration.FfLogsEmail.IsNullOrWhitespace() &&
            !Configuration.FfLogsPassword.IsNullOrWhitespace())
        {
            MainWindow.IsLoggingIn = true;

            try
            {
                FfLogsUser =
                    await FfLogsDesktopClient.LoginAsync(Configuration.FfLogsEmail, Configuration.FfLogsPassword);
                Log.Information("Logged in as {0}", FfLogsUser.User.UserName);
            }
            catch (Exception e)
            {
                Log.Error(e, "Automatic login failed");
                MainWindow.LoginErrorMessage = e.Message;
                return;
            }
            finally
            {
                MainWindow.IsLoggingIn = false;
            }

            MainWindow.SetOptionsFromConfiguration();
            
            try
            {
                await FfLogParser.StartAsync(false, false, false,
                                                    await FfLogsDesktopClient.DownloadParserScript(
                                                        FfLogParser.Id, false, false, false));
                Log.Information("Parser version {0} loaded", await FfLogParser.GetParserVersionAsync());
            }
            catch (Exception e)
            {
                Log.Error(e, "Loading parser failed");
            }
        }
    }
    
    public void ToggleMainUi() => MainWindow.Toggle();
}
