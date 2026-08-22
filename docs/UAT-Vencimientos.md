# UAT — Vencimientos

Ejecutar manualmente en un ambiente controlado, con archivos y destinatarios sintéticos. No anotar en este documento pólizas, correos ni otros datos reales. Conservar evidencia fuera del repositorio cuando corresponda.

## UAT-01 — Login solo Comisiones

Acción: Iniciar sesión con un usuario activo que tenga únicamente permiso de Comisiones.

Resultado esperado: Entra directamente a Comisiones y no puede acceder a Vencimientos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-02 — Login solo Vencimientos

Acción: Iniciar sesión con un usuario activo que tenga únicamente permiso de Vencimientos.

Resultado esperado: Entra directamente a Vencimientos sin cargar el runtime operacional de Comisiones.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-03 — Usuario con ambos módulos

Acción: Iniciar sesión con un usuario activo que tenga permisos de Comisiones y Vencimientos.

Resultado esperado: Aparece el selector de módulos y cada opción abre solamente el módulo elegido.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-04 — Usuario inactivo

Acción: Intentar iniciar sesión con un usuario inactivo, aunque tenga permisos de módulos.

Resultado esperado: El acceso queda bloqueado y no abre ningún módulo.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-05 — PreviousMonth carga/análisis

Acción: Seleccionar PreviousMonth, cargar un Excel sintético válido y analizarlo.

Resultado esperado: Muestra las filas, destinos y pendientes correctos; no permite generar mientras existan pendientes bloqueantes.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-06 — PreviousMonth generación

Acción: Resolver los pendientes y generar PreviousMonth en una carpeta vacía.

Resultado esperado: Crea un archivo por BrokerId/destino, sin hoja `Total de primas`; no modifica el Excel fuente y no usa el formato especial.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-07 — NextMonth carga/análisis

Acción: Seleccionar NextMonth, indicar período, cargar un Excel sintético y analizarlo. Si la detección de Prima/Moneda falla, seleccionarlas manualmente.

Resultado esperado: El período es obligatorio; el análisis y la selección manual habilitan la generación únicamente cuando los datos son válidos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-08 — NextMonth Total de primas

Acción: Generar un archivo estándar NextMonth y abrirlo en Excel.

Resultado esperado: Contiene `Total de primas`; las fórmulas están vivas y los totales CRC/USD coinciden con los registros del corredor.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-09 — Félix alfabético

Acción: Generar NextMonth para el único corredor configurado como SpecialDualSorted y abrir `ORDENADO ALFABETICAMENTE`.

Resultado esperado: Contiene todos y solo los registros esperados, ordenados alfabéticamente, con totales correctos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-10 — Félix por vencimiento

Acción: Abrir el archivo `ORDENADO POR VENCIMIENTO` del mismo lote.

Resultado esperado: Contiene los mismos registros y totales que el alfabético, ordenados por vencimiento.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-11 — Félix ambos archivos mismo correo

Acción: Abrir el preview de envío del corredor SpecialDualSorted.

Resultado esperado: Existe un solo correo para el BrokerId, con exactamente los dos archivos especiales adjuntos; el envío produce un solo item de historial.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-12 — PC Guanacaste → Alberto

Acción: Analizar una fila sintética con el alias configurado `PC Guanacaste`.

Resultado esperado: La fila se enruta al destino Alberto configurado, sin crear mappings nuevos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-13 — Hernán Varela → Javier

Acción: Analizar una fila sintética con el alias configurado `Hernán Varela`.

Resultado esperado: La fila se enruta al destino Javier configurado, sin crear mappings nuevos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-14 — Andrés consolidado

Acción: Analizar filas sintéticas que usen varios aliases/códigos configurados para Andrés, incluso varios en una misma fila.

Resultado esperado: Se genera un solo archivo para su BrokerId y una misma fila no se duplica dentro del destino.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-15 — Ana Luisa / Jerrika

Acción: Analizar datos sintéticos que correspondan a Ana Luisa y Jerrika.

Resultado esperado: Permanecen como destinos separados y cada archivo contiene únicamente sus filas correspondientes.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-16 — Shared policies

Acción: Analizar una fila sintética compartida por dos destinos y generar los archivos.

Resultado esperado: La fila aparece una vez en cada destino aplicable y no se duplica por múltiples aliases del mismo BrokerId.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-17 — Asistentes de Vencimientos

Acción: Configurar asistentes activos e inactivos de Vencimientos y revisar el preview.

Resultado esperado: TO contiene los correos principales y asistentes activos de Vencimientos, sin duplicados; excluye asistentes inactivos y asistentes de Comisiones.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-18 — Settings correo PreviousMonth

Acción: Configurar asunto, mensaje y CC de PreviousMonth; generar y abrir su preview.

Resultado esperado: El preview usa exactamente los settings de PreviousMonth y exige al menos un destinatario TO válido.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-19 — Settings correo NextMonth

Acción: Configurar valores distintos para NextMonth y abrir su preview.

Resultado esperado: Usa los settings de NextMonth sin modificar ni reutilizar accidentalmente los de PreviousMonth o Comisiones.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-20 — Outlook con una cuenta

Acción: Abrir el preview en un equipo de UAT donde Outlook Classic exponga una sola cuenta controlada.

Resultado esperado: La cuenta válida queda seleccionada y el botón de continuar requiere confirmación explícita antes de cualquier envío.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-21 — Outlook con múltiples cuentas

Acción: Abrir el preview donde Outlook Classic exponga más de una cuenta.

Resultado esperado: Debe seleccionarse una cuenta válida; no se puede continuar sin selección.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-22 — Preview destinatarios

Acción: Revisar todos los correos del lote antes de continuar.

Resultado esperado: Cada BrokerId tiene un correo con TO, CC, asunto, mensaje y nombres de adjuntos correctos y deduplicados; no hay datos de otro corredor.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-23 — Envío controlado

Acción: Primero cancelar la confirmación; luego repetir con destinatarios controlados y confirmar una sola vez.

Resultado esperado: Cancelar produce cero envíos y cero operación nueva; confirmar inicia un único lote, sin autosend ni retry automático.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-24 — Resultado exitoso

Acción: Completar un envío controlado que Outlook confirme como exitoso.

Resultado esperado: El item queda Succeeded y la operación completada muestra el contador correcto.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-25 — Resultado fallido

Acción: En un entorno controlado, provocar o simular un fallo explícito de un correo del lote.

Resultado esperado: Solo ese item queda Failed, se muestra el error y no ocurre retry automático. Un resultado no confirmado se muestra como pendiente/desconocido, nunca como Failed.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-26 — Historial

Acción: Abrir el historial y seleccionar las operaciones anteriores.

Resultado esperado: Muestra operaciones e items separados por OperationId, sus estados y el snapshot histórico de destinatarios y adjuntos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-27 — Retry de Failed

Acción: Con el lote original aún disponible, reintentar una operación completada con items Succeeded y Failed.

Resultado esperado: Solo los Failed pasan al preview; se crea una operación nueva enlazada, el intento anterior no cambia y los dos adjuntos especiales permanecen juntos cuando corresponda.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-28 — Reiniciar aplicación

Acción: Cerrar y abrir la aplicación; consultar una operación cuyo lote exacto ya no está disponible en la sesión.

Resultado esperado: El historial sigue visible y el retry queda deshabilitado con un mensaje seguro; no intenta reconstruir archivos ni reenviar.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-29 — Segunda PC

Acción: Desde dos PCs de UAT, editar el mismo setting o perfil y realizar operaciones distintas.

Resultado esperado: Los cambios obsoletos detectan conflicto mediante expectedUpdateTime y cada envío conserva su OperationId. Registrar como riesgo que dos escrituras concurrentes de perfiles no garantizan por sí solas la unicidad global de SpecialDualSorted.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-30 — Configuración Admin

Acción: Entrar como Admin activo con acceso a Vencimientos y abrir administración de permisos.

Resultado esperado: Conserva la administración existente y puede configurar permisos de módulos sin alterar identidad u otros permisos accidentalmente.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-31 — Operador sin Admin

Acción: Entrar como operador activo con acceso a Vencimientos.

Resultado esperado: Puede operar Vencimientos, pero no ve ni puede abrir administración de usuarios.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

## UAT-32 — Regresión manual Comisiones

Acción: Entrar a Comisiones y recorrer manualmente su flujo operacional principal con datos controlados.

Resultado esperado: Asistentes, settings, sesiones, recentSends, cálculo, pagos, generación e historial de Comisiones funcionan sin cambios atribuibles a Vencimientos.

Resultado:
[ ] PASS
[ ] FAIL

Observaciones:
____________________

# Checklist previo a producción

[ ] UAT completo PASS

[ ] Firestore Rules pendientes desplegadas

[ ] Usuarios reales con permisos correctos

[ ] Exactamente un corredor SpecialDualSorted configurado

[ ] Settings PreviousMonth configurados

[ ] Settings NextMonth configurados

[ ] Outlook Classic instalado

[ ] Cuenta(s) Outlook correctas visibles

[ ] Envío real controlado exitoso

[ ] PreviousMonth validado con Excel real

[ ] NextMonth validado con Excel real

[ ] Félix validado visualmente

[ ] Totales CRC/USD validados

[ ] Historial validado

[ ] Comisiones validado manualmente sin regresiones

[ ] Backup/commit estable creado

[ ] Installer generar únicamente después de UAT PASS
