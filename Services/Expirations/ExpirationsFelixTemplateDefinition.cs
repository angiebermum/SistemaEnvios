using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public static class ExpirationsFelixTemplateDefinition
{
    public const string WorksheetName = "Hoja1";
    public const string AlphabeticalTitle = "ORDENADO ALFABETICAMENTE";
    public const string ExpirationDateTitle = "ORDENADO POR VENCIMIENTO";
    public const string HiddenCurrencyHeader = "Moneda";

    // Extraído del archivo de referencia de Félix.
    public const string InsFillRgb = "FFC000";

    // Extraído del archivo de referencia de Félix.
    public const string OtherInsurerFillRgb = "00B0F0";

    public static IReadOnlyList<ExpirationsFelixTemplateColumn> VisibleColumns { get; } =
    [
        new("Número de Póliza", "Número de Póliza", 31.11D),
        new("Nombre del Asegurado", "Nombre del Tomador", 76.78D),
        new("Aseguradora", "Aseguradora", 20.11D),
        new("Vencimiento", "Fecha Hasta", 18.78D, "d/m/yyyy"),
        new("Prima", "Prima", 18.67D, "#,##0.00"),
        new("Período Pago", "Período Pago", 13.11D),
        new("Placa", "Placa/Folio", 20.78D)
    ];
}
