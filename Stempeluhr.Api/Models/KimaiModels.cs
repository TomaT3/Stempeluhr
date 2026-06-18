namespace Stempeluhr.Api.Models;

public sealed record KimaiImportRequest(string? BaseUrl, string? AdminApiToken);

public sealed record KimaiUserDto(int Id, string? Username, string? Email, string DisplayName, string? AvatarUrl);
