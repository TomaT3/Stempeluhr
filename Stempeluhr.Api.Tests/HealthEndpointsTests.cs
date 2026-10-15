using System.Text.Json;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Models;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public void Describe_ReportsAbsencesOnlyWithProjectAndAllThreeActivities()
    {
        Assert.True(HealthEndpoints.Describe(Absences(project: 7, flexDay: 3), "1.0.0").AbsencesConfigured);
        Assert.False(HealthEndpoints.Describe(Absences(project: 7, flexDay: null), "1.0.0").AbsencesConfigured);
        Assert.False(HealthEndpoints.Describe(Absences(project: null, flexDay: 3), "1.0.0").AbsencesConfigured);
        Assert.False(HealthEndpoints.Describe(new RuntimeSettings(), null).AbsencesConfigured);
    }

    private static RuntimeSettings Absences(int? project, int? flexDay) => new()
    {
        AbsenceProjectId = project,
        VacationActivityId = 1,
        SickActivityId = 2,
        FlexDayActivityId = flexDay,
    };

    [Fact]
    public void Describe_KeepsTheFieldsTheKioskAlreadyReads()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "https://kimai.example",
            Employees = [new EmployeeSettings { Id = "max", DisplayName = "Max", IsEnabled = true }],
        };

        var json = JsonSerializer.Serialize(HealthEndpoints.Describe(settings, "1.2.3"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            """{"ok":true,"version":"1.2.3","configuredEmployees":1,"settingsConfigured":true,"absencesConfigured":false}""",
            json);
    }
}
