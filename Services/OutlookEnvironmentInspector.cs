using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace ECS.CommissionsMailer.Services;

internal sealed record OutlookEnvironmentInfo
{
    public required string ExecutionKind { get; init; }
    public required string BaseDirectory { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string OperatingSystemArchitecture { get; init; }
    public required bool ProcessElevated { get; init; }
    public required int ThreadId { get; init; }
    public required ApartmentState ApartmentState { get; init; }
    public required bool ProgIdResolved { get; init; }
    public string? ProgIdClsid { get; init; }
    public required string Registry64 { get; init; }
    public required string Registry32 { get; init; }
    public required bool OutlookClassicRunning { get; init; }
    public required bool NewOutlookRunning { get; init; }
    public int? OutlookProcessId { get; init; }
    public string? OutlookExecutablePath { get; init; }
    public string? OutlookArchitecture { get; init; }
    public bool? OutlookElevated { get; init; }

    public bool ArchitectureMismatch =>
        !string.IsNullOrWhiteSpace(OutlookArchitecture) &&
        !string.Equals(ProcessArchitecture, OutlookArchitecture, StringComparison.OrdinalIgnoreCase);

    public bool ElevationMismatch => OutlookElevated.HasValue && ProcessElevated != OutlookElevated.Value;

    public string ToTechnicalText() => string.Join(Environment.NewLine,
        "Diagnóstico del entorno de Outlook:",
        $"  Ejecución: {ExecutionKind}",
        $"  Directorio base: {BaseDirectory}",
        $"  Arquitectura del proceso: {ProcessArchitecture}",
        $"  Arquitectura del sistema operativo: {OperatingSystemArchitecture}",
        $"  Proceso elevado: {ProcessElevated}",
        $"  Id del hilo administrado: {ThreadId}",
        $"  ApartmentState: {ApartmentState}",
        $"  ProgID Outlook.Application resuelto: {ProgIdResolved}",
        $"  CLSID resuelto: {ProgIdClsid ?? "(sin datos)"}",
        $"  Registro COM de 64 bits: {Registry64}",
        $"  Registro COM de 32 bits: {Registry32}",
        $"  OUTLOOK.EXE en ejecución: {OutlookClassicRunning}",
        $"  Nuevo Outlook en ejecución: {NewOutlookRunning}",
        $"  PID de Outlook: {OutlookProcessId?.ToString() ?? "(sin datos)"}",
        $"  Ejecutable de Outlook: {OutlookExecutablePath ?? "(sin datos)"}",
        $"  Arquitectura de Outlook: {OutlookArchitecture ?? "(sin datos)"}",
        $"  Outlook elevado: {OutlookElevated?.ToString() ?? "(sin datos)"}",
        $"  Incompatibilidad de arquitectura detectada: {ArchitectureMismatch}",
        $"  Diferencia de elevación detectada: {ElevationMismatch}");
}

internal static class OutlookEnvironmentInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static OutlookEnvironmentInfo Inspect()
    {
        Type? outlookType = null;
        try
        {
            outlookType = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
        }
        catch (COMException)
        {
            // El resultado queda registrado como no resuelto y la creación conservará la excepción real.
        }

        var outlookProcesses = Process.GetProcessesByName("OUTLOOK");
        var newOutlookProcesses = Process.GetProcessesByName("olk")
            .Concat(Process.GetProcessesByName("HxOutlook"))
            .ToArray();
        var outlookProcess = outlookProcesses.OrderBy(process => process.Id).FirstOrDefault();
        try
        {
            var outlookPath = TryGetProcessPath(outlookProcess);
            return new OutlookEnvironmentInfo
            {
                ExecutionKind = GetExecutionKind(),
                BaseDirectory = AppContext.BaseDirectory,
                ProcessArchitecture = ArchitectureName(RuntimeInformation.ProcessArchitecture),
                OperatingSystemArchitecture = ArchitectureName(RuntimeInformation.OSArchitecture),
                ProcessElevated = IsCurrentProcessElevated(),
                ThreadId = Environment.CurrentManagedThreadId,
                ApartmentState = Thread.CurrentThread.GetApartmentState(),
                ProgIdResolved = outlookType is not null,
                ProgIdClsid = outlookType?.GUID.ToString("B"),
                Registry64 = ReadRegistration(RegistryView.Registry64),
                Registry32 = ReadRegistration(RegistryView.Registry32),
                OutlookClassicRunning = outlookProcess is not null,
                NewOutlookRunning = newOutlookProcesses.Length > 0,
                OutlookProcessId = outlookProcess?.Id,
                OutlookExecutablePath = outlookPath,
                OutlookArchitecture = TryReadPortableExecutableArchitecture(outlookPath),
                OutlookElevated = outlookProcess is null ? null : TryIsProcessElevated(outlookProcess.Id)
            };
        }
        finally
        {
            foreach (var process in outlookProcesses.Concat(newOutlookProcesses))
            {
                process.Dispose();
            }
        }
    }

    private static string GetExecutionKind()
    {
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildConfiguration")?.Value;
        var isPublishedPath = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        return isPublishedPath ? $"Publicado ({configuration ?? "desconocido"})" : configuration ?? "desconocido";
    }

    private static string ReadRegistration(RegistryView view)
    {
        try
        {
            using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var progIdKey = classesRoot.OpenSubKey(@"Outlook.Application\CLSID");
            var clsid = progIdKey?.GetValue(null) as string;
            using var serverKey = string.IsNullOrWhiteSpace(clsid)
                ? null
                : classesRoot.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
            var server = serverKey?.GetValue(null) as string;
            return $"ProgID={(clsid is null ? "ausente" : "presente")}; CLSID={clsid ?? "(sin datos)"}; LocalServer32={server ?? "(sin datos)"}; Existe={RegisteredExecutableExists(server)}";
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return $"No se pudo leer: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool RegisteredExecutableExists(string? localServer)
    {
        if (string.IsNullOrWhiteSpace(localServer))
        {
            return false;
        }

        var value = localServer.Trim();
        string path;
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            path = closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }
        else
        {
            var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exeEnd >= 0 ? value[..(exeEnd + 4)] : value;
        }

        return File.Exists(Environment.ExpandEnvironmentVariables(path));
    }

    private static string? TryGetProcessPath(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? TryReadPortableExecutableArchitecture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            stream.Position = peOffset + 4;
            return reader.ReadUInt16() switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                0xAA64 => "arm64",
                var machine => $"desconocida (0x{machine:X4})"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    private static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        _ => architecture.ToString().ToLowerInvariant()
    };

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool? TryIsProcessElevated(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        var tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
            {
                return null;
            }

            var size = Marshal.SizeOf<TokenElevation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                return GetTokenInformation(tokenHandle, TokenInformationClass.TokenElevation, buffer, size, out _)
                    ? Marshal.PtrToStructure<TokenElevation>(buffer).TokenIsElevated != 0
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }

            CloseHandle(processHandle);
        }
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
