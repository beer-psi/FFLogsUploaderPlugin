using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFLogsUploaderPlugin.FFLogs;

namespace FFLogsUploaderPlugin.Windows;

public partial class MainWindow
{
    private string email = string.Empty;
    private string password = string.Empty;
    private bool automaticLogin;
    
    private bool isLoggingIn;
    private string loginErrorMessage = string.Empty;
    
    private void DrawLoginScreen()
    {
        ImGui.Text("Log in to FFLogs");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Disabled(isLoggingIn))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Email##email", "Email", ref email,
                                        flags: ImGuiInputTextFlags.EnterReturnsTrue))
            {
                DoLogin();
            }
        
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Password##password", "Password", ref password,
                                        flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                DoLogin();
            }
        
            ImGui.Checkbox("Automatically login", ref automaticLogin);
        
            ImGui.Spacing();

            if (ImGui.Button(isLoggingIn ? "Logging in..." : "Log in", new Vector2(-1, 30)))
            {
                DoLogin();
            }
        }

        if (!loginErrorMessage.IsNullOrWhitespace())
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), loginErrorMessage);
        }
    }
    
    internal void DoAutomaticLogin()
    {
        isLoggingIn = true;
        Task.Run(plugin.FfLogs.AutomaticLoginAsync).ContinueWith(DoLoginContinuation);
    }

    private void DoLogin()
    {
        if (email.IsNullOrWhitespace() || password.IsNullOrWhitespace())
        {
            loginErrorMessage = "Email or password is missing.";
            return;
        }
        
        isLoggingIn = true;
        Task.Run(() => plugin.FfLogs.LoginAsync(email, password, automaticLogin)).ContinueWith(DoLoginContinuation!);
    }

    private void DoLoginContinuation(Task<DesktopClient.LoginResponse?> task)
    {
        isLoggingIn = false;

        if (task.Exception != null)
        {
            Plugin.Log.Error(task.Exception, "Log in failed");
            loginErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
            return;
        }

        if (task.Result is not { } user)
            return;
                
        Plugin.Log.Information("Logged in as {0}", user.User.UserName);
        SetOptionsFromConfiguration();
        StartParser();
    }
}
