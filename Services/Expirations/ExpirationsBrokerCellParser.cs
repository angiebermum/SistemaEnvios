using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsBrokerCellParser(ExpirationsBrokerNormalizer? normalizer = null)
{
    private readonly ExpirationsBrokerNormalizer _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();

    public IReadOnlyList<ExpirationsBrokerComponent> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];

        return value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.None)
            .Select(component => component.Trim())
            .Where(component => component.Length > 0)
            .Select(component => new ExpirationsBrokerComponent
            {
                RawValue = component,
                NormalizedValue = _normalizer.Normalize(component)
            })
            .ToList();
    }
}
