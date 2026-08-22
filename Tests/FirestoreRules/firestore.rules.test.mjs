import { after, before, beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertFails, assertSucceeds } from '@firebase/rules-unit-testing';
import { deleteDoc, doc, getDoc, setDoc, updateDoc } from 'firebase/firestore';

const projectId = 'demo-ecs-commissions';
const brokerOne = '11111111-1111-1111-1111-111111111111';
const brokerTwo = '22222222-2222-2222-2222-222222222222';
const missingBroker = '99999999-9999-9999-9999-999999999999';
const existingAssociation = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
let environment;

const profile = (uid, role = 'operator', overrides = {}) => ({
  uid,
  email: `${uid}@example.test`,
  displayName: uid,
  role,
  isActive: true,
  canUseCommissions: true,
  canUseExpirations: false,
  createdAtUtc: new Date('2026-08-10T00:00:00Z'),
  updatedAtUtc: new Date('2026-08-10T00:00:00Z'),
  ...overrides
});

const broker = (id, overrides = {}) => ({
  id,
  name: `Broker ${id}`,
  primaryEmailAddresses: [`${id}@example.test`],
  assistants: [],
  associatedWorksheetNames: [],
  deductions: [],
  isActive: true,
  requiresReview: false,
  reviewNote: null,
  ...overrides
});

const expirationsBrokerProfile = (brokerId, overrides = {}) => ({
  brokerId,
  isActive: true,
  assistants: [],
  createdAtUtc: new Date('2026-08-10T00:00:00Z'),
  updatedAtUtc: new Date('2026-08-10T00:00:00Z'),
  ...overrides
});

const association = (id, brokerId, overrides = {}) => ({
  id,
  brokerId,
  kind: 'Alias',
  value: 'AMA',
  normalizedValue: 'AMA',
  isActive: true,
  createdAtUtc: new Date('2026-08-10T00:00:00Z'),
  updatedAtUtc: new Date('2026-08-10T00:00:00Z'),
  ...overrides
});

const expirationsSettings = (overrides = {}) => ({
  defaultSubject: 'Vencimientos',
  defaultMessage: 'Mensaje de Vencimientos',
  commonCcAddresses: ['cc@example.test'],
  updatedAtUtc: new Date('2026-08-10T00:00:00Z'),
  ...overrides
});

before(async () => {
  environment = await initializeTestEnvironment({
    projectId,
    firestore: { rules: readFileSync('../../firestore.rules', 'utf8') }
  });
});

beforeEach(async () => {
  await environment.clearFirestore();
  await environment.withSecurityRulesDisabled(async context => {
    const db = context.firestore();
    await setDoc(doc(db, 'appUsers/operator'), profile('operator'));
    await setDoc(doc(db, 'appUsers/admin'), profile('admin', 'admin'));
    await setDoc(doc(db, 'appUsers/inactive'), profile('inactive', 'operator', {
      isActive: false,
      canUseExpirations: true
    }));
    await setDoc(doc(db, 'appUsers/no-commissions'), profile('no-commissions', 'operator', { canUseCommissions: false }));
    await setDoc(doc(db, 'appUsers/expirations'), profile('expirations', 'operator', {
      canUseCommissions: false,
      canUseExpirations: true
    }));
    await setDoc(doc(db, 'appUsers/both'), profile('both', 'operator', { canUseExpirations: true }));
    await setDoc(doc(db, 'appUsers/no-modules'), profile('no-modules', 'operator', {
      canUseCommissions: false,
      canUseExpirations: false
    }));
    await setDoc(doc(db, 'appUsers/expirations-admin'), profile('expirations-admin', 'admin', {
      canUseCommissions: false,
      canUseExpirations: true
    }));
    const legacyProfile = profile('legacy');
    delete legacyProfile.canUseExpirations;
    await setDoc(doc(db, 'appUsers/legacy'), legacyProfile);
    await setDoc(doc(db, 'settings/commissions'), { defaultSubject: 'test' });
    await setDoc(doc(db, `brokers/${brokerOne}`), broker(brokerOne));
    await setDoc(doc(db, `brokers/${brokerTwo}`), broker(brokerTwo));
    await setDoc(
      doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`),
      expirationsBrokerProfile(brokerOne));
    await setDoc(
      doc(db, `modules/vencimientos/associations/${existingAssociation}`),
      association(existingAssociation, brokerOne));
    await setDoc(doc(db, 'system/schema'), { version: 1 });
    await setDoc(doc(db, 'system/migrationState'), { status: 'completed' });
  });
});

after(async () => environment?.cleanup());

test('1. usuario no autenticado: DENY operational data', async () => {
  const db = environment.unauthenticatedContext().firestore();
  await assertFails(getDoc(doc(db, 'settings/commissions')));
});

test('2. autenticado sin appUsers: DENY', async () => {
  const db = environment.authenticatedContext('missing').firestore();
  await assertFails(getDoc(doc(db, 'settings/commissions')));
});

test('3. appUsers inactive: DENY', async () => {
  const db = environment.authenticatedContext('inactive').firestore();
  await assertFails(getDoc(doc(db, 'settings/commissions')));
});

test('4. active sin permiso Comisiones: DENY', async () => {
  const db = environment.authenticatedContext('no-commissions').firestore();
  await assertFails(getDoc(doc(db, 'settings/commissions')));
});

test('5. operator válido accede a operaciones de Comisiones', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  await assertSucceeds(getDoc(doc(db, 'settings/commissions')));
  await assertSucceeds(setDoc(doc(db, 'sessions/current'), { subject: 'ok' }));
});

test('recursos exclusivos actuales de Comisiones continúan requiriendo canUseCommissions', async () => {
  const db = environment.authenticatedContext('expirations-admin').firestore();
  const commissionPaths = [
    'settings/commissions',
    'sessions/session-1',
    'sessions/session-1/brokerItems/broker-1',
    'recentSends/send-1',
    'paymentGenerations/generation-1',
    'paymentGenerations/generation-1/files/file-1'
  ];
  for (const path of commissionPaths)
    await assertFails(getDoc(doc(db, path)));
});

test('brokers: usuario solo Comisiones conserva read/create/update/delete', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  const newBroker = '33333333-3333-3333-3333-333333333333';
  await assertSucceeds(getDoc(doc(db, `brokers/${brokerOne}`)));
  await assertSucceeds(setDoc(doc(db, `brokers/${newBroker}`), broker(newBroker)));
  await assertSucceeds(updateDoc(doc(db, `brokers/${brokerOne}`), { name: 'Broker actualizado' }));
  await assertSucceeds(deleteDoc(doc(db, `brokers/${brokerTwo}`)));
});

test('brokers: usuario solo Vencimientos puede leer pero no escribir', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  const newBroker = '33333333-3333-3333-3333-333333333333';
  await assertSucceeds(getDoc(doc(db, `brokers/${brokerOne}`)));
  await assertFails(setDoc(doc(db, `brokers/${newBroker}`), broker(newBroker)));
  await assertFails(updateDoc(doc(db, `brokers/${brokerOne}`), { name: 'No permitido' }));
  await assertFails(deleteDoc(doc(db, `brokers/${brokerTwo}`)));
});

test('brokers: usuario con ambos permisos conserva acceso de Comisiones', async () => {
  const db = environment.authenticatedContext('both').firestore();
  const newBroker = '33333333-3333-3333-3333-333333333333';
  await assertSucceeds(getDoc(doc(db, `brokers/${brokerOne}`)));
  await assertSucceeds(setDoc(doc(db, `brokers/${newBroker}`), broker(newBroker)));
  await assertSucceeds(updateDoc(doc(db, `brokers/${brokerOne}`), { name: 'Ambos permisos' }));
  await assertSucceeds(deleteDoc(doc(db, `brokers/${newBroker}`)));
});

test('brokers: usuario sin módulos e inactivo reciben DENY', async () => {
  const noModules = environment.authenticatedContext('no-modules').firestore();
  const inactive = environment.authenticatedContext('inactive').firestore();
  await assertFails(getDoc(doc(noModules, `brokers/${brokerOne}`)));
  await assertFails(getDoc(doc(inactive, `brokers/${brokerOne}`)));
});

test('brokerProfiles: Vencimientos permite read/create/update pero no delete', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertSucceeds(getDoc(doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`)));
  await assertSucceeds(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    expirationsBrokerProfile(brokerTwo)));
  await assertSucceeds(updateDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`),
    { isActive: false, updatedAtUtc: new Date('2026-08-10T01:00:00Z') }));
  await assertFails(deleteDoc(doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`)));
});

test('brokerProfiles: conserva shape antiguo y permite ambos modos de mes siguiente', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertSucceeds(getDoc(doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`)));
  await assertSucceeds(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    expirationsBrokerProfile(brokerTwo, { nextMonthGenerationMode: 'Standard' })));
  await assertSucceeds(updateDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    {
      nextMonthGenerationMode: 'SpecialDualSorted',
      updatedAtUtc: new Date('2026-08-10T01:00:00Z')
    }));
});

test('brokerProfiles: rechaza modo de mes siguiente desconocido', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertFails(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    expirationsBrokerProfile(brokerTwo, { nextMonthGenerationMode: 'FelixPorNombre' })));
});

test('brokerProfiles: solo Comisiones no puede leer ni escribir', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  await assertFails(getDoc(doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`)));
  await assertFails(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    expirationsBrokerProfile(brokerTwo)));
  await assertFails(updateDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerOne}`),
    { isActive: false }));
});

test('brokerProfiles: no permite crear perfil para broker inexistente', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertFails(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${missingBroker}`),
    expirationsBrokerProfile(missingBroker)));
});

test('associations: Vencimientos permite read/create/update pero no delete', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  const associationId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  await assertSucceeds(getDoc(doc(db, `modules/vencimientos/associations/${existingAssociation}`)));
  await assertSucceeds(setDoc(
    doc(db, `modules/vencimientos/associations/${associationId}`),
    association(associationId, brokerTwo)));
  await assertSucceeds(updateDoc(
    doc(db, `modules/vencimientos/associations/${existingAssociation}`),
    { isActive: false, updatedAtUtc: new Date('2026-08-10T01:00:00Z') }));
  await assertFails(deleteDoc(doc(db, `modules/vencimientos/associations/${existingAssociation}`)));
});

test('associations: normalizedValue repetido se permite para brokers distintos', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  const firstId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  const secondId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
  await assertSucceeds(setDoc(
    doc(db, `modules/vencimientos/associations/${firstId}`),
    association(firstId, brokerOne, { normalizedValue: 'AMA' })));
  await assertSucceeds(setDoc(
    doc(db, `modules/vencimientos/associations/${secondId}`),
    association(secondId, brokerTwo, { normalizedValue: 'AMA' })));
});

test('associations: no permite broker inexistente y solo Comisiones recibe DENY', async () => {
  const expirationsDb = environment.authenticatedContext('expirations').firestore();
  const commissionsDb = environment.authenticatedContext('operator').firestore();
  const associationId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  await assertFails(setDoc(
    doc(expirationsDb, `modules/vencimientos/associations/${associationId}`),
    association(associationId, missingBroker)));
  await assertFails(getDoc(
    doc(commissionsDb, `modules/vencimientos/associations/${existingAssociation}`)));
});

test('settings Vencimientos: previousMonth y nextMonth son permitidos e independientes', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertSucceeds(setDoc(
    doc(db, 'modules/vencimientos/settings/previousMonth'),
    expirationsSettings({ defaultSubject: 'Mes anterior' })));
  await assertSucceeds(setDoc(
    doc(db, 'modules/vencimientos/settings/nextMonth'),
    expirationsSettings({ defaultSubject: 'Próximo mes' })));
  await assertSucceeds(getDoc(doc(db, 'modules/vencimientos/settings/previousMonth')));
  await assertSucceeds(getDoc(doc(db, 'modules/vencimientos/settings/nextMonth')));
  await assertSucceeds(updateDoc(
    doc(db, 'modules/vencimientos/settings/nextMonth'),
    { defaultMessage: 'Actualizado', updatedAtUtc: new Date('2026-08-10T01:00:00Z') }));
  await assertFails(deleteDoc(doc(db, 'modules/vencimientos/settings/previousMonth')));
});

test('settings Vencimientos: processId arbitrario y solo Comisiones reciben DENY', async () => {
  const expirationsDb = environment.authenticatedContext('expirations').firestore();
  const commissionsDb = environment.authenticatedContext('operator').firestore();
  await assertFails(setDoc(
    doc(expirationsDb, 'modules/vencimientos/settings/arbitrary'),
    expirationsSettings()));
  await assertFails(getDoc(doc(expirationsDb, 'modules/vencimientos/settings/arbitrary')));
  await assertFails(setDoc(
    doc(commissionsDb, 'modules/vencimientos/settings/previousMonth'),
    expirationsSettings()));
  await assertFails(getDoc(doc(commissionsDb, 'modules/vencimientos/settings/previousMonth')));
});

test('documentos Vencimientos rechazan formas o IDs inválidos', async () => {
  const db = environment.authenticatedContext('expirations').firestore();
  await assertFails(setDoc(
    doc(db, `modules/vencimientos/brokerProfiles/${brokerTwo}`),
    { ...expirationsBrokerProfile(brokerOne), unexpected: true }));
  const associationId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  await assertFails(setDoc(
    doc(db, `modules/vencimientos/associations/${associationId}`),
    association(existingAssociation, brokerOne, { kind: 'Unknown' })));
  await assertFails(setDoc(
    doc(db, 'modules/vencimientos/settings/previousMonth'),
    expirationsSettings({ commonCcAddresses: 'cc@example.test' })));
});

test('Comisiones conserva acceso a settings, sessions, recentSends y paymentGenerations', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  const paths = [
    'settings/commissions',
    'sessions/current',
    `sessions/current/brokerItems/${brokerOne}`,
    'recentSends/send-1',
    'paymentGenerations/generation-1',
    'paymentGenerations/generation-1/files/file-1'
  ];
  for (const path of paths) {
    await assertSucceeds(getDoc(doc(db, path)));
    await assertSucceeds(setDoc(doc(db, path), { phaseThreeRegression: true }));
  }
});

test('perfil antiguo conserva Comisiones y no obtiene acceso accidental a Vencimientos', async () => {
  const db = environment.authenticatedContext('legacy').firestore();
  await assertSucceeds(getDoc(doc(db, 'appUsers/legacy')));
  await assertSucceeds(getDoc(doc(db, 'settings/commissions')));
  await assertFails(getDoc(doc(db, 'expirations/example')));
});

test('6. operator no puede promoverse admin', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  await assertFails(updateDoc(doc(db, 'appUsers/operator'), { role: 'admin' }));
});

test('7. operator no puede modificar otro appUser', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  await assertFails(updateDoc(doc(db, 'appUsers/inactive'), { isActive: true }));
});

test('8. admin puede administrar perfiles con forma válida', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertSucceeds(updateDoc(doc(db, 'appUsers/inactive'), {
    isActive: true,
    updatedAtUtc: new Date('2026-08-10T01:00:00Z')
  }));
});

test('admin activo sin Comisiones puede administrar appUsers', async () => {
  const db = environment.authenticatedContext('expirations-admin').firestore();
  await assertSucceeds(updateDoc(doc(db, 'appUsers/inactive'), {
    canUseExpirations: true,
    updatedAtUtc: new Date('2026-08-10T02:00:00Z')
  }));
});

test('9 y 10. WPF no modifica system/schema ni migrationState', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertFails(updateDoc(doc(db, 'system/schema'), { version: 2 }));
  await assertFails(updateDoc(doc(db, 'system/migrationState'), { status: 'changed' }));
});

test('11. contexto administrativo del emulador no queda sujeto a reglas cliente', async () => {
  await environment.withSecurityRulesDisabled(async context => {
    await assertSucceeds(setDoc(doc(context.firestore(), 'system/schema'), { version: 2 }));
  });
});

test('create appUser: usuario no autenticado recibe DENY', async () => {
  const db = environment.unauthenticatedContext().firestore();
  await assertFails(setDoc(doc(db, 'appUsers/new-user'), profile('new-user')));
});

test('create appUser: operator recibe DENY', async () => {
  const db = environment.authenticatedContext('operator').firestore();
  await assertFails(setDoc(doc(db, 'appUsers/new-user'), profile('new-user')));
});

test('create appUser: admin activo con permiso puede crear perfil válido', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertSucceeds(setDoc(doc(db, 'appUsers/new-user'), profile('new-user')));
});

test('create appUser: UID del documento y payload distintos recibe DENY', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertFails(setDoc(doc(db, 'appUsers/new-user'), profile('different-user')));
});

test('create appUser: role inválido recibe DENY', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertFails(setDoc(doc(db, 'appUsers/new-user'), profile('new-user', 'owner')));
});

test('create appUser: shape inválido recibe DENY', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertFails(setDoc(doc(db, 'appUsers/new-user'), {
    ...profile('new-user'),
    unexpectedField: true
  }));
  const missingRequiredField = profile('missing-name');
  delete missingRequiredField.displayName;
  await assertFails(setDoc(doc(db, 'appUsers/missing-name'), missingRequiredField));
  const missingExpirationsPermission = profile('missing-expirations');
  delete missingExpirationsPermission.canUseExpirations;
  await assertFails(setDoc(doc(db, 'appUsers/missing-expirations'), missingExpirationsPermission));
  await assertFails(setDoc(doc(db, 'appUsers/bad-timestamp'), profile('bad-timestamp', 'operator', {
    createdAtUtc: '2026-08-10T00:00:00Z'
  })));
});

test('delete appUser: continúa DENY incluso para admin', async () => {
  const db = environment.authenticatedContext('admin').firestore();
  await assertFails(deleteDoc(doc(db, 'appUsers/operator')));
});
