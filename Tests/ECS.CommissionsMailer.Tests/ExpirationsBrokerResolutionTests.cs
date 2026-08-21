using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsBrokerResolutionTests
{
    private static readonly Guid Arturo = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AnaLuisa = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Jerrika = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Henry = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Donald = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Andres = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Alberto = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Javier = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid Missing = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public void NormalizerUnifiesCaseWhitespaceUnicodeAccentsAndSafePunctuation()
    {
        var normalizer = new ExpirationsBrokerNormalizer();
        var values = new[]
        {
            "Arturo Quesada",
            "ARTURO QUESADA",
            "  Arturo   Quesada ",
            "Arturo Quesáda",
            "Arturo\tQuesada",
            "Arturo\r\nQuesada",
            "Arturo.Quesada"
        };

        Assert.All(values, value => Assert.Equal("ARTURO QUESADA", normalizer.Normalize(value)));
        Assert.Equal("AS1", normalizer.Normalize("AS1"));
        Assert.Equal("AS10", normalizer.Normalize("AS10"));
        Assert.NotEqual(normalizer.Normalize("AS1"), normalizer.Normalize("AS10"));
    }

    [Fact]
    public void ParserSplitsOnlyCommaSemicolonAndNewlines()
    {
        var parser = new ExpirationsBrokerCellParser();

        var comma = parser.Parse("Ana Luisa ALS7, Jerrika JHS");
        var semicolon = parser.Parse("Henry HD1; Donald DD1");
        var newline = parser.Parse("A\r\nB\nC");
        var preserved = parser.Parse("Nombre/Compuesto - Uno & Dos");

        Assert.Equal(["Ana Luisa ALS7", "Jerrika JHS"], comma.Select(item => item.RawValue));
        Assert.Equal(["Henry HD1", "Donald DD1"], semicolon.Select(item => item.RawValue));
        Assert.Equal(["A", "B", "C"], newline.Select(item => item.RawValue));
        Assert.Equal("Nombre/Compuesto - Uno & Dos", Assert.Single(preserved).RawValue);
    }

    [Theory]
    [InlineData("Arturo Quesada")]
    [InlineData("ARTURO QUESADA")]
    [InlineData("Arturo Quesáda")]
    public void CanonicalNameResolvesWithoutAssociation(string value)
    {
        var result = Resolver([Broker(Arturo, "Arturo Quesada")], []).Resolve(Component(value));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, result.Status);
        Assert.Equal(Arturo, result.ResolvedBrokerId);
        Assert.Empty(result.MatchedAssociationIds);
    }

    [Fact]
    public void KnownAliasResolvesByDelimitedPhrase()
    {
        var alias = Association(1, Arturo, ExpirationsAssociationKind.Alias, "Luis Arturo Quesada");

        var result = Resolver([Broker(Arturo, "Arturo Quesada")], [alias])
            .Resolve(Component("Luis Arturo Quesada AQO4"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, result.Status);
        Assert.Equal(Arturo, result.ResolvedBrokerId);
        Assert.Contains(alias.Id, result.MatchedAssociationIds);
    }

    [Fact]
    public void CodeUsesTokenBoundariesAndNeverPartiallyMatches()
    {
        var code = Association(1, Andres, ExpirationsAssociationKind.Code, "AS1");
        var resolver = Resolver([Broker(Andres, "Andrés")], [code]);

        var exact = resolver.Resolve(Component("Andrés AS1"));
        var partial = resolver.Resolve(Component("AS10"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, exact.Status);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, partial.Status);
    }

    [Fact]
    public void NameAliasAndSeveralCodesForSameBrokerResolveOnlyOneCandidate()
    {
        var associations = new[]
        {
            Association(1, Andres, ExpirationsAssociationKind.Name, "Andrés Salas"),
            Association(2, Andres, ExpirationsAssociationKind.Alias, "Andrés"),
            Association(3, Andres, ExpirationsAssociationKind.Code, "AS1"),
            Association(4, Andres, ExpirationsAssociationKind.Code, "AS5")
        };

        var result = Resolver([Broker(Andres, "Andrés Salas")], associations)
            .Resolve(Component("Andrés Salas AS1 AS5"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, result.Status);
        Assert.Equal(Andres, result.ResolvedBrokerId);
        Assert.Equal([Andres], result.CandidateBrokerIds);
        Assert.Equal(4, result.MatchedAssociationIds.Count);
    }

    [Fact]
    public void AmaConfiguredForTwoBrokersIsAmbiguousWithStableCandidates()
    {
        var associations = new[]
        {
            Association(2, Jerrika, ExpirationsAssociationKind.Code, "AMA"),
            Association(1, AnaLuisa, ExpirationsAssociationKind.Code, "AMA")
        };

        var result = Resolver(
                [Broker(Jerrika, "Jerrika"), Broker(AnaLuisa, "Ana Luisa")],
                associations)
            .Resolve(Component("AMA"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, result.Status);
        Assert.Equal([AnaLuisa, Jerrika], result.CandidateBrokerIds);
        Assert.Null(result.ResolvedBrokerId);
    }

    [Fact]
    public void UnknownTextDoesNotGuessOrFuzzyMatch()
    {
        var result = Resolver([Broker(Arturo, "Arturo Quesada")], [])
            .Resolve(Component("Arturo Quesadilla"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, result.Status);
        Assert.Empty(result.CandidateBrokerIds);
    }

    [Fact]
    public void UnambiguousInactiveBrokerIsBlockingAndIdentified()
    {
        var result = Resolver([Broker(Arturo, "Arturo Quesada", false)], [])
            .Resolve(Component("Arturo Quesada"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.InactiveBroker, result.Status);
        Assert.Equal(Arturo, result.ResolvedBrokerId);
    }

    [Fact]
    public void TwoCanonicalNamesInOneRowProduceTwoDestinations()
    {
        var resolution = RowResolver(
                [Broker(Henry, "Henry"), Broker(Donald, "Donald")],
                [])
            .Resolve(SourceRow(20, "Henry, Donald"));

        Assert.Equal([Henry, Donald], resolution.DistinctDestinationBrokerIds);
        Assert.False(resolution.HasBlockingIssues);
    }

    [Fact]
    public void AnaLuisaAndJerrikaCodesProduceBothDestinations()
    {
        var associations = new[]
        {
            Association(1, AnaLuisa, ExpirationsAssociationKind.Code, "ALS7"),
            Association(2, Jerrika, ExpirationsAssociationKind.Code, "JHS")
        };
        var resolution = RowResolver(
                [Broker(AnaLuisa, "Ana Luisa"), Broker(Jerrika, "Jerrika Hernández")],
                associations)
            .Resolve(SourceRow(21, "Ana Luisa ALS7, Jerrika Hernandez JHS"));

        Assert.Equal([AnaLuisa, Jerrika], resolution.DistinctDestinationBrokerIds);
        Assert.False(resolution.HasBlockingIssues);
    }

    [Fact]
    public void HenryAndDonaldConfiguredCodesRemainSeparate()
    {
        var associations = new[]
        {
            Association(1, Henry, ExpirationsAssociationKind.Code, "HD1"),
            Association(2, Donald, ExpirationsAssociationKind.Code, "DD1")
        };
        var resolution = RowResolver(
                [Broker(Henry, "Henry"), Broker(Donald, "Donald")],
                associations)
            .Resolve(SourceRow(22, "Henry HD1; Donald DD1"));

        Assert.Equal([Henry, Donald], resolution.DistinctDestinationBrokerIds);
    }

    [Theory]
    [MemberData(nameof(SpecialAssociationCases))]
    public void SpecialCasesResolveOnlyThroughConfiguredAssociations(
        string value,
        Guid expectedBrokerId,
        string canonicalName)
    {
        var association = Association(1, expectedBrokerId, ExpirationsAssociationKind.Alias, value);

        var result = Resolver([Broker(expectedBrokerId, canonicalName)], [association])
            .Resolve(Component(value));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, result.Status);
        Assert.Equal(expectedBrokerId, result.ResolvedBrokerId);
        Assert.Contains(association.Id, result.MatchedAssociationIds);
    }

    public static TheoryData<string, Guid, string> SpecialAssociationCases => new()
    {
        { "PC Guanacaste", Alberto, "Alberto Volio" },
        { "Hernán Varela", Javier, "Javier Martínez" },
        { "Luis Arturo Quesada", Arturo, "Arturo Quesada" }
    };

    [Fact]
    public void As1As5AndAs20ProduceAndresDestinationOnce()
    {
        var associations = new[]
        {
            Association(1, Andres, ExpirationsAssociationKind.Code, "AS1"),
            Association(2, Andres, ExpirationsAssociationKind.Code, "AS5"),
            Association(3, Andres, ExpirationsAssociationKind.Code, "AS20")
        };
        var resolution = RowResolver([Broker(Andres, "Andrés")], associations)
            .Resolve(SourceRow(23, "AS1, AS5, AS20"));

        Assert.Equal(3, resolution.Components.Count);
        Assert.All(
            resolution.Components,
            component => Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, component.Status));
        Assert.Equal([Andres], resolution.DistinctDestinationBrokerIds);
    }

    [Fact]
    public void InactiveAssociationDoesNotParticipate()
    {
        var inactive = Association(1, Arturo, ExpirationsAssociationKind.Alias, "Alias Nuevo", false);

        var result = Resolver([Broker(Arturo, "Arturo Quesada")], [inactive])
            .Resolve(Component("Alias Nuevo"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, result.Status);
        Assert.Empty(result.MatchedAssociationIds);
    }

    [Fact]
    public void AssociationWhoseBrokerIsMissingProducesBlockingDiagnostic()
    {
        var orphan = Association(1, Missing, ExpirationsAssociationKind.Code, "ORPHAN");

        var result = Resolver([Broker(Arturo, "Arturo Quesada")], [orphan])
            .Resolve(Component("ORPHAN"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, result.Status);
        Assert.Equal([Missing], result.UnknownCatalogBrokerIds);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void PartiallyResolvedRowKeepsKnownDestinationAndBlocksGeneration()
    {
        var code = Association(1, AnaLuisa, ExpirationsAssociationKind.Code, "ALS7");
        var row = SourceRow(24, "Ana Luisa ALS7, CODIGO_NUEVO");
        var read = SuccessfulRead(row);

        var result = new ExpirationsWorkbookAnalysisService().Analyze(
            read,
            [Broker(AnaLuisa, "Ana Luisa")],
            [code]);

        var resolution = Assert.Single(result.RowResolutions);
        Assert.Equal([AnaLuisa], resolution.DistinctDestinationBrokerIds);
        Assert.True(resolution.HasBlockingIssues);
        Assert.Equal(1, result.UnresolvedComponents);
        Assert.False(result.CanGenerate);
        Assert.Empty(result.ResolvedRowNumbersByBroker);
    }

    [Fact]
    public void DataRowWithoutBrokerBecomesMissingBroker()
    {
        var result = new ExpirationsWorkbookAnalysisService().Analyze(
            SuccessfulRead(SourceRow(25, string.Empty)),
            [Broker(Arturo, "Arturo Quesada")],
            []);

        Assert.Equal(1, result.MissingBrokerComponents);
        Assert.Equal(
            ExpirationsBrokerResolutionStatus.MissingBroker,
            Assert.Single(Assert.Single(result.RowResolutions).Components).Status);
        Assert.False(result.CanGenerate);
    }

    [Fact]
    public void InsurerCellNeverAssignsBroker()
    {
        var row = new ExpirationsSourceRow
        {
            RowNumber = 26,
            RawBrokerValue = "DESCONOCIDO",
            Cells =
            [
                new ExpirationsSourceCell { ColumnIndex = 1, DisplayText = "DESCONOCIDO" },
                new ExpirationsSourceCell { ColumnIndex = 2, DisplayText = "QUALITAS" }
            ]
        };

        var result = new ExpirationsWorkbookAnalysisService().Analyze(
            SuccessfulRead(row),
            [Broker(Andres, "Andrés")],
            [Association(1, Andres, ExpirationsAssociationKind.Alias, "QUALITAS")]);

        Assert.Equal(1, result.UnresolvedComponents);
        Assert.Empty(Assert.Single(result.RowResolutions).DistinctDestinationBrokerIds);
    }

    [Fact]
    public void ReaderSelectionErrorBlocksGlobalGeneration()
    {
        var read = new ExpirationsWorkbookReadResult
        {
            Status = ExpirationsWorkbookReadStatus.RequiresManualSelection,
            Messages = ["AmbiguousHeader"]
        };

        var result = new ExpirationsWorkbookAnalysisService().Analyze(read, [], []);

        Assert.False(result.CanGenerate);
        Assert.Equal(ExpirationsWorkbookReadStatus.RequiresManualSelection, result.ReadStatus);
        Assert.Equal("AmbiguousHeader", Assert.Single(result.Messages));
    }

    [Fact]
    public void CandidateOrderingDoesNotDependOnInputOrder()
    {
        var catalog = new[] { Broker(Jerrika, "Jerrika"), Broker(AnaLuisa, "Ana Luisa") };
        var associations = new[]
        {
            Association(2, Jerrika, ExpirationsAssociationKind.Code, "AMA"),
            Association(1, AnaLuisa, ExpirationsAssociationKind.Code, "AMA")
        };

        var forward = Resolver(catalog, associations).Resolve(Component("AMA"));
        var reverse = Resolver(catalog.Reverse(), associations.Reverse()).Resolve(Component("AMA"));

        Assert.Equal(forward.Status, reverse.Status);
        Assert.Equal(forward.CandidateBrokerIds, reverse.CandidateBrokerIds);
        Assert.Equal(forward.MatchedAssociationIds, reverse.MatchedAssociationIds);
    }

    private static ExpirationsBrokerResolver Resolver(
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations) => new(catalog, associations);

    private static ExpirationsRowResolutionService RowResolver(
        IEnumerable<ExpirationsBrokerCatalogItem> catalog,
        IEnumerable<ExpirationsBrokerAssociation> associations) =>
        new(new ExpirationsBrokerCellParser(), Resolver(catalog, associations));

    private static ExpirationsBrokerComponent Component(string value) => new()
    {
        RawValue = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value)
    };

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name, bool active = true) => new()
    {
        BrokerId = id,
        Name = name,
        IsActive = active
    };

    private static ExpirationsBrokerAssociation Association(
        int id,
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        bool active = true) => new()
    {
        Id = Guid.Parse($"{id:x8}-0000-0000-0000-000000000000"),
        BrokerId = brokerId,
        Kind = kind,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = active
    };

    private static ExpirationsSourceRow SourceRow(uint rowNumber, string brokerValue) => new()
    {
        RowNumber = rowNumber,
        RawBrokerValue = brokerValue,
        Cells =
        [
            new ExpirationsSourceCell
            {
                ColumnIndex = 1,
                ColumnReference = "A",
                RawText = brokerValue,
                DisplayText = brokerValue
            }
        ]
    };

    private static ExpirationsWorkbookReadResult SuccessfulRead(params ExpirationsSourceRow[] rows) => new()
    {
        Status = ExpirationsWorkbookReadStatus.Success,
        Workbook = new ExpirationsSourceWorkbook
        {
            SourcePath = "synthetic.xlsx",
            WorksheetName = "Datos",
            HeaderRowNumber = 1,
            BrokerColumnIndex = 1,
            Rows = rows
        }
    };
}
