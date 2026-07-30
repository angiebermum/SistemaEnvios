# Plan de mejora priorizado

## Fase 0: estabilización

| Tarea | Prioridad | Complejidad | Riesgo | Dependencias | Resultado | Módulos |
|---|---|---|---|---|---|---|
| Instalar y verificar el MSI 1.0.3 en VM/equipo controlado | Crítica | Baja | Medio | Copia aprobada, permisos admin | Acceso directo abre versión esperada | `artifacts/installer`, WiX |
| Definir una única raíz de release y retirar ambigüedad | Crítica | Baja | Bajo | Decisión de proceso | No se puede confundir `bin`, `publish` y release | scripts/CI |
| Añadir manifiesto de release con versión, commit y hashes | Crítica | Media | Bajo | Versionado central | Artefacto verificable | build/installer |
| Separar la entrada de directorio del perfil `%LOCALAPPDATA%` | Crítica | Media | Medio | Fuente oficial aprobada | Build reproducible y auditable | `Sync-InstallerData.ps1` |
| Diseñar estado durable previo/posterior a `Send` | Crítica | Alta | Alto | Decisión de negocio sobre reintentos | Sin duplicados ambiguos | Outlook, historial, archivo |
| Centralizar versión de app/MSI/UI | Alta | Media | Medio | Política SemVer | Una sola versión consistente | csproj/WiX/README |

## Fase 1: correcciones funcionales

| Tarea | Prioridad | Complejidad | Riesgo | Dependencias | Resultado | Módulos |
|---|---|---|---|---|---|---|
| Separar estados “entregado a Outlook”, “registrado” y “archivado” | Alta | Media | Medio | Modelo durable | Mensajes exactos y recuperables | modelos, ventanas, servicios |
| Guardar resultado después de cada correo | Alta | Media | Medio | Persistencia revisada | Menor pérdida ante fallo intermedio | `SessionService`, envío |
| Implementar protección de reintento por `RequestId`/lote | Crítica | Alta | Alto | Estado durable | Evita duplicados | servicio de aplicación |
| Validar contenido mínimo de Excel | Media | Media | Bajo | Definir formatos aceptados | Menos adjuntos corruptos | validación |
| Añadir recuperación guiada de configuración/firma | Media | Baja | Bajo | UX | Mensajes accionables | inicio/UI |
| Mostrar versión/commit en “Acerca de” o pie | Alta | Baja | Bajo | Versionado central | Usuario identifica build real | UI |

## Fase 2: arquitectura y mantenibilidad

| Tarea | Prioridad | Complejidad | Riesgo | Dependencias | Resultado | Módulos |
|---|---|---|---|---|---|---|
| Extraer `SendBatchUseCase` y `ResendUseCase` | Alta | Alta | Medio | Tests de caracterización | Una sola lógica de envío/registro | `MainWindow`, `ResendWindow` |
| Introducir interfaces para Outlook, filesystem y reloj | Alta | Media | Medio | Casos de uso extraídos | Pruebas deterministas | `Services` |
| Crear `MainWindowViewModel` y comandos | Media | Alta | Medio | Casos de uso | Menos code-behind | UI |
| Separar DTO persistidos de modelos observables | Media | Media | Medio | Migración de formato | Contratos claros | `Models`, persistencia |
| Dividir `DirectoryMigrationService` por versión/regla | Media | Media | Medio | Tests existentes | Migraciones legibles | migración |
| Introducir composición/DI mínima | Media | Baja | Bajo | Interfaces | Dependencias explícitas | `App.xaml.cs` |

## Fase 3: pruebas y diagnóstico

| Tarea | Prioridad | Complejidad | Riesgo | Dependencias | Resultado | Módulos |
|---|---|---|---|---|---|---|
| Crear proyecto xUnit/NUnit y migrar checks puros | Alta | Media | Bajo | Interfaces | `dotnet test` y CI | Verification/Services |
| Pruebas de fallos post-`Send` e idempotencia | Crítica | Alta | Medio | Adaptador Outlook falso | Garantía contra duplicados | envío |
| Pruebas de actualización MSI | Alta | Alta | Medio | VM/runner Windows | Upgrade repetible | Installer |
| Rotación y redacción de logs | Alta | Media | Bajo | Política de retención | Diagnóstico seguro | logger |
| Exportar paquete de diagnóstico sin datos innecesarios | Media | Media | Bajo | Redacción | Soporte remoto útil | diagnóstico |
| Añadir UI smoke a pipeline Windows | Media | Media | Bajo | Runner interactivo | Regresiones visuales detectables | WPF |

## Fase 4: distribución

| Tarea | Prioridad | Complejidad | Riesgo | Dependencias | Resultado | Módulos |
|---|---|---|---|---|---|---|
| Pipeline Release desde commit/tag limpio | Crítica | Alta | Medio | Fases 0/3 | MSI reproducible | CI/WiX |
| Publicar solo MSI + hashes + notas | Alta | Baja | Bajo | Pipeline | Canal inequívoco | release |
| Verificar firma de código | Alta | Media | Medio | Certificado | Mejor confianza/SmartScreen | exe/MSI |
| Probar instalación limpia y upgrade | Crítica | Media | Medio | Matriz Windows/Outlook | Procedimiento validado | MSI |
| Definir actualización asistida o automática | Media | Alta | Alto | Canal y firma | Usuarios no quedan atrás | instalador/app |
| Documentar rollback y preservación de LocalAppData | Alta | Media | Medio | Política de datos | Recuperación predecible | operaciones |

## Orden inmediato recomendado

1. Congelar y etiquetar el artefacto aprobado.
2. Probar actualización del MSI en una VM clonada.
3. Confirmar que el acceso directo muestra 1.0.3 y conserva datos.
4. Cambiar el proceso de build para no leer implícitamente el perfil local.
5. Implementar el registro durable/idempotente antes de ampliar funciones.

## Criterios de salida

Una fase de distribución se considera lista cuando:

- el build parte de un commit limpio;
- la versión existe en una sola fuente;
- publicación y MSI se generan en carpeta nueva;
- un manifiesto lista hashes y archivos;
- pruebas automáticas pasan;
- upgrade desde la versión soportada pasa;
- el acceso directo apunta al ejecutable verificado;
- el instalador no contiene datos no aprobados;
- el procedimiento puede repetirse en otra máquina de build.

