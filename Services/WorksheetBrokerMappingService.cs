using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class WorksheetBrokerMappingService
{
    public WorksheetBrokerMappingResult Resolve(
        IEnumerable<string> worksheetNames,
        IEnumerable<Broker> brokers)
    {
        var result = new WorksheetBrokerMappingResult();
        var index = BuildIndex(brokers, result.Errors);
        foreach (var worksheetName in worksheetNames)
        {
            var normalized = NormalizeWorksheetName(worksheetName);
            if (normalized.Length == 0)
            {
                result.Errors.Add("El libro contiene una pestaña sin nombre válido.");
            }
            else if (index.TryGetValue(normalized, out var broker))
            {
                result.Assignments.Add(new WorksheetBrokerAssignment(worksheetName, broker));
            }
            else
            {
                result.MissingWorksheetNames.Add(worksheetName);
            }
        }

        return result;
    }

    public List<string> ValidateConfiguration(IEnumerable<Broker> brokers)
    {
        var errors = new List<string>();
        _ = BuildIndex(brokers, errors);
        foreach (var broker in brokers)
        {
            errors.AddRange(ValidateDeductions(broker));
        }

        return errors;
    }

    public List<string> ValidateWorksheetNames(Broker editedBroker, IEnumerable<Broker> allBrokers)
    {
        var errors = new List<string>();
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var worksheetName in editedBroker.AssociatedWorksheetNames)
        {
            var normalized = NormalizeWorksheetName(worksheetName);
            if (normalized.Length == 0)
            {
                errors.Add("Los nombres de pestaña no pueden estar vacíos.");
            }
            else if (!local.Add(normalized))
            {
                errors.Add($"La pestaña '{worksheetName.Trim()}' está repetida en este corredor.");
            }
        }

        var otherAssignments = allBrokers
            .Where(value => value.Id != editedBroker.Id)
            .SelectMany(broker => broker.AssociatedWorksheetNames.Select(name =>
                new { Name = NormalizeWorksheetName(name), Broker = broker }))
            .Where(value => value.Name.Length > 0)
            .ToLookup(value => value.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var worksheetName in editedBroker.AssociatedWorksheetNames)
        {
            var normalized = NormalizeWorksheetName(worksheetName);
            var existing = otherAssignments[normalized].FirstOrDefault();
            if (existing is not null)
            {
                errors.Add($"La pestaña '{worksheetName.Trim()}' ya está asociada al corredor '{existing.Broker.Name}'.");
            }
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> ValidateDeductions(Broker broker)
    {
        var errors = new List<string>();
        foreach (var deduction in broker.Deductions)
        {
            if (string.IsNullOrWhiteSpace(deduction.Description))
            {
                errors.Add($"El corredor '{broker.Name}' tiene un rebajo sin descripción.");
            }

            if (deduction.Amount < 0)
            {
                errors.Add($"El rebajo '{deduction.Description}' de '{broker.Name}' no puede ser negativo.");
            }

            if (!Enum.IsDefined(deduction.Currency))
            {
                errors.Add($"El rebajo '{deduction.Description}' de '{broker.Name}' no tiene una moneda válida.");
            }

            if (!Enum.IsDefined(deduction.ApplicationType))
            {
                errors.Add($"El rebajo '{deduction.Description}' de '{broker.Name}' no tiene un tipo válido.");
            }

            var targetWorksheetName = NormalizeWorksheetName(deduction.TargetWorksheetName);
            if (targetWorksheetName.Length == 0)
            {
                errors.Add(
                    $"El rebajo '{deduction.Description}' de '{broker.Name}' no tiene una pestaña destino.");
            }
            else if (!broker.AssociatedWorksheetNames.Any(value =>
                         string.Equals(
                             NormalizeWorksheetName(value),
                             targetWorksheetName,
                             StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    $"El rebajo '{deduction.Description}' de '{broker.Name}' apunta a la pestaña " +
                    $"'{targetWorksheetName}', que no está asociada a ese corredor.");
            }
        }

        return errors;
    }

    public static string NormalizeWorksheetName(string? value) => (value ?? string.Empty).Trim();

    private static Dictionary<string, Broker> BuildIndex(IEnumerable<Broker> brokers, ICollection<string> errors)
    {
        var index = new Dictionary<string, Broker>(StringComparer.OrdinalIgnoreCase);
        foreach (var broker in brokers)
        {
            foreach (var worksheetName in broker.AssociatedWorksheetNames)
            {
                var normalized = NormalizeWorksheetName(worksheetName);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (index.TryGetValue(normalized, out var existing) && existing.Id != broker.Id)
                {
                    errors.Add(
                        $"La pestaña '{worksheetName.Trim()}' está asociada a dos corredores: " +
                        $"'{existing.Name}' y '{broker.Name}'.");
                    continue;
                }

                index[normalized] = broker;
            }
        }

        return index;
    }
}
