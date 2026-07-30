# Pruebas de automatización de detalles de pago

## Regla de seguridad

> La generación de archivos no envía correos.  
> El envío solo ocurre al presionar `Enviar seleccionados` o `Enviar todos`.

No ejecutar una prueba de envío real sin destinatarios y cuenta expresamente autorizados. Las pruebas automatizadas no llaman `MailItem.Send`.

## Pruebas automatizadas

Desde la raíz:

```powershell
dotnet --info
dotnet restore .\ECS.CommissionsMailer.slnx
dotnet build .\ECS.CommissionsMailer.csproj -c Debug
dotnet build .\ECS.CommissionsMailer.csproj -c Release
dotnet test .\Tests\ECS.CommissionsMailer.Tests\ECS.CommissionsMailer.Tests.csproj -c Debug
.\bin\Debug\net10.0-windows\ECS Envío de Correos.exe --self-test
```

Cobertura principal de xUnit:

- mapeo exacto, varias pestañas, duplicados, nueva pestaña y no coincidencia parcial;
- JSON antiguo y ciclo crear/editar/eliminar/persistir rebajos;
- CRC, USD, moneda ausente, mínimos exactos e inferiores;
- rebajos al bruto y al pago;
- depósito negativo, comisión negativa y redondeo;
- analizadores estándar y FLM estructural;
- un archivo por pestaña y tres archivos agrupados por `BrokerId`;
- fidelidad del XML de la hoja original;
- hojas exactas `Detalle` y `Monto de factura`;
- ambos bloques y moneda ausente completamente en cero;
- SHA-256, archivo faltante e historial inmutable;
- generación sin Outlook.

El self-test instalado agrega comprobaciones de mapeo, cálculos, mínimos, bloques cero y sanitización a las comprobaciones existentes.

## Preparar un libro sintético manual

Crear un `.xlsx` sin datos reales con tres pestañas:

```text
SYN
SYN2
FLM
```

Para `SYN` y `SYN2`:

| Moneda | Comisión corredor |
|---|---:|
| CRC | 100000 |
| USD | 100 |

Para `FLM`, usar dos bloques:

| Etiqueta | Monto |
|---|---:|
| COLONES | |
| Monto bruto comison | 25000 |
| DÓLARES | |
| Monto bruto comisión | 50 |

Agregar a `SYN` formato visible, una fórmula, una combinación, una fila oculta, ancho de columna, alto de fila y configuración de impresión para comprobar fidelidad.

## Prueba manual paso a paso

1. Abrir la aplicación sin argumentos.
2. Crear un corredor sintético sin datos reales.
3. En `Pestañas asociadas`, agregar `SYN` y `SYN2`.
4. En `Rebajos`, agregar:
   - `Ajuste`, CRC, 10000, `Monto bruto de comisión`, pestaña `SYN`;
   - `Ahorro`, CRC, 15000, `Monto a pagar`, pestaña `SYN`;
   - `Adelanto`, USD, 20, `Monto a pagar`, pestaña `SYN2`.
5. Guardar.
6. Seleccionar el libro sintético.
7. Confirmar que se detectan tres pestañas y que `FLM` aparece sin asociación.
8. Presionar `Generar archivos`.
9. En el diálogo de asociación, asignar `FLM` al mismo corredor.
10. Indicar `IQ validación 2026`.
11. Revisar la vista previa:
    - tres pestañas;
    - un corredor;
    - tres archivos;
    - carpeta del escritorio;
    - nombre con corredor, pestaña y periodo.
12. Presionar `Generar`.
13. Confirmar que Outlook no se abre y no aparece ningún correo.
14. Abrir la carpeta desde la aplicación.
15. Confirmar tres archivos:

```text
Detalle de pago - Corredor sintético - SYN - IQ validación 2026.xlsx
Detalle de pago - Corredor sintético - SYN2 - IQ validación 2026.xlsx
Detalle de pago - Corredor sintético - FLM - IQ validación 2026.xlsx
```

16. Abrir cada archivo y comprobar que solo contiene:
    - `Detalle`;
    - `Monto de factura`.
17. En `Detalle`, comprobar valores, fórmula, estilo, color, borde, ancho, alto, combinación, fila oculta e impresión.
18. En `Monto de factura`, comprobar:
    - el conjunto comienza en `B2`: `COLONES` ocupa B:C y `DÓLARES` ocupa E:F, separados por la columna D;
    - bordes visibles alrededor de ambos cuadros;
    - la fila `Monto factura` resaltada en amarillo;
    - los encabezados `Ajustes al monto bruto` y `Deducciones` sin importe;
    - cada rebajo mostrado una sola vez, junto a su tipo.
19. Comprobar que el rebajo bruto reduce la base de IVA y retención.
20. Comprobar que los rebajos al pago aparecen después de la retención.
21. Editar el libro sintético para dejar una moneda sin filas, regenerar en otro periodo y confirmar ocho importes `0.00` en ese bloque.
22. Probar CRC `14999.99` y `15000.00`.
23. Probar USD `29.99` y `30.00`.
    - Para los importes inferiores al mínimo, comprobar que la nota `Comisión acumulada por ser inferior al monto mínimo establecido.` aparece en negrita.
24. Configurar un rebajo al pago superior al disponible y confirmar que no se crea ningún archivo final.
25. Regenerar el mismo periodo y confirmar el diálogo de carpeta existente.
26. Cancelar y comprobar que no aparecen nombres `(1)`, `(2)` ni `Copia`.
27. Confirmar en la fila del corredor:
    - tres archivos en `Generados automáticamente`;
    - adjuntos manuales separados.
28. Quitar o modificar un archivo generado y presionar envío; confirmar que la validación lo bloquea.
29. Regenerar para restaurar hashes.
30. Llegar hasta la confirmación final de envío y elegir `No`.

No se debe ejecutar un envío real durante esta validación.

## Prueba de migración

1. Respaldar `%LOCALAPPDATA%\ECSCommissionsMailer`.
2. Iniciar con un `configuracion.json` anterior sin `DataSchemaVersion`, `AssociatedWorksheetNames` ni `Deductions`.
3. Confirmar que abre sin error.
4. Editar un corredor y guardar.
5. Confirmar listas nuevas y `DataSchemaVersion: 3`.
6. Confirmar que correos, asistentes, activo, revisión, asunto, cuerpo, CC y firma siguen presentes.

## Prueba de rebajo por pestaña

1. Asociar `AQO` y `AQM (HC)` al mismo corredor.
2. Crear un rebajo en CRC y seleccionar únicamente `AQO`.
3. Generar ambos detalles de pago.
4. Confirmar que el rebajo y su importe aparecen en `AQO`.
5. Confirmar que `AQM (HC)` no contiene ni aplica ese rebajo.

## Prueba de agrupación y envío seguro

1. Generar `SYN`, `SYN2` y `FLM` para el mismo corredor.
2. Revisar que la fila muestre tres adjuntos automáticos.
3. Abrir la confirmación de `Enviar seleccionados`.
4. Confirmar que el resumen indica un correo.
5. Cancelar.

El agrupamiento es por `Broker.Id`, no por nombre visible. Dos corredores con el mismo nombre no mezclan adjuntos.

## Prueba de historial

Abrir:

```text
%LOCALAPPDATA%\ECSCommissionsMailer\generaciones-detalles-pago.json
```

Comprobar ID, periodo, fuente/hash, archivos/hash, pestaña, corredor, cálculos, mínimos, rebajos y estado. Editar un rebajo del corredor y comprobar que el snapshot previo no cambia.

## UI smoke

```powershell
.\bin\Debug\net10.0-windows\ECS Envío de Correos.exe --ui-smoke-test
```

Revisar:

```text
%TEMP%\ECSCommissionsMailer-ui-smoke.png
%TEMP%\ECSCommissionsMailer-ui-smoke-minimum.png
%TEMP%\ECSCommissionsMailer-ui-binding-errors.log
%TEMP%\ECSCommissionsMailer-ui-smoke-result.txt
```

No deben existir errores de binding. La pantalla mínima debe permitir llegar al selector y botón de generación.

## Publicación e instalador

```powershell
dotnet publish .\ECS.CommissionsMailer.csproj -c Release -r win-x64 --self-contained true
.\Installer\Build-Installer.cmd
```

No instalar automáticamente el MSI. Inspeccionar que la publicación contenga `DocumentFormat.OpenXml.dll` y sus dependencias, y que no incluya reportes, detalles generados, historial ni archivos de prueba.
