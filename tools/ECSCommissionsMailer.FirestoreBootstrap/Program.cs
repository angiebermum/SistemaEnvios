using System.Text;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Bootstrap;

Console.OutputEncoding = Encoding.UTF8;

if (args.Any(argument => argument is "--help" or "-h"))
{
    PrintUsage();
    return 0;
}

try
{
    var environment = FirestoreOptions.FromEnvironment();
    var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
    var options = new FirestoreOptions
    {
        ProjectId = ReadOption(args, "--project-id") ?? environment.ProjectId,
        DatabaseId = ReadOption(args, "--database-id") ?? environment.DatabaseId,
        Enabled = apply
    };

    if (!apply)
    {
        Console.WriteLine("DRY-RUN: no se abrió conexión ni se escribió en Firestore.");
        Console.WriteLine($"ProjectId: {options.ProjectId ?? "(no configurado)"}");
        Console.WriteLine($"DatabaseId: {options.DatabaseId}");
        Console.WriteLine("Documentos que se crearían solo si no existen:");
        Console.WriteLine("- system/schema");
        Console.WriteLine("- system/migrationState");
        Console.WriteLine("Use --apply para ejecutar explícitamente el bootstrap mediante ADC.");
        return 0;
    }

    var database = new FirestoreConnectionFactory().Create(options);
    var result = await new FirestoreSchemaBootstrapper(database).PrepareAsync();
    Console.WriteLine(result.SchemaCreated
        ? "Creado: system/schema"
        : "Sin cambios: system/schema ya existía");
    Console.WriteLine(result.MigrationStateCreated
        ? "Creado: system/migrationState"
        : "Sin cambios: system/migrationState ya existía");
    Console.WriteLine("No se crearon colecciones operativas ni se migraron datos locales.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Bootstrap cancelado: {exception.Message}");
    return 1;
}

static string? ReadOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Falta el valor de {name}.");
        }

        return arguments[index + 1];
    }

    return null;
}

static void PrintUsage()
{
    Console.WriteLine("ECSCommissionsMailer.FirestoreBootstrap");
    Console.WriteLine("Uso:");
    Console.WriteLine("  dotnet run --project tools/ECSCommissionsMailer.FirestoreBootstrap -- [--project-id ID] [--database-id ID] [--apply]");
    Console.WriteLine();
    Console.WriteLine("Sin --apply funciona como dry-run. La conexión usa Application Default Credentials.");
    Console.WriteLine($"También acepta {FirestoreOptions.ProjectIdEnvironmentVariable}, GOOGLE_CLOUD_PROJECT y {FirestoreOptions.DatabaseIdEnvironmentVariable}.");
}
