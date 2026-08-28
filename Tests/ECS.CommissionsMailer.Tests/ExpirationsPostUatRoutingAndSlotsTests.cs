using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsPostUatRoutingAndSlotsTests
{
    private static readonly Guid Andres = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Alberto = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Javier = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Pc = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Normal = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid LegacyAndres = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Felix = Guid.Parse("6059c919-7198-45b9-a2c3-c6eab96a1503");

    [Theory]
    [Trait("Area", "AndresUatRouting")]
    [InlineData("XYZ - 107", ExpirationsDestinationGroup.Personales)]
    [InlineData("XYZ - 189", ExpirationsDestinationGroup.Personales)]
    [InlineData("XYZ - 94", ExpirationsDestinationGroup.Generales)]
    [InlineData("XYZ - 172", ExpirationsDestinationGroup.Generales)]
    [InlineData("AGENCIAS DIRECTO/AS24 - 167", ExpirationsDestinationGroup.Agencias)]
    [InlineData("BACARI QM AS19 - 145", ExpirationsDestinationGroup.Agencias)]
    [InlineData("BANCARI DA AS20 - 146", ExpirationsDestinationGroup.Agencias)]
    [InlineData("NUEVOS CC AS28 - 176", ExpirationsDestinationGroup.Agencias)]
    [InlineData("NUEVOS CM AS10 - 136", ExpirationsDestinationGroup.Agencias)]
    [InlineData("NUEVOS DA AS4 - 102", ExpirationsDestinationGroup.Agencias)]
    [InlineData("NUEVOS QM AS15 - 141", ExpirationsDestinationGroup.Agencias)]
    [InlineData("REF DA AS3 - 101", ExpirationsDestinationGroup.Agencias)]
    [InlineData("REF QM AS16 - 142", ExpirationsDestinationGroup.Agencias)]
    [InlineData("Taller DA AS2 - 95", ExpirationsDestinationGroup.Agencias)]
    [InlineData("USADOS CC AS30 - 178", ExpirationsDestinationGroup.Agencias)]
    [InlineData("USADOS DA AS21 - 147", ExpirationsDestinationGroup.Agencias)]
    [InlineData("USADOS QM AS17 - 143", ExpirationsDestinationGroup.Agencias)]
    public void AndresExactCodesAndAgencyTokensRouteToSeparateGroups(
        string identifier,
        ExpirationsDestinationGroup expectedGroup)
    {
        var analysis = Analyze([identifier]);

        var destination = Assert.Single(analysis.ResolvedRowNumbersByDestination).Key;
        Assert.Equal(Andres, destination.BrokerId);
        Assert.Equal(expectedGroup, destination.DestinationGroup);
    }

    [Theory]
    [Trait("Area", "AndresUatRouting")]
    [InlineData("1070")]
    [InlineData("108")]
    [InlineData("941")]
    [InlineData("DATO SIN MARCADOR")]
    [InlineData("COMERCIAL")]
    public void AndresDoesNotUseRangesOrAccidentalSubstrings(string identifier)
    {
        var analysis = Analyze([identifier]);

        Assert.False(analysis.CanGenerate);
        Assert.Empty(analysis.ResolvedRowNumbersByDestination);
    }

    [Fact]
    [Trait("Area", "AndresUatRouting")]
    public void AndresConflictingGroupsRemainPendingAndSameGroupIsDeduplicated()
    {
        var conflict = Analyze(["XYZ - 107, NUEVOS DA AS4 - 102"]);
        var sameGroup = Analyze(["AGENCIAS DIRECTO/AS24 - 167, BANCARI DA AS20 - 146"]);

        Assert.False(conflict.CanGenerate);
        Assert.Equal(1, conflict.RowsWithBlockingIssues);
        Assert.Empty(conflict.ResolvedRowNumbersByDestination);
        var agencias = Assert.Single(sameGroup.ResolvedRowNumbersByDestination);
        Assert.Equal(ExpirationsDestinationGroup.Agencias, agencias.Key.DestinationGroup);
        Assert.Equal([2U], agencias.Value);
    }

    [Fact]
    [Trait("Area", "NextMonthUniqueBrokerResolution")]
    public void NextMonthTreatsMultipleAndresGroupsAsOneResolvedFinalBroker()
    {
        var associations = new[]
        {
            Association(Andres, "Andres Steinberg/AS - 94", ExpirationsDestinationGroup.Principal),
            Association(Andres, "AGENCIAS DIRECTO/AS24 - 167", ExpirationsDestinationGroup.Principal)
        };
        var analysis = Analyze(
            ["Andres Steinberg/AS - 94, AGENCIAS DIRECTO/AS24 - 167"],
            associations,
            process: ExpirationsProcess.NextMonth);

        Assert.True(analysis.CanGenerate);
        Assert.Equal(0, analysis.UnresolvedComponents);
        Assert.Equal(0, analysis.AmbiguousComponents);
        var row = Assert.Single(analysis.RowResolutions);
        Assert.Equal([Andres], row.DistinctDestinationBrokerIds);
        Assert.Equal([2U], analysis.ResolvedRowNumbersByBroker[Andres]);
        Assert.All(row.Components, component =>
            Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, component.Status));
        Assert.Equal(
            associations.Select(association => association.Id).Order(),
            row.Components.SelectMany(component => component.MatchedAssociationIds).Distinct().Order());
        Assert.Equal(2, associations.Length);
    }

    [Fact]
    [Trait("Area", "NextMonthUniqueBrokerResolution")]
    public void NextMonthDistributesOneRowToTwoValidFinalBrokers()
    {
        var analysis = Analyze(
            ["Andres Steinberg/AS - 94, Corredor Normal"],
            process: ExpirationsProcess.NextMonth);

        Assert.True(analysis.CanGenerate);
        Assert.Equal(0, analysis.AmbiguousComponents);
        var row = Assert.Single(analysis.RowResolutions);
        Assert.Equal([Andres, Normal], row.DistinctDestinationBrokerIds.Order().ToArray());
        Assert.All(row.Components, component =>
            Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, component.Status));
        Assert.Equal([2U], analysis.ResolvedRowNumbersByBroker[Andres]);
        Assert.Equal([2U], analysis.ResolvedRowNumbersByBroker[Normal]);
    }

    [Fact]
    [Trait("Area", "NextMonthUniqueBrokerResolution")]
    public void NextMonthDistributesOneRowToThreeValidFinalBrokers()
    {
        var analysis = Analyze(
            ["Andres Steinberg/AS - 94, Corredor Normal, PC Guanacaste"],
            process: ExpirationsProcess.NextMonth);

        Assert.True(analysis.CanGenerate);
        Assert.Equal(0, analysis.AmbiguousComponents);
        Assert.Equal([Andres, Alberto, Normal],
            Assert.Single(analysis.RowResolutions).DistinctDestinationBrokerIds.Order().ToArray());
        Assert.All(
            analysis.ResolvedRowNumbersByBroker.Values,
            rows => Assert.Equal([2U], rows));
    }

    [Fact]
    [Trait("Area", "NextMonthUniqueBrokerResolution")]
    public void NextMonthKeepsARealAssociationConflictAmbiguous()
    {
        var analysis = Analyze(
            ["CODIGO COMPARTIDO"],
            [
                Association(Andres, "CODIGO COMPARTIDO", ExpirationsDestinationGroup.Principal),
                Association(Normal, "CODIGO COMPARTIDO", ExpirationsDestinationGroup.Principal)
            ],
            process: ExpirationsProcess.NextMonth);

        Assert.False(analysis.CanGenerate);
        Assert.Equal(1, analysis.AmbiguousComponents);
        var component = Assert.Single(Assert.Single(analysis.RowResolutions).Components);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, component.Status);
        Assert.Equal([Andres, Normal], component.CandidateBrokerIds.Order().ToArray());
    }

    [Fact]
    [Trait("Area", "NextMonthUniqueBrokerResolution")]
    public void PreviousMonthKeepsItsExistingSameBrokerGroupConflictRule()
    {
        var analysis = Analyze(
            ["Andres Steinberg/AS - 94, AGENCIAS DIRECTO/AS24 - 167"],
            process: ExpirationsProcess.PreviousMonth);

        Assert.False(analysis.CanGenerate);
        Assert.True(analysis.AmbiguousComponents > 0);
    }

    [Theory]
    [Trait("Area", "AndresUatRouting")]
    [Trait("Area", "LimitedUatRouting")]
    [InlineData("Andres Stein/VeinsaAS5 - 107", "Andres Stein", ExpirationsDestinationGroup.Personales)]
    [InlineData("Andres Steinberg/AS - 94", "Andres Steinberg", ExpirationsDestinationGroup.Generales)]
    [InlineData("NUEVOS DA AS4 - 102", "NUEVOS", ExpirationsDestinationGroup.Agencias)]
    public void AndresDeterministicSignalOverridesLegacyAssociationGroupWithoutMutatingIt(
        string identifier,
        string genericAssociation,
        ExpirationsDestinationGroup expectedGroup)
    {
        var association = Association(Andres, genericAssociation, ExpirationsDestinationGroup.Principal);

        var analysis = Analyze([identifier], [association]);

        Assert.Equal(expectedGroup,
            Assert.Single(analysis.ResolvedRowNumbersByDestination).Key.DestinationGroup);
        Assert.Equal(ExpirationsDestinationGroup.Principal, association.DestinationGroup);
    }

    [Fact]
    [Trait("Area", "LimitedUatRouting")]
    public void AndresPrincipalAssociationIsCompatibleWithDeterministicRouting()
    {
        const string identifier = "Andres Stein/VeinsaAS5 - 107";
        var conflict = Association(Andres, identifier, ExpirationsDestinationGroup.Principal);

        var analysis = Analyze([identifier], [conflict]);

        Assert.True(analysis.CanGenerate);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved,
            Assert.Single(Assert.Single(analysis.RowResolutions).Components).Status);
        Assert.Equal(ExpirationsDestinationGroup.Personales,
            Assert.Single(analysis.ResolvedRowNumbersByDestination).Key.DestinationGroup);
    }

    [Fact]
    [Trait("Area", "AndresUatRouting")]
    [Trait("Area", "LimitedUatRouting")]
    public void AndresCanonicalIdentityKeepsThreeGroupsWhenSeparateLegacyCatalogEntryExists()
    {
        string[] identifiers =
        [
            "XYZ - 107",
            "XYZ - 189",
            "XYZ - 94",
            "XYZ - 172",
            "NUEVOS DA AS4 - 102",
            "NUEVOS CC AS28 - 176",
            "NUEVOS CM AS10 - 136",
            "NUEVOS QM AS15 - 141",
            "AGENCIAS DIRECTO/AS24 - 167"
        ];
        var legacyAssociations = new[]
        {
            Association(Andres, "XYZ", ExpirationsDestinationGroup.Principal),
            Association(Andres, "NUEVOS", ExpirationsDestinationGroup.Principal),
            Association(Andres, "BANCARI", ExpirationsDestinationGroup.Principal),
            Association(Andres, "AGENCIAS DIRECTO", ExpirationsDestinationGroup.Principal)
        };
        var catalog = Catalog()
            .Append(Broker(LegacyAndres, "Andrés Steimberg - Seguru"))
            .ToList();

        var analysis = Analyze(identifiers, legacyAssociations, catalog);

        Assert.True(analysis.CanGenerate);
        var byGroup = analysis.ResolvedRowNumbersByDestination
            .Where(item => item.Key.BrokerId == Andres)
            .ToDictionary(item => item.Key.DestinationGroup, item => item.Value);
        Assert.Equal(3, byGroup.Count);
        Assert.Equal([2U, 3U], byGroup[ExpirationsDestinationGroup.Personales]);
        Assert.Equal([4U, 5U], byGroup[ExpirationsDestinationGroup.Generales]);
        Assert.Equal([6U, 7U, 8U, 9U, 10U], byGroup[ExpirationsDestinationGroup.Agencias]);
        Assert.DoesNotContain(ExpirationsDestinationGroup.Principal, byGroup.Keys);
    }

    [Fact]
    public void MultiBrokerRowIsPreservedOnceForEachBrokerAndGroup()
    {
        var analysis = Analyze(["107, Corredor Normal"]);

        Assert.True(analysis.CanGenerate);
        Assert.Equal(2, analysis.ResolvedRowNumbersByDestination.Count);
        Assert.All(analysis.ResolvedRowNumbersByDestination.Values, rows => Assert.Equal([2U], rows));
    }

    [Theory]
    [InlineData("PC Guanacaste", "Alberto Volio S", ExpirationsDestinationGroup.PcGuanacaste)]
    [InlineData("Contado Cori Motors", "Alberto Volio S", ExpirationsDestinationGroup.ContadoCoriMotors)]
    [InlineData("Varios Cori Motors", "Alberto Volio S", ExpirationsDestinationGroup.ContadoCoriMotors)]
    [InlineData("Hernán Varela", "Javier Martinez", ExpirationsDestinationGroup.HernanVarela)]
    public void SpecialBusinessIdentitiesUseRecipientAndSeparateFile(
        string identifier,
        string expectedRecipient,
        ExpirationsDestinationGroup expectedGroup)
    {
        var analysis = Analyze([identifier]);
        var destination = Assert.Single(analysis.ResolvedRowNumbersByDestination).Key;
        var recipient = Catalog().Single(item => item.BrokerId == destination.BrokerId);

        Assert.Equal(expectedRecipient, recipient.Name);
        Assert.Equal(expectedGroup, destination.DestinationGroup);
        Assert.NotEqual(Pc, destination.BrokerId);
    }

    [Fact]
    public void LegacyAssociationIsPrincipalForNormalBrokerButUnsafeForMultiGroupBroker()
    {
        var oldNormal = Association(Normal, "NORMAL-OLD", null);
        var oldAndres = Association(Andres, "ANDRES-OLD", null);
        var oldHernan = Association(Javier, "Hernán Varela/HVH - 99", null);

        var normal = Analyze(["NORMAL-OLD"], [oldNormal]);
        var multi = Analyze(["ANDRES-OLD"], [oldAndres]);
        var inferred = Analyze(["Hernán Varela/HVH - 99"], [oldHernan]);

        Assert.Equal(ExpirationsDestinationGroup.Principal,
            Assert.Single(normal.ResolvedRowNumbersByDestination).Key.DestinationGroup);
        Assert.False(multi.CanGenerate);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous,
            Assert.Single(Assert.Single(multi.RowResolutions).Components).Status);
        Assert.Equal(ExpirationsDestinationGroup.HernanVarela,
            Assert.Single(inferred.ResolvedRowNumbersByDestination).Key.DestinationGroup);
    }

    [Fact]
    public void AssociationMapperIsAdditiveAndRoundTripsDestinationGroup()
    {
        var mapper = new ExpirationsBrokerAssociationMapper();
        var value = Association(Andres, "NUEVO XX", ExpirationsDestinationGroup.Agencias);

        var fields = mapper.ToFields(value);
        var roundTrip = mapper.FromFields(fields);
        var legacyFields = fields.Where(item => item.Key != "destinationGroup")
            .ToDictionary(item => item.Key, item => item.Value);

        Assert.Equal(ExpirationsDestinationGroup.Agencias, roundTrip.DestinationGroup);
        Assert.Null(mapper.FromFields(legacyFields).DestinationGroup);
    }

    [Fact]
    public void AssociationEditorRequiresOnlyTypeAndValueForEveryBroker()
    {
        var multi = new ExpirationsAssociationEditorState(Configuration(Andres, "Andrés Steimberg - Agent for EssentialGroupLA"));
        var normal = new ExpirationsAssociationEditorState(Configuration(Normal, "Corredor Normal"));
        multi.Value = "NUEVO";
        normal.Value = "NUEVO";

        Assert.True(multi.CanSave);
        Assert.True(normal.CanSave);
        Assert.Equal(ExpirationsAssociationKind.Name, multi.BuildInput()!.Kind);
        Assert.Equal("NUEVO", multi.BuildInput()!.Value);
    }

    [Theory]
    [Trait("Area", "AndresUatRouting")]
    [Trait("Area", "LimitedUatRouting")]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task AndresGenerationConsolidatesFormerGroupsIntoOneFile(
        ExpirationsProcess process)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "groups.xlsx");
        CreateGroupedSource(source, process);
        var context = GroupedContext(source, Andres, process,
            (ExpirationsDestinationGroup.Personales, new uint[] { 8 }),
            (ExpirationsDestinationGroup.Generales, new uint[] { 9 }),
            (ExpirationsDestinationGroup.Agencias, new uint[] { 10 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(
                context,
                directory.Path,
                process == ExpirationsProcess.NextMonth ? new ExpirationsPeriod(2026, 8) : null),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(Andres, file.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.Principal, file.DestinationGroup);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, file.Variant);
        Assert.Equal([8U, 9U, 10U], file.SourceRowNumbers);
        Assert.True(File.Exists(file.OutputPath));
        var slot = Assert.Single(batch.RequiredAttachmentSlots);
        Assert.Equal(Andres, slot.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.Principal, slot.DestinationGroup);
    }

    [Fact]
    [Trait("Area", "LimitedUatRouting")]
    public void AlbertoContadoAndVariosShareOneEffectiveDestination()
    {
        var analysis = Analyze(["Contado Cori Motors", "Varios Cori Motors"]);

        var destination = Assert.Single(analysis.ResolvedRowNumbersByDestination);
        Assert.Equal(Alberto, destination.Key.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.ContadoCoriMotors, destination.Key.DestinationGroup);
        Assert.Equal([2U, 3U], destination.Value);
    }

    [Fact]
    public void OtherCoriMotorsAssociationKeepsItsConfiguredBroker()
    {
        var otherCori = Association(
            Normal,
            "OTRO CORI MOTORS",
            ExpirationsDestinationGroup.Principal);

        var analysis = Analyze(["OTRO CORI MOTORS"], [otherCori]);

        var destination = Assert.Single(analysis.ResolvedRowNumbersByDestination).Key;
        Assert.Equal(Normal, destination.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.Principal, destination.DestinationGroup);
    }

    [Theory]
    [Trait("Area", "LimitedUatRouting")]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task AlbertoGenerationConsolidatesOwnPcAndCoriGroupsIntoOneFile(
        ExpirationsProcess process)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "alberto-cori.xlsx");
        CreateGroupedSource(source, process, includeFourthRow: true);
        var context = GroupedContext(source, Alberto, process,
            (ExpirationsDestinationGroup.Principal, new uint[] { 8 }),
            (ExpirationsDestinationGroup.PcGuanacaste, new uint[] { 9 }),
            (ExpirationsDestinationGroup.ContadoCoriMotors, new uint[] { 10 }),
            (ExpirationsDestinationGroup.VariosCoriMotors, new uint[] { 11 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(
                context,
                directory.Path,
                process == ExpirationsProcess.NextMonth ? new ExpirationsPeriod(2026, 8) : null),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(Alberto, file.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.Principal, file.DestinationGroup);
        Assert.Equal([8U, 9U, 10U, 11U], file.SourceRowNumbers);
        Assert.Contains("Alberto Volio S", Path.GetFileName(file.OutputPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cori Motors", Path.GetFileName(file.OutputPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PC Guanacaste", Path.GetFileName(file.OutputPath), StringComparison.OrdinalIgnoreCase);
        var slot = Assert.Single(batch.RequiredAttachmentSlots);
        Assert.Equal(ExpirationsDestinationGroup.Principal, slot.DestinationGroup);
    }

    [Theory]
    [MemberData(nameof(ConsolidatedBrokers))]
    public void NextMonthPreflightBuildsOneCombinedPremiumPlanForConsolidatedBroker(Guid brokerId)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "combined-premiums.xlsx");
        var groups = brokerId == Andres
            ? new[]
            {
                (ExpirationsDestinationGroup.Personales, new uint[] { 8 }),
                (ExpirationsDestinationGroup.Generales, new uint[] { 9 }),
                (ExpirationsDestinationGroup.Agencias, new uint[] { 10 })
            }
            :
            [
                (ExpirationsDestinationGroup.Principal, new uint[] { 8 }),
                (ExpirationsDestinationGroup.PcGuanacaste, new uint[] { 9 }),
                (ExpirationsDestinationGroup.ContadoCoriMotors, new uint[] { 10 }),
                (ExpirationsDestinationGroup.VariosCoriMotors, new uint[] { 11 })
            ];
        CreateGroupedSource(
            source,
            ExpirationsProcess.NextMonth,
            includeFourthRow: groups.Length == 4);
        var context = GroupedContext(source, brokerId, ExpirationsProcess.NextMonth, groups);

        var result = new ExpirationsNextMonthGenerationPreflightService().Validate(
            context,
            new ExpirationsPeriod(2026, 8),
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = Assert.Single(result.PremiumTotalsPlansByDestination);
        Assert.Equal(brokerId, plan.Key.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.Principal, plan.Key.DestinationGroup);
        Assert.Equal(groups.Length, plan.Value.RowCount);
    }

    [Fact]
    [Trait("Area", "AndresUatRouting")]
    public async Task AndresGenerationConsolidatesOnlyTheFormerGroupsThatHaveRows()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "groups-present.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = GroupedContext(source, Andres,
            (ExpirationsDestinationGroup.Personales, new uint[] { 8 }),
            (ExpirationsDestinationGroup.Agencias, new uint[] { 10 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(ExpirationsDestinationGroup.Principal, file.DestinationGroup);
        Assert.Equal([8U, 10U], file.SourceRowNumbers);
    }

    [Fact]
    [Trait("Area", "AndresUatRouting")]
    public async Task AndresThreeGroupsPrepareOneEmailWithThreeAttachments()
    {
        using var files = new SlotFiles();
        ExpirationsDestinationGroup[] groups =
        [
            ExpirationsDestinationGroup.Personales,
            ExpirationsDestinationGroup.Generales,
            ExpirationsDestinationGroup.Agencias
        ];
        var generated = groups.Select(group => files.Generated(
            Andres,
            $"{group}.xlsx",
            group)).ToList();
        var slots = groups.Select(group => Slot(Andres, group)).ToList();

        var result = await PreparationService(
                Andres,
                "Andrés Steimberg - Agent for EssentialGroupLA")
            .PrepareSelectedAsync(
                files.Batch(generated, slots),
                [Andres],
                null,
                TestContext.Current.CancellationToken);

        Assert.True(result.CanSend);
        Assert.Equal(3, Assert.Single(result.Requests).AttachmentPaths.Count);
        Assert.Equal(3, Assert.Single(result.PreparedItems).Attachments.Count);
    }

    [Fact]
    public async Task OnlyHernanCreatesOneJavierAttachmentAndNoEmptyPrincipal()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "hernan.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = GroupedContext(source, Javier,
            (ExpirationsDestinationGroup.HernanVarela, new uint[] { 8 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(Javier, file.BrokerId);
        Assert.Equal(ExpirationsDestinationGroup.HernanVarela, file.DestinationGroup);
        Assert.DoesNotContain(batch.RequiredAttachmentSlots,
            slot => slot.DestinationGroup == ExpirationsDestinationGroup.Principal);
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task JavierAndHernanRemainSeparateFilesForTheSameEmail(
        ExpirationsProcess process)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "javier-hernan.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = GroupedContext(source, Javier, process,
            (ExpirationsDestinationGroup.Principal, new uint[] { 8 }),
            (ExpirationsDestinationGroup.HernanVarela, new uint[] { 9 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);
        var preparation = await PreparationService(Javier, "Javier Martinez")
            .PrepareSelectedAsync(
                batch,
                [Javier],
                null,
                TestContext.Current.CancellationToken);

        Assert.Equal(2, batch.Files.Count);
        Assert.Equal(
            [ExpirationsDestinationGroup.Principal, ExpirationsDestinationGroup.HernanVarela],
            batch.Files.Select(file => file.DestinationGroup).Order());
        Assert.True(preparation.CanSend);
        Assert.Equal(2, Assert.Single(preparation.PreparedItems).Attachments.Count);
    }

    [Fact]
    public async Task ManualReplacementSatisfiesOnlyItsLogicalSlotAndAdditionalDoesNot()
    {
        using var files = new SlotFiles();
        var personales = files.Generated(Andres, "personales.xlsx", ExpirationsDestinationGroup.Personales);
        var replacement = files.Manual(Andres, "generales-manual.xlsx",
            new(Andres, ExpirationsDestinationGroup.Generales, ExpirationsGeneratedFileVariant.Standard));
        var additional = files.Manual(Andres, "adicional.xlsx", null);
        var slots = new[]
        {
            Slot(Andres, ExpirationsDestinationGroup.Personales),
            Slot(Andres, ExpirationsDestinationGroup.Generales)
        };
        var service = PreparationService(Andres, "Andrés Steimberg - Agent for EssentialGroupLA");

        var valid = await service.PrepareSelectedAsync(
            files.Batch([personales, replacement], slots),
            [Andres], null, TestContext.Current.CancellationToken);
        var invalid = await service.PrepareSelectedAsync(
            files.Batch([personales, additional], slots),
            [Andres], null, TestContext.Current.CancellationToken);

        Assert.True(valid.CanSend);
        Assert.Equal(2, Assert.Single(valid.Requests).AttachmentPaths.Count);
        Assert.False(invalid.CanSend);
        Assert.Contains(invalid.Errors, error => error.Contains("Generales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddManualThenRemoveAutomaticKeepsSlotAndAllowsExplicitReplacement()
    {
        using var files = new SlotFiles();
        var automatic = files.Generated(Andres, "automatico.xlsx", ExpirationsDestinationGroup.Personales);
        var slot = Slot(Andres, ExpirationsDestinationGroup.Personales);
        var initial = files.Batch([automatic], [slot]);
        var association = new ExpirationsBatchFileAssociationService();

        var added = association.AddManual(
            initial,
            Andres,
            "Andrés Steimberg - Agent for EssentialGroupLA",
            automatic.OutputPath,
            slot.Key);
        var onlyManual = association.Remove(added.Batch, automatic);
        var prepared = await PreparationService(Andres, "Andrés Steimberg - Agent for EssentialGroupLA")
            .PrepareSelectedAsync(onlyManual, [Andres], null, TestContext.Current.CancellationToken);

        Assert.True(added.Succeeded);
        Assert.Single(onlyManual.RequiredAttachmentSlots);
        Assert.Equal(slot.Key, Assert.Single(onlyManual.Files).ReplacesSlot);
        Assert.True(prepared.CanSend);
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task RemovePrincipalThenAddManualAutomaticallyFillsTheMissingPrincipalSlot(
        ExpirationsProcess process)
    {
        using var files = new SlotFiles();
        var automatic = files.Generated(Andres, "principal.xlsx", ExpirationsDestinationGroup.Principal);
        var slot = Slot(Andres, ExpirationsDestinationGroup.Principal);
        var initial = files.Batch([automatic], [slot], process);
        var association = new ExpirationsBatchFileAssociationService();

        var withoutPrincipal = association.Remove(initial, automatic);
        var missing = await PreparationService(Andres, "Andrés Steimberg - Agent for EssentialGroupLA")
            .PrepareSelectedAsync(withoutPrincipal, [Andres], null, TestContext.Current.CancellationToken);
        var added = association.AddManual(
            withoutPrincipal,
            Andres,
            "Andrés Steimberg - Agent for EssentialGroupLA",
            automatic.OutputPath);
        var prepared = await PreparationService(Andres, "Andrés Steimberg - Agent for EssentialGroupLA")
            .PrepareSelectedAsync(added.Batch, [Andres], null, TestContext.Current.CancellationToken);

        Assert.False(missing.CanSend);
        Assert.Contains(missing.Errors, error => error.Contains("Falta el archivo Principal.", StringComparison.Ordinal));
        Assert.True(added.Succeeded);
        Assert.Equal(slot.Key, added.File!.ReplacesSlot);
        Assert.True(prepared.CanSend);
        Assert.DoesNotContain(prepared.Errors, error => error.Contains("Falta el archivo Principal.", StringComparison.Ordinal));
        Assert.Equal(added.File.OutputPath, Assert.Single(prepared.Requests).AttachmentPaths.Single());
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task AddManualWhilePrincipalExistsKeepsTheGeneratedPrincipalAndAddsTheManual(
        ExpirationsProcess process)
    {
        using var files = new SlotFiles();
        var automatic = files.Generated(Alberto, "principal.xlsx", ExpirationsDestinationGroup.Principal);
        var slot = Slot(Alberto, ExpirationsDestinationGroup.Principal);
        var initial = files.Batch([automatic], [slot], process);
        var association = new ExpirationsBatchFileAssociationService();

        var added = association.AddManual(initial, Alberto, "Alberto Volio S", automatic.OutputPath);
        var prepared = await PreparationService(Alberto, "Alberto Volio S")
            .PrepareSelectedAsync(added.Batch, [Alberto], null, TestContext.Current.CancellationToken);

        Assert.True(added.Succeeded);
        Assert.Null(added.File!.ReplacesSlot);
        Assert.Contains(added.Batch.Files, file => file == automatic);
        Assert.True(prepared.CanSend);
        Assert.Equal(
            [automatic.OutputPath, added.File.OutputPath],
            Assert.Single(prepared.Requests).AttachmentPaths);
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task AddSingleManualThenRemovePrincipalPromotesTheManual(
        ExpirationsProcess process)
    {
        using var files = new SlotFiles();
        var automatic = files.Generated(Andres, "principal.xlsx", ExpirationsDestinationGroup.Principal);
        var slot = Slot(Andres, ExpirationsDestinationGroup.Principal);
        var initial = files.Batch([automatic], [slot], process);
        var association = new ExpirationsBatchFileAssociationService();

        var added = association.AddManual(
            initial,
            Andres,
            "Andrés Steimberg - Agent for EssentialGroupLA",
            automatic.OutputPath);
        var promoted = association.Remove(added.Batch, automatic);
        var manual = Assert.Single(promoted.Files);
        var prepared = await PreparationService(Andres, "Andrés Steimberg - Agent for EssentialGroupLA")
            .PrepareSelectedAsync(promoted, [Andres], null, TestContext.Current.CancellationToken);

        Assert.True(added.Succeeded);
        Assert.Null(added.File!.ReplacesSlot);
        Assert.Equal(slot.Key, manual.ReplacesSlot);
        Assert.Equal(ExpirationsDestinationGroup.Principal, manual.DestinationGroup);
        Assert.True(prepared.CanSend);
        Assert.DoesNotContain(prepared.Errors, error => error.Contains("Falta el archivo Principal.", StringComparison.Ordinal));
        Assert.Equal(manual.OutputPath, Assert.Single(prepared.Requests).AttachmentPaths.Single());
    }

    [Theory]
    [InlineData(ExpirationsProcess.PreviousMonth)]
    [InlineData(ExpirationsProcess.NextMonth)]
    [InlineData(ExpirationsProcess.Cancellations)]
    public async Task RemovePrincipalWithSeveralManualCandidatesKeepsTheMissingWarning(
        ExpirationsProcess process)
    {
        using var files = new SlotFiles();
        var automatic = files.Generated(Alberto, "principal.xlsx", ExpirationsDestinationGroup.Principal);
        var slot = Slot(Alberto, ExpirationsDestinationGroup.Principal);
        var initial = files.Batch([automatic], [slot], process);
        var association = new ExpirationsBatchFileAssociationService();

        var first = association.AddManual(initial, Alberto, "Alberto Volio S", automatic.OutputPath);
        var second = association.AddManual(first.Batch, Alberto, "Alberto Volio S", automatic.OutputPath);
        var withoutPrincipal = association.Remove(second.Batch, automatic);
        var prepared = await PreparationService(Alberto, "Alberto Volio S")
            .PrepareSelectedAsync(withoutPrincipal, [Alberto], null, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, withoutPrincipal.Files.Count);
        Assert.All(withoutPrincipal.Files, manual => Assert.Null(manual.ReplacesSlot));
        Assert.False(prepared.CanSend);
        Assert.Contains(prepared.Errors, error => error.Contains("Falta el archivo Principal.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FelixUsesOneStandardWorkbookForCancellations()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "felix-cancelaciones.xlsx");
        CreateGroupedSource(source, ExpirationsProcess.Cancellations);
        var context = GroupedContext(
            source,
            Felix,
            ExpirationsProcess.Cancellations,
            (ExpirationsDestinationGroup.Principal, new uint[] { 8, 10 }));

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, file.Variant);
        Assert.StartsWith("Cancelaciones - ", Path.GetFileName(file.OutputPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SeparatedNextMonthWorkbookUsesDynamicCurrencyFormulasAndAutoRecalculation()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "andres-source.xlsx");
        var output = Path.Combine(directory.Path, "andres-generales.xlsx");
        ExpirationsPremiumTestWorkbook.Create(
            source,
            "Reporte",
            1,
            ["Broker", "Prima", "Moneda"],
            2,
            3,
            [new PremiumTestRow(2, "100", "CRC", "GENERAL")]);

        new ExpirationsNextMonthStandardWorkbookGenerator().Generate(
            new ExpirationsNextMonthStandardWorkbookGenerationRequest(
                source, output, "Reporte", 1, [2]),
            TestContext.Current.CancellationToken);

        using var document = SpreadsheetDocument.Open(output, false);
        var workbook = document.WorkbookPart!.Workbook!;
        var totalsSheet = workbook.Sheets!.Elements<Sheet>().Last();
        var totals = ((WorksheetPart)document.WorkbookPart.GetPartById(totalsSheet.Id!)).Worksheet!;
        var formulas = totals.Descendants<Cell>().Select(cell => cell.CellFormula?.Text)
            .Where(formula => formula is not null).Cast<string>().ToList();
        Assert.All(formulas, formula =>
        {
            Assert.Contains("$B:$B", formula, StringComparison.Ordinal);
            Assert.Contains("$C:$C", formula, StringComparison.Ordinal);
            Assert.DoesNotContain("$B$2:$B$2", formula, StringComparison.Ordinal);
            Assert.Contains("LOOKUP", formula, StringComparison.Ordinal);
            Assert.Contains("TRIM", formula, StringComparison.Ordinal);
        });
        Assert.Contains(formulas, formula => formula.Contains("\"CRC\"", StringComparison.Ordinal));
        Assert.Contains(formulas, formula => formula.Contains("\"USD\"", StringComparison.Ordinal));
        Assert.Equal(CalculateModeValues.Auto, workbook.CalculationProperties!.CalculationMode!.Value);
        Assert.True(workbook.CalculationProperties.FullCalculationOnLoad!.Value);
    }

    [Fact]
    public async Task FelixReplacementSatisfiesOneVariantWhileOtherRemainsRequired()
    {
        using var files = new SlotFiles();
        var alphabetical = files.Generated(
            Javier,
            "alfabetico.xlsx",
            ExpirationsDestinationGroup.Principal,
            ExpirationsGeneratedFileVariant.FelixAlphabetical);
        var expirationReplacement = files.Manual(Javier, "vencimiento-manual.xlsx",
            new(Javier, ExpirationsDestinationGroup.Principal,
                ExpirationsGeneratedFileVariant.FelixExpirationDate));
        var slots = new[]
        {
            new ExpirationsRequiredAttachmentSlot(Javier, ExpirationsDestinationGroup.Principal,
                ExpirationsGeneratedFileVariant.FelixAlphabetical),
            new ExpirationsRequiredAttachmentSlot(Javier, ExpirationsDestinationGroup.Principal,
                ExpirationsGeneratedFileVariant.FelixExpirationDate)
        };
        var service = PreparationService(Javier, "Javier Martinez", ExpirationsNextMonthGenerationMode.SpecialDualSorted);

        var result = await service.PrepareSelectedAsync(
            files.Batch([alphabetical, expirationReplacement], slots, ExpirationsProcess.NextMonth),
            [Javier], null, TestContext.Current.CancellationToken);

        Assert.True(result.CanSend);
        Assert.Equal(2, Assert.Single(result.PreparedItems).Attachments.Count);
    }

    [Theory]
    [MemberData(nameof(GroupedRecipients))]
    public async Task GroupedRecipientGetsOneEmailWithEveryPresentStandardGroup(
        Guid brokerId,
        string brokerName,
        ExpirationsDestinationGroup[] groups)
    {
        using var files = new SlotFiles();
        var generated = groups.Select(group => files.Generated(
            brokerId,
            $"{group}.xlsx",
            group)).ToList();
        var slots = groups.Select(group => Slot(brokerId, group)).ToList();

        var result = await PreparationService(brokerId, brokerName).PrepareSelectedAsync(
            files.Batch(generated, slots),
            [brokerId], null, TestContext.Current.CancellationToken);

        Assert.True(result.CanSend);
        Assert.Equal(groups.Length, Assert.Single(result.Requests).AttachmentPaths.Count);
        Assert.Single(result.PreparedItems);
    }

    public static TheoryData<Guid, string, ExpirationsDestinationGroup[]> GroupedRecipients => new()
    {
        {
            Andres,
            "Andrés Steimberg - Agent for EssentialGroupLA",
            [ExpirationsDestinationGroup.Personales, ExpirationsDestinationGroup.Generales,
                ExpirationsDestinationGroup.Agencias]
        },
        {
            Alberto,
            "Alberto Volio S",
            [ExpirationsDestinationGroup.Principal, ExpirationsDestinationGroup.PcGuanacaste,
                ExpirationsDestinationGroup.ContadoCoriMotors, ExpirationsDestinationGroup.VariosCoriMotors]
        },
        {
            Javier,
            "Javier Martinez",
            [ExpirationsDestinationGroup.Principal, ExpirationsDestinationGroup.HernanVarela]
        }
    };

    public static TheoryData<Guid> ConsolidatedBrokers => new()
    {
        Andres,
        Alberto
    };

    [Fact]
    public void RetryPreservesMultipleStandardAttachmentsDistinguishedByFileName()
    {
        using var files = new SlotFiles();
        var one = files.Generated(Alberto, "alberto.xlsx", ExpirationsDestinationGroup.Principal);
        var two = files.Generated(Alberto, "pc-guanacaste.xlsx", ExpirationsDestinationGroup.PcGuanacaste);
        var batch = files.Batch([one, two],
            [Slot(Alberto, ExpirationsDestinationGroup.Principal), Slot(Alberto, ExpirationsDestinationGroup.PcGuanacaste)]);
        var operation = new ExpirationsSendOperation
        {
            OperationId = Guid.NewGuid(),
            Process = ExpirationsProcess.PreviousMonth,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            SendingAccountEmail = "sender@example.test",
            Subject = "Asunto",
            Body = "Mensaje",
            Status = ExpirationsSendOperationStatus.Completed,
            TotalCount = 1,
            FailureCount = 1
        };
        var item = new ExpirationsSendHistoryItem
        {
            ItemId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            BrokerId = Alberto,
            BrokerName = "Alberto Volio S",
            ToRecipients = ["alberto@example.test"],
            Attachments = [History(one), History(two)],
            Status = ExpirationsSendItemStatus.Failed
        };

        var result = new ExpirationsRetryPreparationService().Prepare(operation, [item], batch);

        Assert.True(result.CanRetry);
        Assert.Equal(2, Assert.Single(result.Preparation!.PreparedItems).Attachments.Count);
    }

    private static ExpirationsWorkbookAnalysisResult Analyze(
        IReadOnlyList<string> values,
        IReadOnlyList<ExpirationsBrokerAssociation>? associations = null,
        IReadOnlyList<ExpirationsBrokerCatalogItem>? catalog = null,
        ExpirationsProcess? process = null)
    {
        var rows = values.Select((value, index) => new ExpirationsSourceRow
        {
            RowNumber = (uint)(index + 2),
            RawBrokerValue = value
        }).ToList();
        return new ExpirationsWorkbookAnalysisService().Analyze(
            new ExpirationsWorkbookReadResult
            {
                Status = ExpirationsWorkbookReadStatus.Success,
                Workbook = new ExpirationsSourceWorkbook
                {
                    WorksheetName = "Reporte",
                    HeaderRowNumber = 1,
                    BrokerColumnIndex = 1,
                    Rows = rows
                }
            },
            catalog ?? Catalog(),
            associations ?? [],
            [],
            [],
            process);
    }

    private static IReadOnlyList<ExpirationsBrokerCatalogItem> Catalog() =>
    [
        Broker(Andres, "Andrés Steimberg - Agent for EssentialGroupLA"),
        Broker(Alberto, "Alberto Volio S"),
        Broker(Javier, "Javier Martinez"),
        Broker(Pc, "PC Guanacaste"),
        Broker(Normal, "Corredor Normal"),
        Broker(Felix, "Félix Lara", ExpirationsNextMonthGenerationMode.SpecialDualSorted)
    ];

    private static ExpirationsGenerationContext GroupedContext(
        string source,
        Guid brokerId,
        params (ExpirationsDestinationGroup Group, uint[] Rows)[] groups)
        => GroupedContext(source, brokerId, ExpirationsProcess.PreviousMonth, groups);

    private static ExpirationsGenerationContext GroupedContext(
        string source,
        Guid brokerId,
        ExpirationsProcess process,
        params (ExpirationsDestinationGroup Group, uint[] Rows)[] groups)
    {
        var broker = Catalog().Single(item => item.BrokerId == brokerId);
        var catalog = process == ExpirationsProcess.NextMonth
            ? new[]
            {
                broker,
                Broker(
                    Javier,
                    "Javier Martinez",
                    ExpirationsNextMonthGenerationMode.SpecialDualSorted)
            }
            : [broker];
        var rows = groups.SelectMany(group => group.Rows).Distinct().Order().Select(row => new ExpirationsSourceRow
        {
            RowNumber = row,
            RawBrokerValue = "resuelto"
        }).ToList();
        var destinations = groups.ToDictionary(
            group => new ExpirationsDestinationKey(brokerId, group.Group),
            group => (IReadOnlyList<uint>)group.Rows);
        return new ExpirationsGenerationContext(
            process,
            source,
            new GeneratedFileHashService().ComputeSha256(source),
            new ExpirationsSourceWorkbook
            {
                SourcePath = source,
                WorksheetName = ExpirationsGenerationTestWorkbook.ReportSheetName,
                HeaderRowNumber = 7,
                BrokerColumnIndex = 1,
                Rows = rows
            },
            new ExpirationsWorkbookAnalysisResult
            {
                ReadStatus = ExpirationsWorkbookReadStatus.Success,
                TotalRows = rows.Count,
                ResolvedRows = rows.Count,
                CanGenerate = true,
                ResolvedRowNumbersByBroker = destinations
                    .GroupBy(item => item.Key.BrokerId)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<uint>)group.SelectMany(item => item.Value).Distinct().ToList()),
                ResolvedRowNumbersByDestination = destinations
            },
            catalog);
    }

    private static void CreateGroupedSource(
        string source,
        ExpirationsProcess process,
        bool includeFourthRow = false)
    {
        if (process != ExpirationsProcess.NextMonth)
        {
            ExpirationsGenerationTestWorkbook.Create(
                source,
                extraDataRows: includeFourthRow ? 1 : 0);
            return;
        }

        var rows = new List<PremiumTestRow>
        {
            new(8, "100", "CRC", "GRUPO-1"),
            new(9, "200", "USD", "GRUPO-2"),
            new(10, "300", "CRC", "GRUPO-3")
        };
        if (includeFourthRow)
            rows.Add(new PremiumTestRow(11, "400", "USD", "GRUPO-4"));
        ExpirationsPremiumTestWorkbook.Create(
            source,
            ExpirationsGenerationTestWorkbook.ReportSheetName,
            7,
            ["Broker", "Prima", "Moneda", "Dato"],
            2,
            3,
            rows);
    }

    private static ExpirationsBrokerAssociation Association(
        Guid brokerId,
        string value,
        ExpirationsDestinationGroup? group) => new()
    {
        Id = Guid.NewGuid(),
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Alias,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        DestinationGroup = group,
        IsActive = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ExpirationsBrokerCatalogItem Broker(
        Guid id,
        string name,
        ExpirationsNextMonthGenerationMode mode = ExpirationsNextMonthGenerationMode.Standard) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:N}@example.test"],
        IsActive = true,
        NextMonthGenerationMode = mode
    };

    private static ExpirationsBrokerConfigurationItem Configuration(Guid id, string name) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:N}@example.test"],
        IsActive = true
    };

    private static ExpirationsRequiredAttachmentSlot Slot(
        Guid brokerId,
        ExpirationsDestinationGroup group) => new(
        brokerId,
        group,
        ExpirationsGeneratedFileVariant.Standard);

    private static ExpirationsSendAttachment History(ExpirationsGeneratedFile file) => new(
        Path.GetFileName(file.OutputPath),
        file.Sha256,
        file.Variant);

    private static ExpirationsSendPreparationService PreparationService(
        Guid brokerId,
        string name,
        ExpirationsNextMonthGenerationMode mode = ExpirationsNextMonthGenerationMode.Standard) => new(
        new SettingsRepository(),
        new ExpirationsBrokerCatalogService(
            new DirectoryRepository(new ExpirationsBrokerDirectoryEntry
            {
                BrokerId = brokerId,
                Name = name,
                PrimaryEmailAddresses = [$"{brokerId:N}@example.test"]
            }),
            new ProfileRepository(new ExpirationsBrokerProfile
            {
                BrokerId = brokerId,
                IsActive = true,
                NextMonthGenerationMode = mode,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            })));

    private sealed class SlotFiles : IDisposable
    {
        private readonly GeneratedFileHashService _hash = new();

        public SlotFiles()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"ecs-exp-slots-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public ExpirationsGeneratedFile Generated(
            Guid brokerId,
            string name,
            ExpirationsDestinationGroup group,
            ExpirationsGeneratedFileVariant variant = ExpirationsGeneratedFileVariant.Standard) =>
            File(brokerId, name, group, variant, null);

        public ExpirationsGeneratedFile Manual(
            Guid brokerId,
            string name,
            ExpirationsAttachmentSlotKey? replacement) =>
            File(
                brokerId,
                name,
                replacement?.DestinationGroup ?? ExpirationsDestinationGroup.Principal,
                ExpirationsGeneratedFileVariant.Manual,
                replacement);

        public ExpirationsGenerationBatch Batch(
            IReadOnlyList<ExpirationsGeneratedFile> files,
            IReadOnlyList<ExpirationsRequiredAttachmentSlot> slots,
            ExpirationsProcess process = ExpirationsProcess.PreviousMonth) => new()
        {
            Id = Guid.NewGuid(),
            Process = process,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OutputDirectory = Directory,
            ParticipatingBrokerIds = slots.Select(slot => slot.BrokerId).ToHashSet(),
            RequiredAttachmentSlots = slots,
            Files = files
        };

        private ExpirationsGeneratedFile File(
            Guid brokerId,
            string name,
            ExpirationsDestinationGroup group,
            ExpirationsGeneratedFileVariant variant,
            ExpirationsAttachmentSlotKey? replacement)
        {
            var path = Path.Combine(Directory, name);
            ExpirationsUatCompletionTests.CreateValidWorkbook(path, name);
            return new ExpirationsGeneratedFile
            {
                BrokerId = brokerId,
                BrokerName = brokerId.ToString("D"),
                OutputPath = path,
                Variant = variant,
                DestinationGroup = group,
                ReplacesSlot = replacement,
                Sha256 = _hash.ComputeSha256(path),
                RequiresReview = variant == ExpirationsGeneratedFileVariant.Manual
            };
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class SettingsRepository : IExpirationsProcessSettingsRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
            ExpirationsProcess process,
            CancellationToken cancellationToken = default) => Task.FromResult<FirestoreStoredDocument<ExpirationsProcessSettings>?>(new(
            new ExpirationsProcessSettings
            {
                DefaultSubject = "Asunto",
                DefaultMessage = "Mensaje",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, "settings", "v1"));

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
            ExpirationsProcess process,
            ExpirationsProcessSettings value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DirectoryRepository(ExpirationsBrokerDirectoryEntry value)
        : IExpirationsBrokerDirectoryRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) => Task.FromResult<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?>(
            brokerId == value.BrokerId ? Store(value) : null);

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>>([Store(value)]);

        private static FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry> Store(
            ExpirationsBrokerDirectoryEntry item) => new(item, "broker", "v1");
    }

    private sealed class ProfileRepository(ExpirationsBrokerProfile value) : IExpirationsBrokerProfileRepository
    {
        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) => Task.FromResult<FirestoreStoredDocument<ExpirationsBrokerProfile>?>(
            brokerId == value.BrokerId ? Store(value) : null);

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>>([Store(value)]);

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile profile,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile profile,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static FirestoreStoredDocument<ExpirationsBrokerProfile> Store(
            ExpirationsBrokerProfile item) => new(item, "profile", "v1");
    }
}
