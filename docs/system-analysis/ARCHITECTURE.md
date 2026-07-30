# Arquitectura actual

## Estilo arquitectónico

La solución es una aplicación WPF monolítica con:

- binding XAML y modelos observables;
- code-behind como controlador y ViewModel implícito;
- servicios concretos creados manualmente;
- persistencia local JSON;
- integración COM con Outlook ejecutada en hilos STA.

No implementa MVVM completo porque `MainWindow.xaml.cs` expone estado enlazable, maneja eventos, coordina servicios y contiene reglas de flujo. No hay clases `*ViewModel`, comandos WPF ni contenedor de inyección de dependencias.

## Diagrama real

```text
App.xaml.cs
    |
    v
MainWindow.xaml + MainWindow.xaml.cs
    |                 |
    |                 +--> Views code-behind (edición, inactivos, reenvío, diálogos)
    |
    +--> ConfigurationService / SessionService
    |         |
    |         +--> AtomicJsonFile
    |         +--> DirectoryMigrationService
    |         +--> %LOCALAPPDATA%\ECSCommissionsMailer\*.json
    |
    +--> EmailValidationService / SignatureImageService
    |
    +--> OutlookEmailService --> StaTaskRunner --> Outlook Classic COM --> MailItem.Send()
    |
    +--> AttachmentArchiveService --> ArchivosEnviados\
    |
    +--> FileLogger --> Logs\app.log
```

## Inicialización

`App.OnStartup` (`App.xaml.cs:12-125`) reconoce cuatro modos:

1. `--self-test`: ejecuta `Verification/SelfTestRunner`.
2. `--outlook-draft-test`: abre y descarta un borrador controlado.
3. `--outlook-diagnostic`: comprueba Outlook y escribe resultado en `%TEMP%`.
4. Inicio normal: configura logging de bindings si se pidió `--ui-smoke-test`, construye `MainWindow` y la muestra.

El inicio normal captura excepciones globales solo alrededor de la construcción/muestra inicial y presenta un diálogo (`App.xaml.cs:89-124`). No hay `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException` ni manejador global posterior.

## Presentación

`MainWindow.xaml` contiene la pantalla operativa: estado de Outlook, asunto, mensaje, CC, firma, directorio de corredores, adjuntos, progreso y comandos de envío.

`MainWindow.xaml.cs` tiene 1.284 líneas y concentra:

- estado enlazable;
- construcción de servicios;
- carga/migración;
- CRUD de corredores;
- firma;
- selección de archivos;
- reglas de lote y revisión;
- coordinación de Outlook;
- archivado;
- historial;
- persistencia;
- mensajes y progreso.

Las ventanas secundarias usan code-behind. `ResendWindow` recibe siete servicios/objetos de coordinación y gestiona validación, envío, archivado e historial en 314 líneas.

## Dominio y modelos

- `AppConfiguration`: valores predeterminados, firma, versión de semilla, CC y corredores.
- `Broker`: identidad, principales, asistentes, activo y revisión.
- `BrokerAssistant`: nombre, correo y estado.
- `BrokerSendItem`: modelo observable de una fila/lote; también se serializa en sesión.
- `EmailSendRequest`: DTO inmutable de envío.
- `EmailSendResult`: resultado técnico inmediato.
- `SentEmailRecord`: historial local.
- `CurrentSession`: borrador operativo.

Existe mezcla entre modelo de dominio, estado de UI y persistencia en `BrokerSendItem`: propiedades observables, estado de envío, adjuntos, revisión y atributos JSON coexisten. Esto dificulta aislar pruebas y evolucionar el formato.

## Servicios y comunicación

`ConfigurationService` carga JSON, normaliza y ejecuta `DirectoryMigrationService`. La migración puede crear respaldos, importar la semilla empaquetada, dividir registros históricos conocidos y guardar configuración/sesión.

`EmailValidationService`:

- separa direcciones por `;`, coma o salto;
- normaliza con `System.Net.Mail.MailAddress`;
- elimina duplicados;
- resuelve principales/asistentes/CC;
- valida asunto, cuerpo, revisión, firma y adjuntos.

`OutlookEmailService`:

- ejecuta toda automatización en un hilo STA mediante `StaTaskRunner`;
- crea/valida Outlook;
- fija explícitamente la cuenta;
- crea destinatarios y exige `ResolveAll`;
- adjunta archivos;
- inserta firma por CID/MAPI;
- llama `MailItem.Send`;
- clasifica fallos y libera objetos COM.

`AttachmentArchiveService` copia adjuntos después de que Outlook acepta `Send`.

## Inyección de dependencias

No hay DI de framework. Se utiliza inyección manual parcial:

- `App` entrega `AppDataPaths` y `FileLogger` a `MainWindow`.
- `MainWindow` instancia servicios concretos.
- `ResendWindow` recibe servicios concretos desde `MainWindow`.

Esto permite algo de reutilización, pero no permite sustituir fácilmente Outlook, reloj, filesystem o persistencia en pruebas.

## Acoplamiento y testabilidad

### Alto acoplamiento

- `MainWindow` conoce todos los servicios y modelos.
- `ResendWindow` repite el flujo de enviar, archivar y registrar.
- `DirectoryMigrationService` contiene reglas específicas de personas/registros junto con infraestructura de migración.
- `Build-Installer.ps1`, el proyecto WiX y `Sync-InstallerData.ps1` comparten convenciones de ruta y versión.

### Componentes difíciles de probar

- Los handlers `async void` solo son invocables desde UI.
- Outlook es una dependencia COM concreta, sin interfaz adaptadora.
- El filesystem y `DateTime.Now` están usados directamente.
- No hay unidad transaccional para “enviar + registrar + archivar”.
- Los diálogos estáticos (`AppDialog`) impiden simular decisiones del usuario.

### Componentes con demasiadas responsabilidades

| Componente | Evidencia |
|---|---|
| `MainWindow.xaml.cs` | 1.284 líneas y más de 35 métodos de coordinación/UI |
| `OutlookEmailService.cs` | 636 líneas: descubrimiento, diagnóstico, cuenta, formato, envío, COM y reporte |
| `DirectoryMigrationService.cs` | 423 líneas: validación de semilla, respaldo, reglas de corrección, mezcla y sincronización |
| `ResendWindow.xaml.cs` | envío, validación, archivo, historial y UI |

## Riesgos de flujo

1. `MailItem.Send()` se considera éxito inmediato (`OutlookEmailService.cs:376-379`). Eso confirma aceptación por Outlook, no entrega final.
2. El historial se persiste después de completar todo el lote (`MainWindow.xaml.cs:821-836`). Si falla el guardado, ya pueden existir correos enviados sin registro durable.
3. El archivado se realiza después del envío (`MainWindow.xaml.cs:867-881`). Su error no cambia `WasSuccessful`, y el resumen cuenta el correo como enviado.
4. El logger ignora silenciosamente cualquier fallo (`FileLogger.cs:29-32`), por lo que una máquina con problemas de permisos puede perder diagnóstico sin aviso.

## Arquitectura recomendada, no implementada

Separar gradualmente:

```text
Views
  -> ViewModels / Commands
    -> Application services (SendBatch, Resend, ManageDirectory)
      -> Interfaces de Outlook, persistencia, archivo y logging
        -> Adaptadores COM/JSON/filesystem
```

La prioridad no es una reescritura. Primero debe estabilizarse el contrato transaccional del envío y la distribución; después pueden extraerse casos de uso desde `MainWindow`.

