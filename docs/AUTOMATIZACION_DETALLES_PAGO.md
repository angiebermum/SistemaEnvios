# Automatización de detalles de pago

## Objetivo

La aplicación puede transformar un reporte general de comisiones `.xlsx` en un archivo individual por pestaña, asociarlo al corredor configurado y dejarlo listo para el flujo existente de Outlook.

> La generación de archivos no envía correos.  
> El envío solo ocurre al presionar `Enviar seleccionados` o `Enviar todos`.

## Flujo operativo

1. En `Corredores`, editar cada corredor y configurar sus pestañas y rebajos.
2. En la pantalla principal, presionar `Seleccionar Excel general`.
3. La aplicación analiza todas las pestañas y sus montos CRC/USD.
4. Si una pestaña no tiene asociación, se abre el diálogo de mapeo. Se puede seleccionar un corredor o crear uno nuevo.
5. Presionar `Generar archivos`.
6. Indicar el periodo y revisar la vista previa de carpeta, archivo y cantidades.
7. Confirmar la generación. Si la carpeta ya existe, confirmar o cancelar el reemplazo de los archivos con los mismos nombres.
8. Revisar los adjuntos automáticos por corredor.
9. Solo cuando corresponda, seleccionar corredores y usar el envío existente.

La generación y el envío son operaciones distintas. Enviar usa los archivos ya generados, valida sus hashes y nunca vuelve a calcularlos.

## Relación corredor-pestaña

`Broker.AssociatedWorksheetNames` conserva los nombres originales. La resolución:

- quita espacios externos;
- ignora mayúsculas y minúsculas;
- exige igualdad completa;
- no relaciona `AAV` con `AAV Especial`;
- bloquea una pestaña asignada a dos corredores.

Un corredor puede tener cualquier cantidad de pestañas. Cada pestaña genera un archivo y todos los archivos del mismo `Broker.Id` se incorporan al mismo correo.

Ejemplo:

```text
Corredor sintético
├── AR
├── AR2
└── ANDRES

Resultado:
Detalle de pago - Corredor sintético - AR - IIQ julio 2026.xlsx
Detalle de pago - Corredor sintético - AR2 - IIQ julio 2026.xlsx
Detalle de pago - Corredor sintético - ANDRES - IIQ julio 2026.xlsx
```

## Generación y fidelidad Excel

La implementación usa `DocumentFormat.OpenXml` 3.5.1, paquete de código abierto, sin automatización COM de Excel.

Por cada pestaña:

1. copia físicamente el libro original a una ubicación temporal;
2. abre la copia como paquete Open XML;
3. elimina las demás hojas y sus partes;
4. conserva la hoja seleccionada y la renombra `Detalle`;
5. agrega `Monto de factura`;
6. vuelve a abrir y valida las dos hojas;
7. calcula SHA-256;
8. mueve el archivo al destino final.

La hoja seleccionada no se reconstruye celda por celda. Permanecen sus valores, fórmulas, estilos, dimensiones, combinaciones, filas/columnas ocultas, configuración de impresión y relaciones de dibujo soportadas por Open XML. Los estilos nuevos se agregan al final de la tabla de estilos, por lo que los índices originales no cambian.

El libro fuente se abre para lectura durante el análisis y siempre se modifica una copia. La prueba de fidelidad compara el XML completo de la hoja elegida antes y después.

La generación fiel admite `.xlsx`. Los formatos `.xls` y `.xlsm` se bloquean para evitar pérdida silenciosa de formato binario o macros. Los adjuntos manuales existentes continúan aceptando `.xlsx` y `.xls`.

## Análisis estándar y FLM

`StandardCommissionWorksheetAnalyzer` localiza dinámicamente:

- encabezado de moneda;
- encabezado de comisión del corredor;
- filas de CRC/colones;
- filas de USD/dólares;
- valores numéricos de comisión.

No usa celdas fijas.

`FlmCommissionWorksheetAnalyzer` reconoce un diseño de resumen mediante etiquetas y encabezados de moneda. Tolera la etiqueta histórica `Monto bruto comison`. La selección del analizador usa la estructura detectada, no el nombre de la pestaña; `FLM` solo se conserva como nombre de negocio y advertencia visual.

Si no se identifica con seguridad una moneda, etiqueta o importe, se bloquea toda la generación antes de crear archivos finales.

## Rebajos

Cada `BrokerDeduction` incluye:

```json
{
  "Id": "4c2c6265-7390-4d3d-95ef-4a6e4045718b",
  "Description": "Ahorro",
  "Amount": 15000.00,
  "Currency": "CRC",
  "ApplicationType": "PayableAmount",
  "DisplayOrder": 0
}
```

Las descripciones son libres. La interfaz permite agregar, editar y eliminar. El monto almacenado es positivo; la hoja lo presenta como descuento. Se valida descripción, decimal mayor o igual que cero, moneda y tipo.

Tipos:

- `GrossCommission`: reduce la base antes del mínimo, IVA y retención.
- `PayableAmount`: se aplica después de IVA y retención.

No hay conversión ni traslado automático entre monedas. Un ajuste entre monedas debe registrarse manualmente como rebajo en la moneda destino.

## Cálculos y mínimos

Cada moneda se calcula de forma independiente y se redondea a dos decimales con `MidpointRounding.AwayFromZero`.

```text
Comisión ajustada = comisión original - rebajos al bruto
IVA                = comisión ajustada × 13 %
Monto factura      = comisión ajustada + IVA
Retención          = comisión ajustada × 2 %
Antes de rebajos   = monto factura - retención
Monto depositado   = antes de rebajos - rebajos al pago
```

Mínimos sobre la comisión ajustada:

- CRC: menos de `₡15,000.00` no se paga; exactamente `₡15,000.00` sí.
- USD: menos de `$30.00` no se paga; exactamente `$30.00` sí.

Ejemplo CRC:

```text
Original                  ₡100,000.00
Rebajo bruto              -₡10,000.00
Ajustado                   ₡90,000.00
IVA                        ₡11,700.00
Factura                   ₡101,700.00
Retención                  -₡1,800.00
Rebajos al pago           -₡35,000.00
Depositado                 ₡64,900.00
```

Ejemplo USD:

```text
Original                     $100.00
Rebajo bruto                 -$10.00
Ajustado                      $90.00
IVA                           $11.70
Factura                      $101.70
Retención                     -$1.80
Rebajos al pago              -$20.00
Depositado                    $79.90
```

Si no existen comisiones, o si el monto ajustado está bajo el mínimo, la presentación financiera completa queda en `0.00`, incluidos los rebajos aplicados. El snapshot conserva el monto original para auditoría.

```text
Monto bruto comisión          0.00
Rebajos al monto bruto        0.00
Monto bruto ajustado          0.00
IVA 13%                       0.00
Monto factura                 0.00
Retención 2%                  0.00
Rebajos al monto a pagar      0.00
Monto depositado              0.00
```

Un monto ajustado negativo causado por rebajos o un depósito negativo bloquea la generación. Una comisión fuente negativa se advierte y su bloque se presenta en cero, sin conversión.

## Carpeta y nombres

La carpeta se obtiene con `Environment.SpecialFolder.DesktopDirectory`, compatible con escritorios redirigidos:

```text
{Escritorio real}\{Periodo}\
```

Formato:

```text
Detalle de pago - {Corredor} - {Pestaña} - {Periodo}.xlsx
```

Los componentes sustituyen `< > : " / \ | ? *` por guiones. El periodo se rechaza si está vacío, excede 80 caracteres, contiene caracteres inválidos, usa un nombre reservado o termina en punto/espacio.

Si la carpeta existe, la aplicación pide confirmación. Solo reemplaza archivos cuyos nombres coinciden con la nueva generación; conserva otros archivos.

## Persistencia y migración

El esquema actual es `DataSchemaVersion = 2`.

Los JSON antiguos siguen cargando porque las propiedades nuevas tienen valores predeterminados y la normalización garantiza listas no nulas:

```json
{
  "DataSchemaVersion": 2,
  "Brokers": [
    {
      "Id": "f0c80e1a-64d0-4e45-814e-d8fe78261dbc",
      "Name": "Corredor sintético",
      "PrimaryEmailAddresses": ["synthetic@example.com"],
      "Assistants": [],
      "AssociatedWorksheetNames": ["AR", "AR2", "ANDRES"],
      "Deductions": [
        {
          "Id": "4c2c6265-7390-4d3d-95ef-4a6e4045718b",
          "Description": "Ahorro",
          "Amount": 15000.00,
          "Currency": "CRC",
          "ApplicationType": "PayableAmount",
          "DisplayOrder": 0
        }
      ],
      "IsActive": true
    }
  ]
}
```

No se eliminan asistentes, correos, estado, marcas de revisión ni propiedades previas.

## Historial y snapshots

Las generaciones se guardan atómicamente en:

```text
%LOCALAPPDATA%\ECSCommissionsMailer\generaciones-detalles-pago.json
```

Cada lote registra:

- ID, periodo, fecha, estado;
- ruta y SHA-256 del reporte general;
- carpeta;
- archivos y SHA-256;
- corredor y pestaña;
- analizador;
- cálculos CRC/USD;
- mínimos y observaciones;
- copia de cada rebajo aplicado;
- corredores enviados o fallidos.

Estados: `Generated`, `ReadyToSend`, `Sent`, `PartialSend`, `Failed`.

Los snapshots contienen copias de los rebajos. Editar la configuración posterior no cambia generaciones anteriores.

Los archivos se generan en staging. Si falla una hoja, validación, movimiento o persistencia del historial, se eliminan los nuevos archivos y se restauran los reemplazados.

## Integración con envío

Los adjuntos se muestran en dos grupos:

- `Generados automáticamente`;
- `Agregados manualmente`.

Antes de enviar un grupo generado, la aplicación comprueba:

- generación activa;
- `BrokerId`;
- conjunto completo de archivos del corredor;
- existencia;
- SHA-256 sin cambios.

El `EmailSendRequest` contiene todos los adjuntos del `BrokerSendItem`, por lo que tres pestañas del mismo corredor producen un solo `MailItem` con tres archivos. Los destinatarios, asistentes, CC, firma, revisión y secuencia de Outlook siguen usando el flujo existente.

La generación no contiene ninguna llamada a `MailItem.Send`. Solo `OutlookEmailService`, invocado por los botones explícitos de envío, puede ejecutar esa llamada.

## Errores y advertencias

Bloquean:

- libro ausente, ilegible o no `.xlsx`;
- libro sin pestañas;
- pestaña sin asociación o asociación duplicada;
- encabezados/moneda/monto no interpretables;
- rebajo inválido;
- resultado financiero negativo;
- nombre inválido;
- error de copia, reapertura, hash o historial;
- archivo generado faltante, cambiado o de otro corredor antes de enviar.

Advierten:

- corredor sin correo;
- moneda sin comisión;
- comisión negativa;
- comisión bajo el mínimo;
- formato FLM;
- rebajo de una moneda sin comisión;
- carpeta ya existente;
- corredor con varias pestañas.

