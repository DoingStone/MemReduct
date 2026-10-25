using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MemReduct.Gui;

/// <summary>
/// Registers the application to start at sign-in.
/// <para>
/// The registration is a per-user Scheduled Task, not a value under the <c>Run</c> key. The shell
/// starts <c>Run</c> entries with the ordinary, non-elevated token, and an executable carrying the
/// <c>RUNASADMIN</c> compatibility flag cannot be created from that context: the shell logs the
/// command as started with process id 0 and the user never sees a window. A task with
/// <c>RunLevel=HighestAvailable</c> and an interactive logon token starts the same executable with
/// the full token and no consent prompt, which is also what this tool needs to release the standby
/// list.
/// </para>
/// <para>
/// The task is created through <c>schtasks.exe</c> rather than the Task Scheduler COM API so that
/// the same definition can be registered by hand while diagnosing a machine.
/// </para>
/// <para>
/// The legacy <c>Run</c> value is still read, and is always deleted whenever the task is in place:
/// two registrations would start two copies, and the second one only manages to show an
/// "already running" message box. It is also the fallback when the task cannot be created, so a
/// machine that blocks Task Scheduler but allows start-up entries still gets a registration.
/// </para>
/// </summary>
internal static class StartupRegistration
{
    private const string TaskName = "MemReduct";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MemReduct";

    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Delay between sign-in and the launch. Start-up is competing with the shell, the desktop
    /// comes up first, and nothing about a memory tool is urgent enough to join that race.
    /// </summary>
    private static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(15);

    /// <summary>The image to start, or null when this build has no executable path.</summary>
    public static string? ExecutablePath
    {
        get
        {
            // Environment.ProcessPath is the real image path for a single-file publish;
            // Application.ExecutablePath can point at the extracted host instead.
            var path = Environment.ProcessPath;

            if (string.IsNullOrEmpty(path))
            {
                path = Application.ExecutablePath;
            }

            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>The command Windows would run, or null when this build has no executable path.</summary>
    public static string? Command
    {
        get
        {
            if (ExecutablePath is not { } path)
            {
                return null;
            }

            // Quoted only when it has to be: a path with a space would otherwise be parsed
            // as a command with arguments.
            return path.Contains(' ') ? $"\"{path}\"" : path;
        }
    }

    /// <summary>Whether sign-in start-up is registered for this executable.</summary>
    public static bool IsEnabled()
    {
        if (IsTaskCurrent())
        {
            return true;
        }

        // Only when the task is not it: an entry left behind by an older build.
        var stored = ReadRunValue();

        return stored is not null &&
            string.Equals(Normalize(stored), ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers or removes the sign-in start-up. Returns whether the setting is now in effect.
    /// </summary>
    public static bool Set(bool enabled)
    {
        if (!enabled)
        {
            DeleteTask();
            DeleteRunValue();

            return !TaskExists() && ReadRunValue() is null;
        }

        if (ExecutablePath is not { } path)
        {
            return false;
        }

        if (!RegisterTask(path))
        {
            // Task Scheduler is unavailable or locked down. The classic start-up key needs no
            // such rights, so it is worth keeping as the degraded registration.
            return WriteRunValue();
        }

        DeleteRunValue();

        return true;
    }

    /// <summary>Whether the scheduled task exists, is enabled, and points at this executable.</summary>
    private static bool IsTaskCurrent()
    {
        if (QueryTaskXml() is not { } xml)
        {
            return false;
        }

        try
        {
            var root = XDocument.Parse(xml).Root;

            if (root is null)
            {
                return false;
            }

            if (!IsOn(root.Element(TaskNamespace + "Settings")?.Element(TaskNamespace + "Enabled")))
            {
                // Disabled from Task Scheduler. The check box is a promise that the tool will
                // start, so it reports this as off rather than as on-but-not-running.
                return false;
            }

            // The registration only counts as start-up while a logon trigger carries it: a task
            // left behind with another trigger, or with its trigger switched off, starts nothing
            // when the user signs in.
            var trigger = root
                .Element(TaskNamespace + "Triggers")?
                .Element(TaskNamespace + "LogonTrigger");

            if (trigger is null || !IsOn(trigger.Element(TaskNamespace + "Enabled")))
            {
                return false;
            }

            var command = root?
                .Element(TaskNamespace + "Actions")?
                .Elements(TaskNamespace + "Exec")
                .Select(exec => exec.Element(TaskNamespace + "Command")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            // A registration left pointing at a folder that has since been moved or renamed is
            // stale, and for the same reason counts as off.
            return string.Equals(Normalize(command), ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TaskExists() => QueryTaskXml() is not null;

    /// <summary>The registered task definition, or null when there is no such task.</summary>
    private static string? QueryTaskXml()
    {
        var (exitCode, output) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML");

        return exitCode == 0 && output.Contains('<') ? output : null;
    }

    /// <summary>Writes the task definition to a temporary file and registers it.</summary>
    private static bool RegisterTask(string path)
    {
        var file = Path.Combine(Path.GetTempPath(), "MemReduct-startup-task.xml");

        try
        {
            // schtasks reads the definition from a file, and only accepts Unicode there, so the
            // declaration in the document and the encoding on disk have to agree.
            File.WriteAllText(
                file,
                BuildTaskXml(path),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            return Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // A leftover file in the temp directory is not worth reporting.
            }
        }
    }

    private static void DeleteTask()
    {
        // A task that is already gone is the goal state, so the exit code is not interesting.
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>The task definition as schtasks wants to read it.</summary>
    private static string BuildTaskXml(string path)
    {
        var user = WindowsIdentity.GetCurrent().Name;

        // Task Scheduler rejects a document whose sections are not exactly the element set of the
        // declared schema version: 1.2 refuses <DisallowStartOnRemoteAppSession>, 1.3 accepts it.
        // The order below is the one Task Scheduler itself writes for this version.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{SecurityElement.Escape(user)}</Author>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger id="Trigger1">
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT{(int)LogonDelay.TotalSeconds}S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <Duration>PT10M</Duration>
                  <WaitTimeout>PT1H</WaitTimeout>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <!-- Task Scheduler drops this element again while it is true; IsOn reads an
                     absent element as on for exactly that reason. -->
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(path)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(path) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string? ReadRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool WriteRunValue()
    {
        try
        {
            if (Command is not { } command)
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);

            return true;
        }
        catch (Exception)
        {
            // A locked-down or read-only hive.
            return false;
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Nothing to do: the value is not coming back on its own.
        }
    }

    /// <summary>
    /// Whether an optional <c>Enabled</c> element says the element it sits in is switched on.
    /// Task Scheduler stores the definition without this element whenever its value is true: it is
    /// written back only for a disabled task or trigger. An absent element therefore means on, and
    /// only an explicit <c>false</c> means off — reading it as "must be present and true" would
    /// call every healthy registration off.
    /// </summary>
    private static bool IsOn(XElement? element) =>
        element is null || !bool.TryParse(element.Value, out var on) || on;

    /// <summary>Trims a stored command down to the image path it names.</summary>
    private static string? Normalize(string? value)
    {
        var text = value?.Trim().Trim('"').Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
            {
                return (-1, string.Empty);
            }

            // The bytes are read rather than the text: schtasks answers a query with a UTF-16
            // document, which a reader left on the console code page would turn into noise.
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit();

            return (process.ExitCode, Decode(buffer.ToArray()));
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
