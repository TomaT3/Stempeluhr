using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

public sealed class TelegramCorrectionMessageTests
{
    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static TimeCorrectionOriginal Original(DateTimeOffset begin, DateTimeOffset? end, int activity = WorkActivity)
        => new(begin, end, activity, WorkProject, "Stempeluhr", true);

    [Fact]
    public void ChangeTimes_ShowsOnlyWhatChanges()
    {
        var request = Request(TimeCorrectionKind.ChangeTimes, configure: r => r with
        {
            Original = Original(Day(6, 22), Day(7, 6, 10)),
            End = Day(7, 5, 40),
        });

        var text = TelegramMessageFactory.BuildCorrectionRequest(request, Berlin);

        Assert.Equal(
            "📝 Korrekturantrag · Max Mustermann\nBeginn/Ende ändern\nSchicht: Di 06.10. 22:00 – Mi 07.10. 06:10\nEnde: Mi 07.10. 06:10 → Mi 07.10. 05:40",
            text);
    }

    [Fact]
    public void AddShift_NamesTheTaskAndPauseAndFallsBackToTheDefaultTask()
    {
        var request = Request(TimeCorrectionKind.AddShift, configure: r => r with
        {
            Begin = Day(6, 22), End = Day(7, 6), PauseBegin = Day(7, 2), PauseEnd = Day(7, 2, 30),
        });

        Assert.Equal(
            "📝 Korrekturantrag · Max Mustermann\nSchicht nachtragen\nSchicht: Di 06.10. 22:00 – Mi 07.10. 06:00\nTätigkeit: Kunde X\nPause: Mi 07.10. 02:00 – 02:30",
            TelegramMessageFactory.BuildCorrectionRequest(request, Berlin, "Kunde X"));
        Assert.Contains("Tätigkeit: Standard-Tätigkeit", TelegramMessageFactory.BuildCorrectionRequest(request, Berlin));
    }

    [Fact]
    public void PauseEntry_IsLabelledAsPause()
    {
        var request = Request(TimeCorrectionKind.SetEnd, configure: r => r with
        {
            Original = Original(At(8), At(8, 30), PauseActivity),
            End = At(8, 20),
        });

        var text = TelegramMessageFactory.BuildCorrectionRequest(request, Berlin, originalIsPause: true);

        Assert.Contains("Pause: Mo 05.10. 08:00 – 08:30\nEnde: Mo 05.10. 08:30 → Mo 05.10. 08:20", text);
    }

    [Fact]
    public void Decision_OfAnOpenRequest_HasNoResultLine()
    {
        var request = Request(TimeCorrectionKind.SetEnd, configure: r => r with { Original = Original(At(6), At(11)), End = At(10) });

        Assert.Equal(
            TelegramMessageFactory.BuildCorrectionRequest(request, Berlin),
            TelegramMessageFactory.BuildCorrectionDecision(request, Berlin));
    }

    [Fact]
    public void Decision_ShortensVeryLongKimaiErrors()
    {
        var request = Request(TimeCorrectionKind.SetEnd, configure: r => r with
        {
            Original = Original(At(6), At(11)),
            End = At(10),
            Status = TimeCorrectionStatus.Failed,
            Error = new string('x', 900),
        });

        var text = TelegramMessageFactory.BuildCorrectionDecision(request, Berlin);

        Assert.True(text.Length < 700);
        Assert.Contains("…", text);
    }
}
