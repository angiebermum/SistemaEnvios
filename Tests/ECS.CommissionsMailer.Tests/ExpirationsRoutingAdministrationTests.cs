using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsRoutingAdministrationTests
{
    private static readonly Guid ShortBroker = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LongBroker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AssociationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ExclusionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullExactNameWinsOverContainedShortNameButConflictingExactAssociationIsAmbiguous()
    {
        var catalog = new[]
        {
            Broker(ShortBroker, "Fernando Cabada"),
            Broker(LongBroker, "Fernando Cabada Corvisier")
        };
        var component = Component("Fernando Cabada Corvisier");

        var exactName = new ExpirationsBrokerResolver(catalog, []).Resolve(component);
        var conflict = new ExpirationsBrokerResolver(
            catalog,
            [Association(ShortBroker, "Fernando Cabada Corvisier")]).Resolve(component);
        var containingBoth = new ExpirationsBrokerResolver(catalog, []).Resolve(
            Component("Agencia Fernando Cabada Corvisier Norte"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, exactName.Status);
        Assert.Equal(LongBroker, exactName.ResolvedBrokerId);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, conflict.Status);
        Assert.Equal([ShortBroker, LongBroker], conflict.CandidateBrokerIds.Order());
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, containingBoth.Status);
    }

    [Fact]
    public void ExclusionMatchIsExactNonBlockingAndMixedRowKeepsOnlyValidDestinations()
    {
        var catalog = new[] { Broker(ShortBroker, "Ana") };
        var exclusion = Exclusion("Cristian Porras", active: true);
        var read = Read(
            new ExpirationsSourceRow { RowNumber = 2, RawBrokerValue = "Cristian Porras" },
            new ExpirationsSourceRow { RowNumber = 3, RawBrokerValue = "Ana; Cristian Porras" },
            new ExpirationsSourceRow { RowNumber = 4, RawBrokerValue = "Cristian Porras Quesada" });

        var analysis = new ExpirationsWorkbookAnalysisService().Analyze(
            read,
            catalog,
            [],
            [],
            [exclusion]);

        var fullyExcluded = analysis.RowResolutions.Single(row => row.RowNumber == 2);
        var mixed = analysis.RowResolutions.Single(row => row.RowNumber == 3);
        var longerValue = analysis.RowResolutions.Single(row => row.RowNumber == 4);
        Assert.False(fullyExcluded.HasBlockingIssues);
        Assert.Empty(fullyExcluded.DistinctDestinationBrokerIds);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded, Assert.Single(fullyExcluded.Components).Status);
        Assert.False(mixed.HasBlockingIssues);
        Assert.Equal(ShortBroker, Assert.Single(mixed.DistinctDestinationBrokerIds));
        Assert.Contains(mixed.Components, component => component.Status == ExpirationsBrokerResolutionStatus.Excluded);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, Assert.Single(longerValue.Components).Status);
        Assert.True(longerValue.HasBlockingIssues);
        Assert.Equal(1, analysis.ExcludedRows);
        Assert.Equal(2, analysis.ExcludedComponents);
    }

    [Fact]
    public async Task AssociationsCanBeListedDeactivatedReactivatedAndReassignedWithoutLosingIdentity()
    {
        var original = Association(ShortBroker, "F. Cabada");
        var associations = new FakeAssociationRepository([Stored(original, "a-1")]);
        var exclusions = new FakeExclusionRepository([]);
        var brokers = new FakeConfigurationService(
            BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
            BrokerConfiguration(LongBroker, "Fernando Cabada Corvisier", "long@example.test"));
        var service = Service(associations, exclusions, brokers);

        var listed = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ShortBroker, listed.Association.BrokerId);
        Assert.Equal("short@example.test", listed.BrokerPrimaryEmail);

        var deactivated = await service.SetAssociationActiveAsync(
            AssociationId, false, listed.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(deactivated.WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved,
            Resolver(associations).Resolve(Component("F. Cabada")).Status);

        var afterDeactivate = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        var reactivated = await service.SetAssociationActiveAsync(
            AssociationId, true, afterDeactivate.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(reactivated.WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved,
            Resolver(associations).Resolve(Component("F. Cabada")).Status);

        var afterReactivate = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        var reassigned = await service.ReassignAsync(
            AssociationId, LongBroker, afterReactivate.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(reassigned.WasPersisted);
        var stored = Assert.Single(associations.Documents).Value;
        Assert.Equal(AssociationId, stored.Id);
        Assert.Equal(CreatedAt, stored.CreatedAtUtc);
        Assert.Equal(LongBroker, stored.BrokerId);
    }

    [Fact]
    public async Task AssociationExclusionConflictIsBlockedAndDeactivatingExclusionReturnsValueToPending()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(ShortBroker, "Essential"), "a-1")
        ]);
        var exclusions = new FakeExclusionRepository([
            Stored(Exclusion("Essential", active: false), "e-1")
        ]);
        var brokers = new FakeConfigurationService(
            BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"));
        var service = Service(associations, exclusions, brokers);

        var blocked = await service.SetExclusionActiveAsync(
            ExclusionId, true, "e-1", TestContext.Current.CancellationToken);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, blocked.Outcome);

        Assert.True((await service.SetAssociationActiveAsync(
            AssociationId, false, "a-1", TestContext.Current.CancellationToken)).WasPersisted);
        Assert.True((await service.SetExclusionActiveAsync(
            ExclusionId, true, "e-1", TestContext.Current.CancellationToken)).WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded,
            Resolver(associations, exclusions).Resolve(Component("Essential")).Status);
        var reactivationBlocked = await service.SetAssociationActiveAsync(
            AssociationId,
            true,
            Assert.Single(associations.Documents).UpdateTime,
            TestContext.Current.CancellationToken);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, reactivationBlocked.Outcome);

        var exclusionVersion = Assert.Single(exclusions.Documents).UpdateTime;
        Assert.True((await service.SetExclusionActiveAsync(
            ExclusionId, false, exclusionVersion, TestContext.Current.CancellationToken)).WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved,
            Resolver(associations, exclusions).Resolve(Component("Essential")).Status);
    }

    [Fact]
    public async Task AssociationCanBeCreatedAndEditedWithoutDestinationSelection()
    {
        var associations = new FakeAssociationRepository([]);
        var service = Service(
            associations,
            new FakeExclusionRepository([]),
            new FakeConfigurationService(BrokerConfiguration(
                ShortBroker,
                "Andrés Steimberg - Agent for EssentialGroupLA",
                "andres@example.test")));

        var created = await service.CreateAssociationAsync(
            ShortBroker,
            ExpirationsAssociationKind.Alias,
            "NUEVOS XX AS35 - 200",
            TestContext.Current.CancellationToken);
        var stored = Assert.Single(associations.Documents);
        var edited = await service.EditAssociationAsync(
            stored.Value.Id,
            ExpirationsAssociationKind.Code,
            "NUEVOS XX AS35 - 201",
            stored.UpdateTime,
            TestContext.Current.CancellationToken);

        Assert.True(created.WasPersisted);
        Assert.True(edited.WasPersisted);
        var updated = Assert.Single(associations.Documents).Value;
        Assert.Equal(ExpirationsAssociationKind.Code, updated.Kind);
        Assert.Equal("NUEVOS XX AS35 - 201", updated.Value);
        Assert.Equal(ExpirationsDestinationGroup.Principal, updated.DestinationGroup);
    }

    [Fact]
    public void AdministrationSearchFindsAssociationByValueTypeBrokerEmailAndBrokerFilter()
    {
        var state = new ExpirationsRoutingAdministrationState();
        var first = new ExpirationsAssociationAdministrationItem
        {
            Association = Association(ShortBroker, "Fernando Cabada"),
            UpdateTime = "a-1",
            BrokerName = "Fernando Cabada Corvisier",
            BrokerPrimaryEmail = "fernando.corvisier@example.test"
        };
        var secondAssociation = Association(LongBroker, "FC-02");
        secondAssociation.Kind = ExpirationsAssociationKind.Code;
        var second = new ExpirationsAssociationAdministrationItem
        {
            Association = secondAssociation,
            UpdateTime = "a-2",
            BrokerName = "Otra persona",
            BrokerPrimaryEmail = "otra@example.test"
        };
        var configurations = new[]
        {
            BrokerConfiguration(ShortBroker, first.BrokerName, first.BrokerPrimaryEmail),
            BrokerConfiguration(LongBroker, second.BrokerName, second.BrokerPrimaryEmail)
        };
        state.SetData([first, second], configurations, null);

        foreach (var query in new[] { "Fernando Cabada", "Alias", "Corvisier", "fernando.corvisier@" })
        {
            state.AssociationSearchText = query;
            Assert.Equal(AssociationId, Assert.Single(state.VisibleAssociations).Association.Id);
        }
        state.AssociationSearchText = string.Empty;
        state.SelectedBrokerFilter = state.BrokerFilters.Single(filter => filter.BrokerId == LongBroker);
        Assert.Equal(LongBroker, Assert.Single(state.VisibleAssociations).Association.BrokerId);
    }

    [Fact]
    public void SelectedBrokerAlwaysShowsReadOnlyMasterIdentityAndAllAdditionalAssociationStates()
    {
        var active = new ExpirationsAssociationAdministrationItem
        {
            Association = Association(ShortBroker, "FC-01"),
            BrokerName = "Fernando Cabada",
            BrokerPrimaryEmail = "short@example.test"
        };
        var inactiveAssociation = Association(ShortBroker, "Fernando alternativo");
        inactiveAssociation.Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        inactiveAssociation.IsActive = false;
        var inactive = new ExpirationsAssociationAdministrationItem
        {
            Association = inactiveAssociation,
            BrokerName = "Fernando Cabada",
            BrokerPrimaryEmail = "short@example.test"
        };
        var configurations = new[]
        {
            BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
            BrokerConfiguration(LongBroker, "Sin asociaciones", "none@example.test")
        };
        var state = new ExpirationsRoutingAdministrationState();

        state.SetData([active, inactive], configurations, ShortBroker);

        Assert.Equal("Fernando Cabada", state.SelectedBrokerName);
        Assert.Equal("short@example.test", state.SelectedBrokerEmail);
        Assert.Equal("Activo", state.SelectedBrokerStatus);
        Assert.Equal(Visibility.Visible, state.BrokerIdentityVisibility);
        Assert.Equal(2, state.VisibleAssociations.Count);
        Assert.Equal(3, state.VisibleKnownIdentifiers.Count);
        Assert.Contains(state.VisibleKnownIdentifiers, item => item.IsMaster &&
            item.OriginText == "Maestro" && item.Value == "Fernando Cabada");
        Assert.Contains(state.VisibleAssociations, item => item.StatusText == "Activa");
        Assert.Contains(state.VisibleAssociations, item => item.StatusText == "Inactiva");
        state.SelectedBrokerFilter = state.BrokerFilters.Single(item => item.BrokerId == LongBroker);
        Assert.Empty(state.VisibleAssociations);
        Assert.Equal("Sin asociaciones", state.SelectedBrokerName);
        Assert.Equal(Visibility.Visible, state.NoAssociationsVisibility);
        Assert.Null(typeof(ExpirationsRoutingAdministrationState).GetProperty(nameof(state.SelectedBrokerName))!.SetMethod);
    }

    [Fact]
    public async Task AssociationCanBeAddedEditedReassignedAndDeletedPreservingIdentityFields()
    {
        var associations = new FakeAssociationRepository([]);
        var service = Service(
            associations,
            new FakeExclusionRepository([]),
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
                BrokerConfiguration(LongBroker, "Fernando Cabada Corvisier", "long@example.test")));

        var added = await service.CreateAssociationAsync(
            ShortBroker,
            ExpirationsAssociationKind.Code,
            " FCC - 52 ",
            TestContext.Current.CancellationToken);
        Assert.True(added.WasPersisted);
        var created = Assert.Single(associations.Documents);
        Assert.Equal("FCC - 52", created.Value.Value);
        Assert.Equal(ExpirationsAssociationKind.Code, created.Value.Kind);
        var createdAt = created.Value.CreatedAtUtc;

        var edited = await service.EditAssociationAsync(
            created.Value.Id,
            ExpirationsAssociationKind.Alias,
            "Fernando Cabada/FCC - 52",
            created.UpdateTime,
            TestContext.Current.CancellationToken);
        Assert.True(edited.WasPersisted);
        var editedDocument = Assert.Single(associations.Documents);
        Assert.Equal(createdAt, editedDocument.Value.CreatedAtUtc);
        Assert.Equal(ExpirationsAssociationKind.Alias, editedDocument.Value.Kind);

        var reassigned = await service.ReassignAsync(
            editedDocument.Value.Id,
            LongBroker,
            editedDocument.UpdateTime,
            TestContext.Current.CancellationToken);
        Assert.True(reassigned.WasPersisted);
        var reassignedDocument = Assert.Single(associations.Documents);
        Assert.Equal(LongBroker, reassignedDocument.Value.BrokerId);
        Assert.Equal(createdAt, reassignedDocument.Value.CreatedAtUtc);

        var deleted = await service.DeleteAssociationAsync(
            reassignedDocument.Value.Id,
            reassignedDocument.UpdateTime,
            TestContext.Current.CancellationToken);
        Assert.True(deleted.WasPersisted);
        Assert.Empty(associations.Documents);
    }

    [Fact]
    public async Task AddEditReassignAndReactivateRejectNormalizedAssociationOrExclusionConflicts()
    {
        var existing = Association(ShortBroker, "FCC - 52");
        var editable = Association(LongBroker, "FCC / 52");
        editable.Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        editable.IsActive = false;
        var associations = new FakeAssociationRepository([
            Stored(existing, "a-1"),
            Stored(editable, "a-2")
        ]);
        var exclusions = new FakeExclusionRepository([
            Stored(Exclusion("EXCLUIDO", active: true), "e-1")
        ]);
        var service = Service(
            associations,
            exclusions,
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
                BrokerConfiguration(LongBroker, "Fernando Cabada Corvisier", "long@example.test")));

        var duplicate = await service.CreateAssociationAsync(
            LongBroker, ExpirationsAssociationKind.Code, "fcc 52", TestContext.Current.CancellationToken);
        var excluded = await service.EditAssociationAsync(
            editable.Id, ExpirationsAssociationKind.Alias, "excluido", "a-2", TestContext.Current.CancellationToken);
        var reactivateConflict = await service.EditAssociationAsync(
            editable.Id, ExpirationsAssociationKind.Alias, "fcc-52", "a-2", TestContext.Current.CancellationToken);
        var reassignConflict = await service.ReassignAsync(
            editable.Id, ShortBroker, "a-2", TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, duplicate.Outcome);
        Assert.Contains("Fernando Cabada", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, excluded.Outcome);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, reactivateConflict.Outcome);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, reassignConflict.Outcome);
        Assert.Equal(2, associations.Documents.Count);
    }

    [Fact]
    public async Task KnownIdentifiersMergeMasterAssociationsAndObservedWithDeterministicPrecedence()
    {
        var association = Association(ShortBroker, "FC-01");
        association.Origin = ExpirationsAssociationOrigin.Imported;
        var associations = new FakeAssociationRepository([Stored(association, "a-1")]);
        var observations = new FakeObservedRepository([
            Stored(Observed(ShortBroker, "FC-01"), "o-1"),
            Stored(Observed(ShortBroker, "Fernando Cabada/FCC - 52"), "o-2"),
            Stored(Observed(ShortBroker, "Ignorado", ignored: true), "o-3")
        ]);
        var service = Service(
            associations,
            new FakeExclusionRepository([]),
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test")),
            observations);

        var items = await service.ListKnownIdentifiersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.IsMaster && item.OriginText == "Maestro");
        Assert.Contains(items, item => item.IsAssociation && item.OriginText == "Importado" && item.Value == "FC-01");
        Assert.Contains(items, item => item.IsObserved &&
            item.OriginText == "Detectado automáticamente" &&
            item.Value == "Fernando Cabada/FCC - 52");
        Assert.DoesNotContain(items, item => item.Value == "Ignorado");
        Assert.Single(items, item => item.NormalizedValue == association.NormalizedValue);

        var state = new ExpirationsRoutingAdministrationState();
        var broker = BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test");
        state.SetKnownData(items, [broker], ShortBroker);
        foreach (var query in new[]
                 {
                     "Fernando Cabada/FCC - 52", "Detectado automáticamente", "short@example.test", "Alias"
                 })
        {
            state.AssociationSearchText = query;
            Assert.Contains(state.VisibleKnownIdentifiers, item => item.Value == "Fernando Cabada/FCC - 52");
        }
    }

    [Fact]
    public async Task KnownIdentifiersShowDetectedCodeOnceWithExactRawExcelTrace()
    {
        var alias = Observed(ShortBroker, "Adriana Arroyo/AAV - 90");
        var code = Observed(ShortBroker, "AAV - 90");
        code.Kind = ExpirationsAssociationKind.Code;
        var observations = new FakeObservedRepository([
            Stored(alias, "o-alias"),
            Stored(code, "o-code")
        ]);
        var service = Service(
            new FakeAssociationRepository([]),
            new FakeExclusionRepository([]),
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Adriana Arroyo", "adriana@example.test")),
            observations);

        var items = await service.ListKnownIdentifiersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        var detected = Assert.Single(items, item => item.IsObserved);
        Assert.Equal(ExpirationsAssociationKind.Code, detected.Kind);
        Assert.Equal("AAV - 90", detected.Value);
        Assert.Equal("Detectado", detected.StatusText);
        Assert.Equal("Visto en el Excel como: Adriana Arroyo/AAV - 90", detected.DetailText);
        Assert.Equal(2, observations.Documents.Count);
    }

    [Fact]
    public async Task KnownIdentifiersNeverFuzzyGroupAndExposeOnlyContextualActions()
    {
        var alias = Observed(ShortBroker, "Adriana Arroyo/AAV90");
        var code = Observed(ShortBroker, "AAV - 90");
        code.Kind = ExpirationsAssociationKind.Code;
        var association = Association(ShortBroker, "Adriana");
        var service = Service(
            new FakeAssociationRepository([Stored(association, "a-1")]),
            new FakeExclusionRepository([]),
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Adriana Arroyo", "adriana@example.test")),
            new FakeObservedRepository([Stored(alias, "o-alias"), Stored(code, "o-code")]));
        var items = await service.ListKnownIdentifiersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, items.Count);
        Assert.Equal(2, items.Count(item => item.IsObserved));
        var state = new ExpirationsRoutingAdministrationState();
        state.SetKnownData(items, [BrokerConfiguration(
            ShortBroker, "Adriana Arroyo", "adriana@example.test")], ShortBroker);

        state.SelectedKnownIdentifier = items.Single(item => item.IsMaster);
        Assert.Equal(Visibility.Collapsed, state.AssociationActionsVisibility);
        Assert.Equal(Visibility.Collapsed, state.ObservedActionsVisibility);
        Assert.Equal(Visibility.Collapsed, state.ReassignActionsVisibility);

        state.SelectedKnownIdentifier = items.Single(item => item.IsAssociation);
        Assert.Equal(Visibility.Visible, state.AssociationActionsVisibility);
        Assert.Equal(Visibility.Collapsed, state.ObservedActionsVisibility);

        state.SelectedKnownIdentifier = items.First(item => item.IsObserved);
        Assert.Equal(Visibility.Collapsed, state.AssociationActionsVisibility);
        Assert.Equal(Visibility.Visible, state.ObservedActionsVisibility);
        Assert.Equal("Reasignar y usar como asociación", state.ReassignButtonText);
    }

    [Fact]
    public async Task ObservedIdentifiersCanBeConfirmedReassignedAndIgnoredWithoutBecomingResolverInput()
    {
        var associations = new FakeAssociationRepository([]);
        var observations = new FakeObservedRepository([
            Stored(Observed(ShortBroker, "FC-01"), "o-1"),
            Stored(Observed(ShortBroker, "FCC - 52"), "o-2"),
            Stored(Observed(ShortBroker, "Descartar"), "o-3")
        ]);
        var service = Service(
            associations,
            new FakeExclusionRepository([]),
            new FakeConfigurationService(
                BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
                BrokerConfiguration(LongBroker, "Fernando Cabada Corvisier", "long@example.test")),
            observations);

        var confirmed = await service.ConfirmObservedIdentifierAsync(
            observations.Documents[0].Value.Id,
            "o-1",
            TestContext.Current.CancellationToken);
        var reassigned = await service.ReassignAndConfirmObservedIdentifierAsync(
            observations.Documents.Single(item => item.Value.Value == "FCC - 52").Value.Id,
            LongBroker,
            "o-2",
            TestContext.Current.CancellationToken);
        var ignoredDocument = observations.Documents.Single(item => item.Value.Value == "Descartar");
        var ignored = await service.IgnoreObservedIdentifierAsync(
            ignoredDocument.Value.Id,
            ignoredDocument.UpdateTime,
            TestContext.Current.CancellationToken);

        Assert.True(confirmed.WasPersisted);
        Assert.True(reassigned.WasPersisted);
        Assert.True(ignored.WasPersisted);
        Assert.Contains(associations.Documents, item => item.Value.BrokerId == ShortBroker &&
            item.Value.Value == "FC-01" &&
            item.Value.Origin == ExpirationsAssociationOrigin.ManuallyConfirmed);
        Assert.Contains(associations.Documents, item => item.Value.BrokerId == LongBroker &&
            item.Value.Value == "FCC - 52" &&
            item.Value.Origin == ExpirationsAssociationOrigin.ManuallyConfirmed);
        Assert.True(observations.Documents.Single(item => item.Value.Value == "FCC - 52").Value.IsIgnored);
        Assert.True(observations.Documents.Single(item => item.Value.Value == "Descartar").Value.IsIgnored);
        Assert.Equal(
            ExpirationsBrokerResolutionStatus.Unresolved,
            new ExpirationsBrokerResolver(
                [Broker(ShortBroker, "Fernando Cabada"), Broker(LongBroker, "Fernando Cabada Corvisier")],
                []).Resolve(Component("Descartar")).Status);
    }

    [Fact]
    public void ExclusionsStateListsAllValuesAndSearchesIndependentlyOfAnyBroker()
    {
        var state = new ExpirationsExclusionsState();
        state.SetItems([
            new ExpirationsExclusionAdministrationItem { Exclusion = Exclusion("Essential ECS", true) },
            new ExpirationsExclusionAdministrationItem { Exclusion = Exclusion("Histórico", false) }
        ]);

        Assert.Equal(2, state.VisibleItems.Count);
        state.SearchText = "essential";
        Assert.Equal("Essential ECS", Assert.Single(state.VisibleItems).Exclusion.Value);
    }

    [Fact]
    public void ExpirationsUiKeepsExclusionsOutOfMainAndAssociationWindows()
    {
        var main = File.ReadAllText(SourceFile("Views", "ExpirationsWindow.xaml"));
        var management = File.ReadAllText(SourceFile("Views", "ExpirationsBrokerManagementWindow.xaml"));
        var associations = File.ReadAllText(SourceFile("Views", "ExpirationsRoutingAdministrationWindow.xaml"));

        Assert.DoesNotContain("ViewExcluded_Click", main, StringComparison.Ordinal);
        Assert.Contains("ViewExclusions_Click", management, StringComparison.Ordinal);
        Assert.Contains("Administrar asociaciones", management, StringComparison.Ordinal);
        Assert.Contains("Identificadores conocidos — Vencimientos", associations, StringComparison.Ordinal);
        Assert.DoesNotContain("Administración de routing", associations, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exclus", associations, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TabControl", associations, StringComparison.Ordinal);
    }

    private static ExpirationsRoutingAdministrationService Service(
        FakeAssociationRepository associations,
        FakeExclusionRepository exclusions,
        FakeConfigurationService brokers,
        FakeObservedRepository? observed = null) => new(
        associations,
        exclusions,
        brokers,
        timeProvider: new FixedTimeProvider(UpdatedAt.AddHours(1)),
        observedIdentifiers: observed);

    private static string SourceFile(
        string directory,
        string fileName,
        [CallerFilePath] string sourceFilePath = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        return Path.Combine(root, directory, fileName);
    }

    private static ExpirationsBrokerResolver Resolver(
        FakeAssociationRepository associations,
        FakeExclusionRepository? exclusions = null) => new(
        [Broker(ShortBroker, "Fernando Cabada"), Broker(LongBroker, "Fernando Cabada Corvisier")],
        associations.Documents.Select(document => document.Value),
        exclusions: exclusions?.Documents.Select(document => document.Value));

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:D}@example.test"],
        IsActive = true
    };

    private static ExpirationsBrokerConfigurationItem BrokerConfiguration(Guid id, string name, string email) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [email],
        IsActive = true
    };

    private static ExpirationsBrokerComponent Component(string value) => new()
    {
        RawValue = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value)
    };

    private static ExpirationsBrokerAssociation Association(Guid brokerId, string value) => new()
    {
        Id = AssociationId,
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Alias,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = true,
        CreatedAtUtc = CreatedAt,
        UpdatedAtUtc = UpdatedAt
    };

    private static ExpirationsExclusion Exclusion(string value, bool active) => new()
    {
        Id = ExclusionId,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = active,
        CreatedAtUtc = CreatedAt,
        UpdatedAtUtc = UpdatedAt
    };

    private static ExpirationsObservedIdentifier Observed(
        Guid brokerId,
        string value,
        bool ignored = false) => new()
    {
        Id = ExpirationsObservedIdentifierCaptureService.DeterministicId(
            brokerId,
            new ExpirationsBrokerNormalizer().Normalize(value)),
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Alias,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        FirstSeenAtUtc = CreatedAt,
        LastSeenAtUtc = UpdatedAt,
        IsIgnored = ignored
    };

    private static ExpirationsWorkbookReadResult Read(params ExpirationsSourceRow[] rows) => new()
    {
        Status = ExpirationsWorkbookReadStatus.Success,
        Workbook = new ExpirationsSourceWorkbook
        {
            SourcePath = "routing.xlsx",
            WorksheetName = "Detalle",
            HeaderRowNumber = 1,
            BrokerColumnIndex = 1,
            Rows = rows
        }
    };

    private static FirestoreStoredDocument<ExpirationsBrokerAssociation> Stored(
        ExpirationsBrokerAssociation value,
        string version) => new(value, $"modules/vencimientos/associations/{value.Id:D}", version);

    private static FirestoreStoredDocument<ExpirationsExclusion> Stored(
        ExpirationsExclusion value,
        string version) => new(value, $"modules/vencimientos/exclusions/{value.Id:D}", version);

    private static FirestoreStoredDocument<ExpirationsObservedIdentifier> Stored(
        ExpirationsObservedIdentifier value,
        string version) => new(value, $"modules/vencimientos/observedIdentifiers/{value.Id:D}", version);

    private sealed class FakeAssociationRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsBrokerAssociation>> documents)
        : IExpirationsBrokerAssociationRepository
    {
        private int _version = 1;
        public List<FirestoreStoredDocument<ExpirationsBrokerAssociation>> Documents { get; } = documents.ToList();
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>?> GetAsync(
            Guid associationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == associationId));
        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>>(Documents.ToList());
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> CreateAsync(
            ExpirationsBrokerAssociation value, CancellationToken cancellationToken = default)
        {
            var stored = Stored(value, $"a-{++_version}");
            Documents.Add(stored);
            return Task.FromResult(stored);
        }
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> UpdateAsync(
            ExpirationsBrokerAssociation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"associations/{value.Id:D}", expectedUpdateTime);
            var stored = Stored(value, $"a-{++_version}");
            Documents[index] = stored;
            return Task.FromResult(stored);
        }
        public Task DeleteAsync(
            Guid associationId,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == associationId);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"associations/{associationId:D}", expectedUpdateTime);
            Documents.RemoveAt(index);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExclusionRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsExclusion>> documents)
        : IExpirationsExclusionRepository
    {
        private int _version = 1;
        public List<FirestoreStoredDocument<ExpirationsExclusion>> Documents { get; } = documents.ToList();
        public Task<FirestoreStoredDocument<ExpirationsExclusion>?> GetAsync(
            Guid exclusionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == exclusionId));
        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>>(Documents.ToList());
        public Task<FirestoreStoredDocument<ExpirationsExclusion>> CreateAsync(
            ExpirationsExclusion value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FirestoreStoredDocument<ExpirationsExclusion>> UpdateAsync(
            ExpirationsExclusion value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"exclusions/{value.Id:D}", expectedUpdateTime);
            var stored = Stored(value, $"e-{++_version}");
            Documents[index] = stored;
            return Task.FromResult(stored);
        }
    }

    private sealed class FakeObservedRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsObservedIdentifier>>? documents = null)
        : IExpirationsObservedIdentifierRepository
    {
        private int _version = 3;
        public List<FirestoreStoredDocument<ExpirationsObservedIdentifier>> Documents { get; } =
            documents?.ToList() ?? [];

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>?> GetAsync(
            Guid identifierId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == identifierId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>>>(Documents.ToList());

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>> CreateAsync(
            ExpirationsObservedIdentifier value,
            CancellationToken cancellationToken = default)
        {
            var stored = Store(value);
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsObservedIdentifier>> UpdateAsync(
            ExpirationsObservedIdentifier value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"observedIdentifiers/{value.Id:D}", expectedUpdateTime);
            var stored = Store(value);
            Documents[index] = stored;
            return Task.FromResult(stored);
        }

        private FirestoreStoredDocument<ExpirationsObservedIdentifier> Store(
            ExpirationsObservedIdentifier value) => new(
            ExpirationsObservedIdentifierCaptureService.Copy(value),
            $"modules/vencimientos/observedIdentifiers/{value.Id:D}",
            $"o-{++_version}");
    }

    private sealed class FakeConfigurationService(params ExpirationsBrokerConfigurationItem[] brokers)
        : IExpirationsBrokerConfigurationService
    {
        public Task<IReadOnlyList<ExpirationsBrokerConfigurationItem>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpirationsBrokerConfigurationItem>>(brokers);
        public Task<ExpirationsBrokerConfigurationItem?> GetAsync(
            Guid brokerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(brokers.FirstOrDefault(broker => broker.BrokerId == brokerId));
        public Task<ExpirationsBrokerConfigurationSaveResult> SaveAsync(
            ExpirationsBrokerConfigurationItem configuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
