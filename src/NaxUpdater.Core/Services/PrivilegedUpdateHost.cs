using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NaxUpdater.Core.Models;

namespace NaxUpdater.Core.Services;

internal static class PrivilegedUpdateHost
{
    internal const string LaunchArgument = "--apply-elevated-update";
    private const string PipePrefix = "NaxUpdater.Update.";
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Installer = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal sealed record PayloadFile(string Source, string RelativePath, string Sha256);
    private sealed record InstallerJob(UpdateExecutionKind Kind, string Entry, string[] Arguments, bool Interactive,
        PayloadFile[] Files, string[] Directories);
    private sealed record NativeJob(string Executable, string WorkingDirectory, string[] Arguments);
    private sealed record Request(InstallerJob? Installer = null, WingetUpdateTarget? WinGet = null,
        ChocolateyUpdateTarget? Chocolatey = null, NativeJob? Native = null);

    internal static Task<UpdateExecutionResult> RunWingetAsync(WingetUpdateTarget target, CancellationToken token) =>
        StartAsync(new Request(WinGet: target), token);

    internal static Task<UpdateExecutionResult> RunChocolateyAsync(ChocolateyUpdateTarget target, CancellationToken token) =>
        StartAsync(new Request(Chocolatey: target), token);

    internal static Task<UpdateExecutionResult> RunNativeAsync(
        string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken token) =>
        StartAsync(new Request(Native: new NativeJob(executable, workingDirectory, arguments.ToArray())), token);

    internal static Task<UpdateExecutionResult> ApplyNativeInProcessAsync(
        string executable, string workingDirectory, IReadOnlyList<string> arguments) =>
        !Environment.IsPrivilegedProcess
            ? throw new UpdateExecutionRefusedException("The elevated update requires administrator rights.")
            : Task.Run(() => ApplyNativeAsync(new NativeJob(executable, workingDirectory, arguments.ToArray())));

    internal static async Task<UpdateExecutionResult> RunInstallerAsync(
        UpdateExecutionPlan plan, PreparedUpdateExecution prepared, CancellationToken token) =>
        await StartAsync(new Request(Installer: await CreateInstallerJobAsync(plan, prepared, token)), token);

    internal static Task<UpdateExecutionResult> ApplyInstallerInProcessAsync(
        UpdateExecutionPlan plan, PreparedUpdateExecution prepared, CancellationToken token)
    {
        if (!Environment.IsPrivilegedProcess)
            throw new UpdateExecutionRefusedException("The elevated update requires administrator rights.");
        return Task.Run(async () => await ApplyInstallerAsync(await CreateInstallerJobAsync(plan, prepared, token)), token);
    }

    private static async Task<InstallerJob> CreateInstallerJobAsync(
        UpdateExecutionPlan plan, PreparedUpdateExecution prepared, CancellationToken token)
    {
        string entry = prepared.ExecutablePath!;
        string root = prepared.CleanupDirectory ?? Path.GetDirectoryName(entry)!;
        var files = new List<PayloadFile>();
        foreach (var item in prepared.ContentLocks!)
        {
            item.Stream.Position = 0;
            try
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(item.Stream, token));
                files.Add(new PayloadFile(item.Path, Path.GetRelativePath(root, item.Path), hash));
            }
            finally { item.Stream.Position = 0; }
        }
        return new InstallerJob(plan.Kind, Path.GetRelativePath(root, entry),
            UpdateExecutionService.InstallerArguments(plan).ToArray(),
            plan.Interactive, files.ToArray(), prepared.ApprovedDirectories?.ToArray() ?? []);
    }

    private static Task<UpdateExecutionResult> StartAsync(Request request, CancellationToken token) =>
        Task.Run(() => StartCoreAsync(request, token), token);

    private static async Task<UpdateExecutionResult> StartCoreAsync(Request request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath;
        if (executable is null || !IsProtectedApplication(executable))
            throw new UpdateExecutionRefusedException(
                "Install Nax-Updater for all users in a protected folder before applying updates that require administrator rights.");

        byte[] approved = JsonSerializer.SerializeToUtf8Bytes(request);
        if (approved.Length > MaxMessageBytes)
            throw new InvalidDataException("The approved update request exceeds the channel limit.");
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(Admin, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, acl);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(LaunchArgument);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(approved)));
        try
        {
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("The elevated updater did not start.");
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(35));
            var connectTask = pipe.WaitForConnectionAsync(handshake.Token);
            var exitTask = worker.WaitForExitAsync(handshake.Token);
            try
            {
                var completed = await Task.WhenAny(connectTask, exitTask);
                if (ReferenceEquals(completed, exitTask) && !connectTask.IsCompletedSuccessfully)
                {
                    await handshake.CancelAsync();
                    try { await connectTask; } catch (OperationCanceledException) { }
                    throw new UpdateExecutionRefusedException("The elevated updater exited before connecting to the update channel.");
                }
                await connectTask;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != worker.Id)
                    throw new UpdateExecutionRefusedException("The elevated update channel could not verify its worker.");
                await WriteAsync(pipe, approved, handshake.Token);
            }
            catch (OperationCanceledException) when (handshake.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new UpdateExecutionRefusedException("The elevated updater did not connect in time.");
            }
            catch (IOException)
            {
                throw new UpdateExecutionRefusedException("The elevated updater closed the update channel before starting.");
            }
            // Windows owns the installation once the complete approved request has been sent.
            var result = Deserialize<UpdateExecutionResult>(await ReadAsync(pipe, CancellationToken.None));
            if (result.NotStarted && result.ExitCode != 1223)
                throw new UpdateExecutionRefusedException(result.Error ?? "The elevated update request was refused.");
            return result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UpdateExecutionResult(1223, false, "The Windows elevation prompt was cancelled.", NotStarted: true);
        }
    }

    internal static async Task<int> HandleAsync(string[] arguments)
    {
        if (!Environment.IsPrivilegedProcess || arguments.Length != 5 ||
            !arguments[2].StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(arguments[2][PipePrefix.Length..], "N", out _) ||
            !int.TryParse(arguments[3], out int parent) || parent <= 0 ||
            arguments[4].Length != 64 || arguments[4].Any(character => !char.IsAsciiHexDigit(character)) ||
            Environment.ProcessPath is not { } executable || !IsProtectedApplication(executable))
            return 1;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var pipe = new NamedPipeClientStream(".", arguments[2], PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != parent ||
            !IsSameApplication(parent, executable))
            return 1;

        byte[] approved = await ReadAsync(pipe, deadline.Token);
        // Bind the bytes received after UAC to the request that was already approved when elevation began.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(approved), Convert.FromHexString(arguments[4])))
            return 1;
        var request = Deserialize<Request>(approved);
        UpdateExecutionResult result;
        try
        {
            if (request.Installer is { } installer)
                result = await ApplyInstallerAsync(installer);
            else if (request.WinGet is { } winget)
                result = await new WingetPackageService().ApplyElevatedAsync(winget);
            else if (request.Chocolatey is { } chocolatey)
                result = await ChocolateyPackageService.ApplyElevatedAsync(chocolatey);
            else if (request.Native is { } native)
                result = await ApplyNativeAsync(native);
            else
                throw new UpdateExecutionRefusedException("The elevated update request did not specify an approved operation.");
        }
        catch (UpdateExecutionRefusedException exception)
        {
            result = new UpdateExecutionResult(-1, false, exception.Message, NotStarted: true);
        }
        catch (Exception exception)
        {
            result = new UpdateExecutionResult(-1, false, exception.Message);
        }
        await WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(result), CancellationToken.None);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<UpdateExecutionResult> ApplyInstallerAsync(InstallerJob job)
    {
        using var directory = CreateProtectedWorkspace();
        string entry = await StageAsync(directory.Path, job.Entry, job.Files, job.Directories);
        string temporary = Directory.CreateDirectory(Path.Combine(directory.Path, Guid.NewGuid().ToString("N"))).FullName;
        var plan = new UpdateExecutionPlan(job.Kind, null, null, null, null, null,
            job.Arguments, false, [], [], Interactive: job.Interactive);
        var payload = new PreparedUpdateExecution(null, entry, Path.GetDirectoryName(entry), null, null);
        var start = UpdateExecutionService.CreateInstallerStartInfo(plan, payload);
        start.UseShellExecute = false;
        start.WorkingDirectory = Path.GetDirectoryName(entry)!;
        SetProtectedEnvironment(start, temporary);
        return directory.Finish(await UpdateExecutionService.RunStartedProcessAsync(start, CancellationToken.None, job.Interactive, directory.Path));
    }

    private static async Task<UpdateExecutionResult> ApplyNativeAsync(NativeJob job)
    {
        if (!IsProtectedPath(job.Executable) || !IsProtectedPath(job.WorkingDirectory, folder: true))
            throw new UpdateExecutionRefusedException("The elevated native updater is outside a protected installation.");
        using var temporary = CreateProtectedWorkspace();
        var start = new ProcessStartInfo(job.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = job.WorkingDirectory
        };
        foreach (var argument in job.Arguments) start.ArgumentList.Add(argument);
        SetProtectedEnvironment(start, temporary.Path);
        return temporary.Finish(await UpdateExecutionService.RunStartedProcessAsync(start, CancellationToken.None));
    }

    internal static void SetProtectedEnvironment(ProcessStartInfo start, string temporary)
    {
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["PATH"] = MachinePath();
    }

    private const string WorkspacePrefix = "NaxUpdater.Update.";

    internal static Workspace CreateProtectedWorkspace()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) || !IsProtectedPath(programFiles, folder: true))
            throw new UpdateExecutionRefusedException("The Windows Program Files folder is not protected against modification.");
        CleanupStaleWorkspaces(programFiles, DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64));
        var directory = new DirectoryInfo(Path.Combine(programFiles, WorkspacePrefix + Guid.NewGuid().ToString("N")));
        var acl = new DirectorySecurity();
        acl.SetOwner(Admin);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Admin, LocalSystem })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(acl);
        return new Workspace(directory);
    }

    // Staging folders retained for a pending restart (1641/3010) or briefly locked on exit are
    // reclaimed on the next elevated run once Windows has restarted past their creation time.
    internal static void CleanupStaleWorkspaces(string root, DateTime bootTimeUtc)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories(WorkspacePrefix + "*"))
            {
                string suffix = directory.Name[WorkspacePrefix.Length..];
                if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _) ||
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    directory.CreationTimeUtc >= bootTimeUtc)
                    continue;
                try { DeleteDirectoryForce(directory); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteDirectoryForce(DirectoryInfo directory)
    {
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.TryPop(out var folder))
        {
            if (folder.Attributes.HasFlag(FileAttributes.ReadOnly))
                folder.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
        directory.Delete(recursive: true);
    }

    internal sealed class Workspace(DirectoryInfo directory) : IDisposable
    {
        private bool _retained;
        internal string Path => directory.FullName;
        internal UpdateExecutionResult Finish(UpdateExecutionResult result)
        {
            _retained = result.IsSuccess && result.ExitCode is 1641 or 3010;
            return result;
        }
        public void Dispose()
        {
            if (_retained) return; // The installer may have queued files in staging for use when Windows restarts.
            try { DeleteDirectoryForce(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task<string> StageAsync(string root, string entry, IReadOnlyList<PayloadFile> files,
        IReadOnlyList<string>? directories = null)
    {
        string FullPath(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Split(['\\', '/']).Any(part => part is "" or "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
                throw new UpdateExecutionRefusedException("A staged installer path is invalid.");
            return Path.GetFullPath(relative, root);
        }

        string entryPath = FullPath(entry);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories ?? []) Directory.CreateDirectory(FullPath(directory));
        foreach (var file in files)
        {
            string target = FullPath(file.RelativePath);
            if (!seen.Add(target))
                throw new UpdateExecutionRefusedException("The update contains duplicate staged files.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await input.CopyToAsync(output);
            output.Position = 0;
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(output));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateExecutionRefusedException("An installer file changed before it could be copied into protected staging.");
        }
        if (!seen.Contains(entryPath))
            throw new UpdateExecutionRefusedException("The approved installer entry is missing from its staged files.");
        return entryPath;
    }

    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            "0123456789¹²³".Contains(name[3]);
    }

    private static string MachinePath()
    {
        string machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        return string.Join(Path.PathSeparator, machine.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(Environment.SystemDirectory)
            .Where(path => Path.IsPathFullyQualified(path) && IsProtectedPath(path, folder: true))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private const FileSystemRights GenericWrite = (FileSystemRights)0x50000000;
    private const FileSystemRights AncestorChange = GenericWrite | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights EntryWrite = GenericWrite | FileSystemRights.Delete |
        FileSystemRights.WriteAttributes | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        FileSystemRights.WriteData | FileSystemRights.AppendData;

    internal static bool IsProtectedTree(string root)
    {
        if (!IsProtectedPath(root, folder: true)) return false;
        try
        {
            var entries = new Stack<DirectoryInfo>();
            entries.Push(new DirectoryInfo(root));
            while (entries.TryPop(out var folder))
            {
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    bool isDirectory = entry is DirectoryInfo;
                    var writeRights = isDirectory ? EntryWrite | FileSystemRights.DeleteSubdirectoriesAndFiles : EntryWrite;
                    if (!HasTrustedAcl(entry, writeRights)) return false;
                    if (entry is DirectoryInfo child) entries.Push(child);
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    internal static bool IsProtectedPath(string path, bool folder = false)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is null || root.Length != 3 || new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
            FileSystemInfo entry = folder ? new DirectoryInfo(full) : new FileInfo(full);
            var writeRights = folder ? EntryWrite | FileSystemRights.DeleteSubdirectoriesAndFiles : EntryWrite;
            if (!HasTrustedAcl(entry, writeRights)) return false;
            // Create and attribute rights on ancestors (such as C:\ProgramData) do not let a
            // standard user rename, delete, or replace an admin-owned child directory.
            for (DirectoryInfo? parent = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent;
                 parent is not null; parent = parent.Parent)
            {
                var rights = AncestorChange;
                if (!folder && string.Equals(parent.FullName, Path.GetDirectoryName(full), StringComparison.OrdinalIgnoreCase))
                    rights |= FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;
                if (!HasTrustedAcl(parent, rights)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return false; }
    }

    private static bool HasTrustedAcl(FileSystemInfo entry, FileSystemRights rights)
    {
        if (!entry.Exists || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        FileSystemSecurity acl = entry is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null || descriptor.Owner is not { } owner || !Trusted(owner))
            return false;
        return !acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
            (rule.FileSystemRights & rights) != 0 && rule.IdentityReference is SecurityIdentifier sid && !Trusted(sid));
    }

    private static bool Trusted(SecurityIdentifier sid) => sid.Equals(Admin) || sid.Equals(LocalSystem) || sid.Equals(Installer);

    private static bool IsProtectedApplication(string executable) =>
        Path.GetFileName(executable).Equals("Nax-Updater.exe", StringComparison.OrdinalIgnoreCase) &&
        IsProtectedTree(Path.GetDirectoryName(executable)!);

    private static bool IsSameApplication(int parent, string executable)
    {
        uint pid = (uint)parent;
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid || !ProcessIdToSessionId(pid, out uint parentSession) ||
            !ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSession) || parentSession != currentSession)
            return false;
        var image = new StringBuilder(32_768);
        int length = image.Capacity;
        return QueryFullProcessImageName(process, 0, image, ref length) &&
            string.Equals(image.ToString(), executable, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteAsync(Stream pipe, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("The approved update request exceeds the channel limit.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await pipe.WriteAsync(length, token);
        await pipe.WriteAsync(bytes, token);
    }

    private static async Task<byte[]> ReadAsync(Stream pipe, CancellationToken token)
    {
        var length = new byte[4];
        await pipe.ReadExactlyAsync(length, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaxMessageBytes) throw new InvalidDataException("The elevated update message has an invalid size.");
        var bytes = new byte[count];
        await pipe.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("The elevated update message is empty.");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}
