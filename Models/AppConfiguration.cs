namespace ECS.CommissionsMailer.Models;

public sealed class AppConfiguration
{
    public const int CurrentDataSchemaVersion = 3;
    public const int CurrentEmailDirectorySeedVersion = 3;
    public const string InitialSubject = "DETALLE DE COMISIONES IIQ DE JUNIO";

    public const string InitialMessage = """
        ¡Buenas!

        Estimado(a)

        En el presente se adjunta el detalle de comisiones a facturar de la IIQ del mes de JUNIO 2026; recordar que el pago se hace contra factura.

        Por favor enviar las facturas mañana antes de las 2:00 p. m. para lograr el pago correspondiente.

        Cualquier consulta estoy para servirle.

        Saludos cordiales,
        """;

    public string DefaultSubject { get; set; } = InitialSubject;
    public string DefaultMessage { get; set; } = InitialMessage;
    public string? SignatureImagePath { get; set; }
    public int DataSchemaVersion { get; set; } = CurrentDataSchemaVersion;
    public int EmailDirectorySeedVersion { get; set; }
    public string? EmailDirectorySeedId { get; set; }
    public List<string> CommonCcAddresses { get; set; } = [];
    public List<Broker> Brokers { get; set; } = [];

    public static AppConfiguration CreateDefault() => new();
}
