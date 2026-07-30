# Inventario funcional

## Resumen

| Funcionalidad | Pantalla/componente | Estado aparente |
|---|---|---|
| Inicialización y recuperación | `App`, `MainWindow` | Funcional |
| Migración de directorio inicial | `DirectoryMigrationService` | Funcional, altamente específica |
| Conexión/diagnóstico Outlook | Pantalla principal y CLI | Funcional en código; integración real no validada en este análisis |
| CRUD de corredores/asistentes | Pantalla principal y editores | Funcional |
| Activar/desactivar corredores | Pantalla principal/inactivos | Funcional |
| Configurar firma | Pantalla principal | Funcional |
| Preparar lote y adjuntar Excel | Pantalla principal | Funcional |
| Enviar seleccionados/todos | Pantalla principal | Funcional con riesgo post-envío |
| Historial y archivo local | Pantalla principal/LocalAppData | Parcial ante fallos post-envío |
| Reenvío | `ResendWindow` | Funcional con duplicación de lógica |
| Autocomprobación | `SelfTestRunner` | Funcional, no es proyecto de pruebas |
| Instalación MSI | WiX | Compila; actualización real no probada |
| Actualización automática | — | No existe |

## 1. Inicialización y recuperación

- Archivos: `App.xaml.cs`, `MainWindow.xaml.cs:51-76`, `ConfigurationService.cs`, `SessionService.cs`.
- Entrada: argumentos CLI, archivos JSON locales y semilla empaquetada.
- Flujo: crea carpetas, logger y servicios; carga configuración/sesión/historial; aplica migración; arma filas activas; carga firma; comprueba Outlook.
- Modifica: puede crear directorios, configuración, respaldo de migración y archivos `.broken-*`.
- Errores: el inicio normal muestra un diálogo; JSON corrupto se aparta. Falta de semilla puede impedir abrir.
- Dependencias: filesystem local, WPF y semilla `Data`.
- Estado: funcional y cubierto parcialmente por `--self-test`.

## 2. Migración del directorio

- Archivos: `Services/DirectoryMigrationService.cs`, `Data/correos-iniciales.v3.json`.
- Entrada: configuración local previa, sesión, historial y semilla versión 3.
- Flujo: valida semilla, respalda datos, aplica valores empaquetados, divide registros conocidos incorrectamente fusionados, retira sembrados obsoletos, mezcla directorio y sincroniza sesión.
- Salida: JSON actualizados y respaldo bajo `Backups`.
- Errores: semilla ausente/inválida impide la carga; fallos de disco se propagan al inicio.
- Estado: funcional según autocomprobación; reglas específicas embebidas elevan el costo de futuras migraciones.

## 3. Conectar y diagnosticar Outlook

- Archivos: `MainWindow.xaml.cs:217-307`, `OutlookEmailService.cs`, `OutlookApplicationFactory.cs`, `OutlookEnvironmentInspector.cs`, `OutlookFailureClassifier.cs`.
- Entrada: Outlook Classic instalado, perfil y cuenta configurados.
- Flujo: inspecciona ambiente, crea/obtiene `Outlook.Application`, consulta cuentas, guarda el correo conectado en memoria y actualiza UI.
- Salida: estado visual y log.
- Errores: Outlook no registrado, bitness/COM, permisos/elevación, perfil/cuenta o diálogo modal.
- Estado: manejo defensivo implementado; no se ejecutó la prueba interactiva porque puede abrir Outlook.

## 4. Administrar corredores

- Archivos: `MainWindow.xaml.cs:333-445`, `BrokerEditorWindow.xaml.cs`, `AssistantEditorWindow.xaml.cs`, `InactiveBrokersWindow.xaml.cs`.
- Entrada: nombre, uno o más correos principales, asistentes, estado y nota de revisión.
- Flujo: editor clona o crea, valida, persiste configuración, reconstruye filas y guarda sesión.
- Modifica: `configuracion.json`, `sesion-actual.json`.
- Condiciones de error: direcciones inválidas/duplicadas, registro no encontrado, fallo de disco.
- Estado: funcional.

## 5. Configurar firma

- Archivos: `MainWindow.xaml.cs:448-516`, `SignatureImageService.cs`.
- Entrada: PNG/JPG/JPEG local.
- Flujo: valida formato/dimensiones, copia a `Assets\Firma`, guarda ruta administrada, carga preview y elimina copia anterior.
- Modifica: configuración y archivos de firma administrados.
- Errores: formato, ruta, permisos, archivo ilegible.
- Estado: funcional y comprobado con imágenes temporales.

## 6. Preparar lote

- Archivos: `MainWindow.xaml.cs:532-706`, `BrokerSendItem.cs`, `EmailValidationService.cs`.
- Entrada: selección de corredores, asunto, cuerpo, CC, archivos `.xlsx`/`.xls`.
- Flujo: agrega archivos únicos por ruta, calcula preparación, permite limpiar el lote y conserva/restablece texto.
- Modifica: `sesion-actual.json`.
- Errores: archivos eliminados/bloqueados, extensión no permitida, destinatarios o texto incompletos.
- Estado: funcional.

## 7. Validar destinatarios y revisión

- Archivos: `EmailValidationService.cs`, `MainWindow.xaml.cs:594-666`, `MainWindow.xaml.cs:902-959`.
- Entrada: directorio actual, CC general, firma, adjuntos y confirmación del usuario.
- Flujo: normaliza correos, prioriza TO sobre CC, elimina duplicados, exige revisión explícita cuando corresponde y excluye filas inválidas.
- Salida: `EmailSendRequest` por corredor.
- Errores: correo inválido, duplicado principal/asistente, sin TO, sin asunto/cuerpo/Excel, firma inválida o revisión no confirmada.
- Estado: funcional y con varias comprobaciones internas.

## 8. Envío por Outlook

- Archivos: `MainWindow.xaml.cs:784-865`, `OutlookEmailService.cs:189-389`.
- Entrada: solicitudes ya validadas.
- Flujo: un hilo STA abre Outlook, resuelve cuenta, procesa secuencialmente cada solicitud, agrega TO/CC/adjuntos/firma y llama `MailItem.Send`.
- Salida: `EmailSendResult`, progreso y log.
- Errores: conexión, cuenta, resolución de destinatarios, adjuntos, firma, COM y políticas de Outlook.
- Estado: implementación funcional; no se envió correo real durante esta auditoría.
- Riesgo: el éxito significa que `Send()` no lanzó excepción, no que el servidor o destinatario confirmó entrega.

## 9. Historial y archivo de adjuntos

- Archivos: `MainWindow.xaml.cs:821-899`, `AttachmentArchiveService.cs`, `SessionService.cs`.
- Entrada: resultado de Outlook y archivos enviados.
- Flujo: copia Excel a una carpeta con fecha/corredor, crea registro y finalmente guarda toda la colección.
- Modifica: `ArchivosEnviados`, `envios-recientes.json`, `sesion-actual.json`.
- Estado: parcial ante errores posteriores a `Send`.
- Riesgo: si falla archivo o persistencia, el correo puede estar enviado pero el registro local queda incompleto o ausente; reintentar puede duplicarlo.

## 10. Consultar historial, limpiar y reenviar

- Archivos: `Views/ResendWindow.xaml.cs`.
- Entrada: registro histórico, directorio actual, CC actual, asunto editado y adjuntos archivados/reemplazados.
- Flujo: recalcula destinatarios desde el corredor vigente, permite reemplazar archivos, confirma revisión y crea un correo nuevo relacionado con el original.
- Modifica: historial y archivo local; limpiar historial no elimina adjuntos archivados.
- Errores: archivo archivado faltante, destinatarios inválidos, Outlook, archivo o persistencia.
- Estado: funcional con lógica duplicada respecto al envío principal.

## 11. Modos de diagnóstico

| Argumento | Efecto | Riesgo de envío |
|---|---|---|
| `--self-test` | 34 comprobaciones con directorios temporales | No envía |
| `--ui-smoke-test` | Abre UI, captura PNG y cierra | No envía |
| `--outlook-diagnostic` | Muestra/descarta borrador de prueba | No llama `Send` |
| `--outlook-draft-test <correo> <firma> <excel>` | Muestra/descarta borrador completo | No llama `Send` |

## Funciones ausentes

- autenticación/autorización;
- base de datos o sincronización central;
- confirmación de entrega;
- cola durable e idempotencia;
- actualización automática;
- telemetría central;
- exportación/importación explícita de configuración;
- pruebas automatizadas de UI/Outlook ejecutadas en CI.

