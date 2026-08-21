import { after, before, beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertFails, assertSucceeds } from '@firebase/rules-unit-testing';
import { deleteDoc, doc, getDoc, setDoc, updateDoc } from 'firebase/firestore';

const projectId = 'demo-ecs-commissions';
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
    await setDoc(doc(db, 'appUsers/inactive'), profile('inactive', 'operator', { isActive: false }));
    await setDoc(doc(db, 'appUsers/no-commissions'), profile('no-commissions', 'operator', { canUseCommissions: false }));
    await setDoc(doc(db, 'appUsers/expirations-admin'), profile('expirations-admin', 'admin', {
      canUseCommissions: false,
      canUseExpirations: true
    }));
    const legacyProfile = profile('legacy');
    delete legacyProfile.canUseExpirations;
    await setDoc(doc(db, 'appUsers/legacy'), legacyProfile);
    await setDoc(doc(db, 'settings/commissions'), { defaultSubject: 'test' });
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

test('recursos actuales de Comisiones continúan requiriendo canUseCommissions', async () => {
  const db = environment.authenticatedContext('expirations-admin').firestore();
  const commissionPaths = [
    'settings/commissions',
    'brokers/broker-1',
    'sessions/session-1',
    'sessions/session-1/brokerItems/broker-1',
    'recentSends/send-1',
    'paymentGenerations/generation-1',
    'paymentGenerations/generation-1/files/file-1'
  ];
  for (const path of commissionPaths)
    await assertFails(getDoc(doc(db, path)));
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
