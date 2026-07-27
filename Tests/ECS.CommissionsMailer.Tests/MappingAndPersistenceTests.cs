using System.Text.Json;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class MappingAndPersistenceTests
{
    private readonly WorksheetBrokerMappingService _service = new();

    [Fact]
    public void ResolvesOneWorksheetAndSeveralWorksheetsByStableBrokerId()
    {
        var broker = Broker("Corredor A", "AR", "AR2", "ANDRES");

        var result = _service.Resolve(["AR", "AR2", "ANDRES"], [broker]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Assignments.Count);
        Assert.All(result.Assignments, value => Assert.Equal(broker.Id, value.Broker.Id));
    }

    [Fact]
    public void MatchingIgnoresCaseAndExternalSpacesButNotPartialNames()
    {
        var broker = Broker("Corredor A", "  AaV  ");

        var exact = _service.Resolve(["aav"], [broker]);
        var partial = _service.Resolve(["AAV Especial"], [broker]);

        Assert.True(exact.IsValid);
        Assert.Empty(partial.Assignments);
        Assert.Equal(["AAV Especial"], partial.MissingWorksheetNames);
    }

    [Fact]
    public void DuplicateWorksheetAcrossBrokersIsBlocking()
    {
        var first = Broker("Corredor A", "AR");
        var second = Broker("Corredor B", " ar ");

        var result = _service.Resolve(["AR"], [first, second]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("dos corredores", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewWorksheetIsReportedAndNeverGuessed()
    {
        var result = _service.Resolve(["NUEVA"], [Broker("Corredor A", "AR")]);

        Assert.Empty(result.Assignments);
        Assert.Equal(["NUEVA"], result.MissingWorksheetNames);
    }

    [Fact]
    public void LegacyBrokerJsonLoadsNewCollectionsAsEmpty()
    {
        const string json = """
            {
              "Id": "26ad0661-34d1-43b1-90ea-dba7e03dc4ee",
              "Name": "Corredor sintético",
              "PrimaryEmailAddresses": ["test@example.com"],
              "Assistants": []
            }
            """;

        var broker = JsonSerializer.Deserialize<Broker>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(broker);
        Assert.Empty(broker.AssociatedWorksheetNames);
        Assert.Empty(broker.Deductions);
    }

    [Fact]
    public void DeductionCanBeCreatedEditedDeletedAndPersisted()
    {
        var broker = Broker("Corredor sintético", "SYN");
        var deduction = new BrokerDeduction
        {
            Description = "Ahorro",
            Amount = 1250.75m,
            Currency = DeductionCurrency.CRC,
            ApplicationType = DeductionApplicationType.PayableAmount
        };
        broker.Deductions.Add(deduction);
        broker.Deductions[0].Description = "Ahorro voluntario";

        var json = JsonSerializer.Serialize(broker);
        var reloaded = JsonSerializer.Deserialize<Broker>(json)!;

        Assert.Single(reloaded.Deductions);
        Assert.Equal("Ahorro voluntario", reloaded.Deductions[0].Description);
        Assert.Equal(1250.75m, reloaded.Deductions[0].Amount);
        reloaded.Deductions.RemoveAt(0);
        Assert.Empty(reloaded.Deductions);
    }

    [Fact]
    public void InvalidDeductionIsBlocking()
    {
        var broker = Broker("Corredor sintético", "SYN");
        broker.Deductions.Add(new BrokerDeduction { Description = "", Amount = -1m });

        var errors = _service.ValidateDeductions(broker);

        Assert.Equal(2, errors.Count);
    }

    private static Broker Broker(string name, params string[] worksheetNames) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PrimaryEmailAddresses = [$"{Guid.NewGuid():N}@example.com"],
        AssociatedWorksheetNames = [.. worksheetNames]
    };
}
