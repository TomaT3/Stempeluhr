using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Additional tasks per employee (work for other customers without clocking
/// out): they must survive every admin save (DTO + mapping, AGENTS
/// invariant), be validated so the status can map a running sheet back to
/// exactly one task, and reach the kiosk only as display data.
/// </summary>
public sealed class EmployeeTasksTests
{
    private static readonly EmployeeTaskSettings CustomerX =
        new() { Id = "kx", Label = "Kunde X", ProjectId = 20, ActivityId = 21, Billable = false };

    private static RuntimeSettings Settings(params EmployeeTaskSettings[] tasks) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = 1,
        DefaultActivityId = 2,
        PauseActivityId = 99,
        Employees =
        [
            new EmployeeSettings { Id = "max", DisplayName = "Max", ApiToken = "t", Tasks = tasks }
        ]
    };

    private static AdminEmployeeUpdateDto Update(IReadOnlyCollection<AdminEmployeeTaskDto>? tasks) =>
        new("max", null, "Max", null, null, null, true, null, null, null, null, null, null, true, true, tasks);

    [Fact]
    public void AdminDto_RoundTripsTasks()
    {
        var dto = AdminEmployeeDto.FromSettings(Settings(CustomerX).Employees[0]);

        var task = Assert.Single(dto.Tasks);
        Assert.Equal(new AdminEmployeeTaskDto("kx", "Kunde X", 20, 21, false), task);

        var saved = Update(dto.Tasks).ToSettings(Settings(CustomerX));
        var roundTripped = Assert.Single(saved.Tasks);
        Assert.Equal(("kx", "Kunde X", 20, 21, false),
            (roundTripped.Id, roundTripped.Label, roundTripped.ProjectId, roundTripped.ActivityId, roundTripped.Billable));
    }

    [Fact]
    public void AdminUpdate_WithoutTasksField_KeepsStoredTasks()
    {
        // An old, cached admin client does not know the field and sends null.
        var saved = Update(null).ToSettings(Settings(CustomerX));

        Assert.Equal("kx", Assert.Single(saved.Tasks).Id);
    }

    [Fact]
    public void AdminUpdate_EmptyList_RemovesTasks()
    {
        var saved = Update([]).ToSettings(Settings(CustomerX));

        Assert.Empty(saved.Tasks);
    }

    [Fact]
    public void AdminUpdate_NewTaskWithoutId_GetsStableId()
    {
        var saved = Update([new AdminEmployeeTaskDto(null, " Kunde Y ", 30, 31, true)]).ToSettings(Settings());

        var task = Assert.Single(saved.Tasks);
        Assert.False(string.IsNullOrWhiteSpace(task.Id));
        Assert.Equal("Kunde Y", task.Label);
    }

    [Fact]
    public void DefaultTaskLabel_RoundTripsThroughAdmin()
    {
        var current = new RuntimeSettings
        {
            Employees = [new EmployeeSettings { Id = "max", DisplayName = "Max", DefaultTaskLabel = "Büro" }]
        };

        Assert.Equal("Büro", AdminEmployeeDto.FromSettings(current.Employees[0]).DefaultTaskLabel);
        Assert.Equal("Büro", UpdateLabel(null).ToSettings(current).DefaultTaskLabel);       // alter Client: behalten
        Assert.Null(UpdateLabel(" ").ToSettings(current).DefaultTaskLabel);                 // bewusst geleert
        Assert.Equal("Werkstatt", UpdateLabel(" Werkstatt ").ToSettings(current).DefaultTaskLabel);
    }

    [Fact]
    public void EmployeeDto_CarriesDefaultTaskLabel()
    {
        var employee = new EmployeeSettings { Id = "max", DisplayName = "Max", DefaultTaskLabel = "Büro" };

        Assert.Equal("Büro", new EmployeeService().ToEmployeeDto(employee).DefaultTaskLabel);
    }

    private static AdminEmployeeUpdateDto UpdateLabel(string? label) =>
        new("max", null, "Max", null, null, null, true, null, null, null, null, null, null, true, true, null, label);

    [Fact]
    public void Validate_AcceptsValidTasks()
    {
        Assert.Null(CreateAdminService().ValidateTasks(Settings(CustomerX)));
    }

    [Theory]
    [InlineData("", 20, 21, "Bezeichnung")]
    [InlineData("Kunde Y", null, 21, "Projekt und Aktivitaet")]
    [InlineData("Kunde Y", 20, 99, "Pausen-Aktivitaet")]
    [InlineData("Kunde Y", 1, 2, "Standard-Taetigkeit")]
    public void Validate_RejectsInvalidTask(string label, int? projectId, int activityId, string expected)
    {
        var settings = Settings(new EmployeeTaskSettings { Label = label, ProjectId = projectId, ActivityId = activityId });

        Assert.Contains(expected, CreateAdminService().ValidateTasks(settings));
    }

    [Fact]
    public void Validate_RejectsDuplicateProjectActivity()
    {
        var settings = Settings(CustomerX, new EmployeeTaskSettings { Label = "Kunde X2", ProjectId = 20, ActivityId = 21 });

        Assert.Contains("eindeutig", CreateAdminService().ValidateTasks(settings));
    }

    [Fact]
    public void EmployeeDto_CarriesOnlyIdAndLabelOfCompleteTasks()
    {
        var incomplete = new EmployeeTaskSettings { Id = "half", Label = "Halb", ProjectId = 20 };

        var dto = new EmployeeService().ToEmployeeDto(Settings(CustomerX, incomplete).Employees[0]);

        Assert.Equal([new EmployeeTaskDto("kx", "Kunde X")], dto.Tasks);
    }

    [Fact]
    public async Task KimaiStatus_MapsRunningSheetToTask()
    {
        var client = CreateKimaiClient(
            """[{"id":5,"begin":"2026-09-05T08:00:00+0200","duration":0,"project":{"id":20},"activity":{"id":21}}]""");

        var status = await client.GetStatusAsync(Settings(CustomerX), Settings(CustomerX).Employees[0]);

        Assert.Equal("working", status.State);
        Assert.Equal(("kx", "Kunde X"), (status.ActiveTaskId, status.ActiveTaskLabel));
    }

    [Fact]
    public async Task KimaiStatus_DefaultSheet_HasNoTask()
    {
        var client = CreateKimaiClient(
            """[{"id":5,"begin":"2026-09-05T08:00:00+0200","duration":0,"project":1,"activity":2}]""");

        var status = await client.GetStatusAsync(Settings(CustomerX), Settings(CustomerX).Employees[0]);

        Assert.Equal("working", status.State);
        Assert.Null(status.ActiveTaskId);
    }

    private static AdminService CreateAdminService() => new(null!, null!);

    private static KimaiClient CreateKimaiClient(string activeJson) =>
        new(new HttpClient(new FixedHandler(activeJson)), NullLogger<KimaiClient>.Instance);

    private sealed class FixedHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
