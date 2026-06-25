// ReSharper disable InconsistentNaming
using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FFLogsUploaderPlugin.Ipc;

public class IINACTIpc(IDalamudPluginInterface pluginInterface)
{
    private readonly ICallGateSubscriber<Version> getVersion = pluginInterface.GetIpcSubscriber<Version>("IINACT.Version");

    public bool IsActive()
    {
        try
        {
            var version = getVersion.InvokeFunc();
            Plugin.Log.Debug($"IINACT is active, version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
            return true;
        }
        catch (IpcNotReadyError)
        {
            Plugin.Log.Debug("IINACT is not active");
            return false;
        }
    }

    public string? GetLogFilePath()
    {
        var pluginConfigDirectory = pluginInterface.GetPluginConfigDirectory();
        var iinactConfigFile = Path.Combine(pluginConfigDirectory, "..", "IINACT.json");

        if (!File.Exists(iinactConfigFile))
            return null;

        var iinactConfig =
            JsonConvert.DeserializeAnonymousType(File.ReadAllText(iinactConfigFile),
                                                 new { LogFilePath = string.Empty });

        return iinactConfig?.LogFilePath;
    }
}
