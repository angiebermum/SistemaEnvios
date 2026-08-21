using System.Text;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECSCommissionsMailer.FirestoreMigration;

Console.OutputEncoding = Encoding.UTF8;

if (args.Any(value => value is "--help" or "-h"))
{
    PrintUsage();
    return 0;
}

try
{
    var modes = new[] { "--dry-run", "--apply", "--verify", "--finalize-state" }.Where(HasFlag).ToList();
    if (modes.Count != 1)
    {
        throw new ArgumentException("Indique exactamente uno de --dry-run, --apply, --verify o --finalize-state.");
    }

    var environment = FirestoreOptions.FromEnvironment();
    var options = new MigrationRunOptions(
        modes[0][2..],
        ReadRequired("--source-directory"),
        ReadOption("--project-id") ?? environment.ProjectId,
        ReadOption("--database-id") ?? environment.DatabaseId);
    return await new FirestoreMigrationRunner().RunAsync(options);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Argumentos inválidos: {exception.Message}");
    PrintUsage();
    return 2;
}

bool HasFlag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

string ReadRequired(string name) => ReadOption(name)
    ?? throw new ArgumentException($"{name} es obligatorio.");

string? ReadOption(string name)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Falta el valor de {name}.");
        }

        return args[index + 1];
    }

    return null;
}

static void PrintUsage()
{
    Console.WriteLine("ECSCommissionsMailer.FirestoreMigration");
    Console.WriteLine("Uso:");
    Console.WriteLine("  dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- --dry-run --source-directory <DIR> [--project-id <ID>] [--database-id <ID>]");
    Console.WriteLine("  dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- --apply --source-directory <DIR> --project-id <ID> [--database-id <ID>]");
    Console.WriteLine("  dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- --verify --source-directory <DIR> --project-id <ID> [--database-id <ID>]");
    Console.WriteLine("  dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- --finalize-state --source-directory <DIR> --project-id <ID> [--database-id <ID>]");
    Console.WriteLine();
    Console.WriteLine("DatabaseId por defecto: (default). --apply repite el dry-run, crea backup, ejecuta bootstrap, preflight, escritura idempotente, verify e idempotencia.");
    Console.WriteLine("--finalize-state exige verify/idempotencia completa y actualiza únicamente system/migrationState.");
    Console.WriteLine("La autenticación usa exclusivamente Application Default Credentials externas.");
}
