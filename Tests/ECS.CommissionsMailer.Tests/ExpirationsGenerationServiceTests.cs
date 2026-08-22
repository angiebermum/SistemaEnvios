using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsGenerationServiceTests
{
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Jerrika = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Henry = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Donald = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Andres = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Alberto = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Javier = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task MaterializesAuthoritativeDistributionOncePerBrokerIdWithSharedRowsAndEmailWarning()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "distribution.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var brokers = new[]
        {
            Broker(Ana, "Ana Luisa"),
            Broker(Jerrika, "Jerrika"),
            Broker(Henry, "Henry"),
            Broker(Donald, "Donald", []),
            Broker(Andres, "Andrés"),
            Broker(Alberto, "Alberto"),
            Broker(Javier, "Javier")
        };
        IReadOnlyDictionary<Guid, IReadOnlyList<uint>> rows = new Dictionary<Guid, IReadOnlyList<uint>>
        {
            [Ana] = [8],
            [Jerrika] = [8],
            [Henry] = [9],
            [Donald] = [10],
            [Andres] = [8, 8, 8],
            [Alberto] = [8],
            [Javier] = [10]
        };
        var context = ExpirationsGenerationTestWorkbook.Context(
            source,
            ExpirationsProcess.PreviousMonth,
            brokers,
            rows);

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, batch.Files.Count);
        Assert.Equal(
            batch.Files.Select(file => file.BrokerId).Distinct().OrderBy(id => id),
            batch.ParticipatingBrokerIds.OrderBy(id => id));
        Assert.Equal(1, batch.Files.Single(file => file.BrokerId == Ana).RowCount);
        Assert.Equal([8U], batch.Files.Single(file => file.BrokerId == Ana).SourceRowNumbers);
        Assert.Equal([8U], batch.Files.Single(file => file.BrokerId == Jerrika).SourceRowNumbers);
        Assert.Equal([9U], batch.Files.Single(file => file.BrokerId == Henry).SourceRowNumbers);
        Assert.Equal([10U], batch.Files.Single(file => file.BrokerId == Donald).SourceRowNumbers);
        Assert.Equal([8U], batch.Files.Single(file => file.BrokerId == Andres).SourceRowNumbers);
        Assert.Equal([8U], batch.Files.Single(file => file.BrokerId == Alberto).SourceRowNumbers);
        Assert.Equal([10U], batch.Files.Single(file => file.BrokerId == Javier).SourceRowNumbers);
        Assert.Equal("Andrés", batch.Files.Single(file => file.BrokerId == Andres).BrokerName);
        Assert.Equal("Alberto", batch.Files.Single(file => file.BrokerId == Alberto).BrokerName);
        Assert.Equal("Javier", batch.Files.Single(file => file.BrokerId == Javier).BrokerName);
        Assert.Contains(batch.Warnings, warning => warning.Contains("Donald", StringComparison.Ordinal));
        Assert.Empty(batch.Files.Single(file => file.BrokerId == Henry).Warnings);
        Assert.All(batch.Files, file => Assert.True(File.Exists(file.OutputPath)));
        Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path),
            path => Path.GetFileName(path).StartsWith(".ECS-expirations-generation-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedSourceHashBlocksBeforeCreatingAnyOutput()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "changed.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = Context(source, (Ana, [8U]));
        File.AppendAllText(source, "changed-after-analysis");

        var exception = await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(context, directory.Path),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cambió después del análisis", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task NextMonthIsExplicitlyBlockedWithoutPublishingWorkbook()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "next.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = ExpirationsGenerationTestWorkbook.Context(
            source,
            ExpirationsProcess.NextMonth,
            [Broker(Ana, "Félix Lara")],
            new Dictionary<Guid, IReadOnlyList<uint>> { [Ana] = [8] });

        await Assert.ThrowsAsync<ExpirationsGenerationException>(() =>
            new ExpirationsGenerationService().GenerateAsync(
                new ExpirationsGenerationRequest(context, directory.Path),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task PreviousMonthIgnoresNextMonthSpecialModeAndGeneratesOneStandardFile()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "previous-special-setting.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var broker = Broker(Ana, "Félix Lara");
        broker.NextMonthGenerationMode = ExpirationsNextMonthGenerationMode.SpecialDualSorted;
        var context = ExpirationsGenerationTestWorkbook.Context(
            source,
            ExpirationsProcess.PreviousMonth,
            [broker],
            new Dictionary<Guid, IReadOnlyList<uint>> { [Ana] = [8] });

        var batch = await new ExpirationsGenerationService().GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.Single(batch.Files);
        Assert.Equal(ExpirationsGeneratedFileVariant.Standard, file.Variant);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnyGenerationOrValidationFailureRollsBackCompleteBatch(int failAtCall)
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, $"rollback-{failAtCall}.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = Context(source, (Ana, [8U]), (Henry, [9U]), (Donald, [10U]));
        var service = new ExpirationsGenerationService(
            new FailingWorkbookGenerator(failAtCall),
            timeProvider: new FixedTimeProvider());

        await Assert.ThrowsAsync<ExpirationsGenerationException>(() => service.GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
        Assert.Equal([Path.GetFileName(source)], Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RepeatedGenerationCreatesNewBatchFolderWithoutOverwritingPrevious()
    {
        using var directory = new ExpirationsGenerationTestDirectory();
        var source = Path.Combine(directory.Path, "repeat.xlsx");
        ExpirationsGenerationTestWorkbook.Create(source);
        var context = Context(source, (Ana, [8U]));
        var service = new ExpirationsGenerationService(timeProvider: new FixedTimeProvider());

        var first = await service.GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);
        var firstHash = new ECS.CommissionsMailer.Services.GeneratedFileHashService()
            .ComputeSha256(first.Files.Single().OutputPath);
        var second = await service.GenerateAsync(
            new ExpirationsGenerationRequest(context, directory.Path),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(first.OutputDirectory, second.OutputDirectory);
        Assert.True(Directory.Exists(first.OutputDirectory));
        Assert.True(Directory.Exists(second.OutputDirectory));
        Assert.Equal(firstHash, new ECS.CommissionsMailer.Services.GeneratedFileHashService()
            .ComputeSha256(first.Files.Single().OutputPath));
    }

    [Fact]
    public void FileNamesAreSanitizedBoundedAndCaseInsensitiveCollisionsUseDeterministicIds()
    {
        var service = new ExpirationsFileNameService();
        var names = service.CreateFileNames(
            ExpirationsProcess.PreviousMonth,
            [
                Broker(Ana, " Juan  Pérez:*? "),
                Broker(Jerrika, "juan pérez---"),
                Broker(Henry, new string('X', 300)),
                Broker(Donald, "JUAN PÉREZ---")
            ]);

        Assert.DoesNotContain(names[Ana], value => "<>:\"/\\|?*".Contains(value));
        Assert.True(names[Henry].Length <= 185);
        Assert.Equal(names.Count, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Jerrika.ToString("N")[..8], names[Jerrika], StringComparison.Ordinal);
        Assert.Contains(Donald.ToString("N")[..8], names[Donald], StringComparison.Ordinal);
    }

    private static ExpirationsGenerationContext Context(
        string source,
        params (Guid BrokerId, IReadOnlyList<uint> Rows)[] targets)
    {
        var brokers = targets.Select(target => Broker(
            target.BrokerId,
            target.BrokerId == Donald ? "Donald" : $"Broker {target.BrokerId.ToString("N")[..4]}"))
            .ToList();
        return ExpirationsGenerationTestWorkbook.Context(
            source,
            ExpirationsProcess.PreviousMonth,
            brokers,
            targets.ToDictionary(target => target.BrokerId, target => target.Rows));
    }

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name, List<string>? emails = null) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = emails ?? [$"{id:N}@example.test"],
        IsActive = true
    };

    private sealed class FailingWorkbookGenerator(int failAtCall) : IExpirationsStandardWorkbookGenerator
    {
        private readonly ExpirationsStandardWorkbookGenerator _inner = new();
        private int _callCount;

        public void Generate(
            ExpirationsStandardWorkbookGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == failAtCall && failAtCall < 3)
                throw new ExpirationsGenerationException("Fallo sintético de generación.");
            _inner.Generate(request, cancellationToken);
            if (_callCount == failAtCall)
                throw new ExpirationsGenerationException("Fallo sintético de validación.");
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Time = new(2026, 8, 21, 12, 34, 56, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Time;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
