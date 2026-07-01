using Stempeluhr.Api.Api;
using Stempeluhr.Api.Middleware;
using Stempeluhr.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IRuntimeSettingsStore, RuntimeSettingsStore>();
builder.Services.AddSingleton<IEmployeeService, EmployeeService>();
builder.Services.AddSingleton<IAdminAuthorizationService, AdminAuthorizationService>();
builder.Services.AddSingleton<INfcClockEventStore, NfcClockEventStore>();
builder.Services.AddScoped<IClockService, ClockService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddHttpClient<IKimaiClient, KimaiClient>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200", "https://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseApiExceptionHandling();
app.UseCors("AngularDev");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapApiEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
