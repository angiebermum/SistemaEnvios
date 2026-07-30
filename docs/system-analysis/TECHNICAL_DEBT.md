# Deuda técnica y riesgos

## Resumen de severidad

| Severidad | Cantidad |
|---|---:|
| Críticos | 3 |
| Altos | 6 |
| Medios | 7 |
| Bajos | 4 |

## Críticos

### C1. Los accesos directos ejecutan una versión instalada antigua

- Evidencia: accesos de escritorio/Inicio apuntan a `C:\Program Files\ECS Envío de Correos\ECS Envío de Correos.exe`; archivo y registro indican 1.0.0. El repositorio tiene MSI/publicación 1.0.3.
- Ubicación: `Installer/Package.wxs:57-80`; instalación y registro de Windows.
- Impacto: el usuario usa código anterior aunque exista un build nuevo.
- Causa probable: el MSI 1.0.3 no fue instalado; coexistencia de múltiples salidas.
- Recomendación: aprobar un único MSI, instalar/actualizar, comprobar versión desde archivo y UI, y retirar/archivar salidas antiguas.
- Prioridad: inmediata.

### C2. El build de distribución depende de datos locales y puede empaquetar contenido equivocado

- Evidencia: `Installer/Build-Installer.ps1:18` llama a `Sync-InstallerData.ps1`; este lee `%LOCALAPPDATA%\ECSCommissionsMailer\configuracion.json` (`Sync-InstallerData.ps1:3-4`) y reescribe la semilla/firma del repositorio.
- Impacto: destinatarios o firma de un perfil incorrecto pueden distribuirse; el release no es reproducible desde un commit.
- Causa: el perfil del operador actúa como fuente de release.
- Recomendación: entrada de release explícita, revisada y versionada de forma segura; generar en staging, validar diff y hashes, no mutar el árbol durante build.
- Prioridad: inmediata.

### C3. Un fallo después de enviar puede dejar el correo sin registro durable y provocar duplicados

- Evidencia: `MailItem.Send()` ocurre en `OutlookEmailService.cs:378`; el archivo y registro se realizan después en `MainWindow.xaml.cs:821-836` y `867-881`.
- Impacto: si falla disco/JSON tras enviar, la UI puede informar error general y un reintento puede enviar duplicado.
- Causa: “enviar, archivar y registrar” no tiene estado durable/idempotente.
- Recomendación: registrar intención con ID antes de enviar, persistir cada resultado inmediatamente, distinguir `AcceptedByOutlook` de `Archived/Recorded` y bloquear reintentos ambiguos.
- Prioridad: inmediata.

## Altos

### H1. Datos personales de destinatarios están en Git y en cada instalador

- Evidencia: `Data/correos-iniciales.v3.json` contiene 64 corredores, 65 principales, 10 asistentes y una CC; la búsqueda encontró correos en cuatro archivos rastreados.
- Impacto: exposición según visibilidad, clones, backups y paquetes.
- Causa: directorio operativo utilizado como semilla de código.
- Recomendación: clasificar datos, restringir repositorio/artefactos, considerar archivo cifrado o provisión separada y política de retención.

### H2. Versionado manual duplicado

- Evidencia: 1.0.3 aparece en `ECS.CommissionsMailer.csproj:20-23`, `.wixproj:6-8`, `Package.wxs:14-17`, `ECSInstallUI.wxs` y `Installer/README.md`.
- Impacto: MSI, binario, UI y documentación pueden discrepar.
- Recomendación: una propiedad de versión central y versionado derivado del build/commit.

### H3. Publicaciones antiguas y nombres históricos permanecen junto a salidas actuales

- Evidencia: `publish\win-x64` y `publish\win-x64-self-contained` contienen 1.0.0 `ECS.CommissionsMailer.exe`; `bin\Debug` conserva `SistemaEnvios.exe`.
- Impacto: copiar o comprimir la carpeta equivocada es fácil.
- Recomendación: salida única `artifacts/release/<version>`, limpieza verificada y manifiesto.

### H4. Orquestación principal excesivamente acoplada al code-behind

- Evidencia: `MainWindow.xaml.cs` tiene 1.284 líneas y construye/coordina todos los servicios; `ResendWindow` recibe siete dependencias.
- Impacto: cambios de flujo y pruebas tienen alto riesgo.
- Recomendación: extraer casos de uso de envío, directorio y sesión; después introducir ViewModels.

### H5. “Enviado” solo significa aceptación inmediata de Outlook

- Evidencia: resultado exitoso justo después de `MailItem.Send`, `OutlookEmailService.cs:376-379`.
- Impacto: fallos posteriores de transporte o rechazo no se reflejan.
- Recomendación: renombrar estado a “Entregado a Outlook” o integrar confirmación/seguimiento si el negocio lo exige.

### H6. Logging local sin rotación, protección ni señal de fallo

- Evidencia: append indefinido en `FileLogger.cs:15-27`; cualquier excepción se ignora en `29-32`.
- Impacto: crecimiento, datos personales en texto claro y pérdida silenciosa de diagnóstico.
- Recomendación: rotación/retención, minimización, permisos y aviso de degradación.

## Medios

### M1. No existe proyecto estándar de pruebas

- Evidencia: no hay referencia a `Microsoft.NET.Test.Sdk`, MSTest, NUnit o xUnit.
- Impacto: `dotnet test`/CI no descubren las 34 comprobaciones.
- Recomendación: mover casos puros a pruebas unitarias y conservar pocos smoke tests.

### M2. El perfil de publicación está ignorado

- Evidencia: `Properties/PublishProfiles/win-x64-self-contained.pubxml` existe, pero `*.pubxml` está ignorado.
- Impacto: una ruta de publicación depende del equipo.
- Recomendación: versionar un perfil sin secretos o eliminarlo y usar solo script oficial.

### M3. Build directo del `.wixproj` no limpia `PublishDir`

- Evidencia: `.wixproj:15-19` publica e incluye; la limpieza solo existe en `Build-Installer.ps1:25-27`.
- Impacto: archivos residuales pueden incluirse si se omite el wrapper.
- Recomendación: mover limpieza segura al target o publicar siempre a carpeta nueva.

### M4. Archivo fallido no altera el resultado de envío

- Evidencia: `CreateSentRecord` conserva `WasSuccessful = result.WasSuccessful` aun cuando falla `Archive`, `MainWindow.xaml.cs:867-898`.
- Impacto: resumen “sin errores” con auditoría incompleta.
- Recomendación: estados separados y resumen de advertencias post-envío.

### M5. Historial completo se reescribe en cada cambio

- Evidencia: `SessionService.cs:49-50`.
- Impacto: costo creciente y mayor ventana de fallo.
- Recomendación: almacén append-only o base local ligera con compactación.

### M6. Sin manejador global de excepciones después del inicio

- Evidencia: `App.xaml.cs` solo captura construcción; no hay `DispatcherUnhandledException`.
- Impacto: un handler no cubierto puede cerrar la aplicación.
- Recomendación: captura global con log y mensaje seguro, sin ocultar errores.

### M7. Validación de Excel solo superficial

- Evidencia: extensiones permitidas en `EmailValidationService.cs:9,243` y prueba de apertura.
- Impacto: archivo renombrado/corrupto puede enviarse.
- Recomendación: validar firma ZIP/OLE y, si importa, estructura mínima.

## Bajos

### B1. Archivo `.csproj.user` con nombre histórico

- Evidencia: `SistemaEnvios.csproj.user`, vacío e ignorado.
- Impacto: confusión local.
- Recomendación: retirar de forma consciente tras confirmar que Visual Studio no lo usa.

### B2. Proyecto diagnóstico fuera de la solución

- Evidencia: `.slnx` solo incluye aplicación e instalador.
- Impacto: no se compila en build de solución.
- Recomendación: incluirlo en una carpeta de solución o documentar comando obligatorio.

### B3. Constantes de asunto y cuerpo con fecha concreta

- Evidencia: `Models/AppConfiguration.cs:6-20`.
- Impacto: defaults quedan desactualizados si falla la semilla.
- Recomendación: sacar contenido operativo del binario.

### B4. Comentario de limpieza silenciosa

- Evidencia: catches vacíos en `AtomicJsonFile.cs:78-81`, `MainWindow.xaml.cs:478` y logger.
- Impacto: residuos difíciles de diagnosticar.
- Recomendación: métricas/advertencias no intrusivas.

## Pruebas y observabilidad

Cobertura medible: no disponible. No hay instrumentación de cobertura. La batería interna cubre buena parte de servicios puros, pero no permite asignar un porcentaje defendible a la funcionalidad completa.

Prioridad de pruebas:

1. estado durable e idempotencia alrededor de envío;
2. actualización MSI 1.0.0 -> 1.0.3+;
3. resolución de cuenta/destinatarios Outlook;
4. migraciones de semilla y preservación de datos locales;
5. fallos de disco durante configuración, historial y archivo;
6. ViewModels/casos de uso extraídos;
7. UI smoke y accesibilidad.

Observabilidad actual:

- log: `%LOCALAPPDATA%\ECSCommissionsMailer\Logs\app.log`;
- resultados diagnósticos: `%TEMP%\ECSCommissionsMailer-*.txt`;
- capturas UI smoke: `%TEMP%\ECSCommissionsMailer-ui-smoke*.png`.

El log aporta stack trace, HRESULT y ambiente Outlook, pero no tiene ID de lote persistente, rotación, nivel configurable ni exportación de diagnóstico.

