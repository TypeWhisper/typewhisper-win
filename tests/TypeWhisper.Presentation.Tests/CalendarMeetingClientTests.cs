using System.Net;
using System.Net.Http.Json;
using System.Text;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class CalendarMeetingClientTests
{
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private const string GraphEvent = """
        {"id":"occurrence-1","subject":"Planning","start":{"dateTime":"2026-09-09T10:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-09-09T11:00:00.0000000","timeZone":"UTC"},"responseStatus":{"response":"accepted"},"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/19%3Ameeting_ABC%40thread.v2/0?context=private"}}
        """;

    [Fact]
    public async Task GraphFollowsPagesAndKeepsExpandedOccurrenceIdentityInUtc()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            Assert.Contains("outlook.timezone=\"UTC\"", string.Join(",", request.Headers.GetValues("Prefer")));
            calls++;
            return Json(calls == 1
                ? "{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/selected/calendarView?$skiptoken=next\"}"
                : "{\"value\":[" + GraphEvent + "]}");
        }));
        var client = new CalendarMeetingClient(http, _ => Task.FromResult("test-token"));
        var meeting = Assert.Single(await client.ReadMicrosoftAsync("selected", From, From.AddDays(7), default));
        Assert.Equal(2, calls);
        Assert.Equal("occurrence-1", meeting.EventId);
        Assert.Equal(From.AddHours(10), meeting.Start);
        Assert.Equal("teams.microsoft.com/l/meetup-join/19:meeting_ABC@thread.v2/0", Assert.Single(meeting.Links).Identity);
        Assert.True(meeting.PermitsAutomaticStart);
        Assert.False(meeting.IsInsideJoinWindow(From.AddHours(9)));
    }

    [Theory]
    [InlineData("https://evil.invalid/steal")]
    [InlineData("https://graph.microsoft.com.evil.invalid/v1.0/me/calendars/selected/calendarView")]
    [InlineData("http://graph.microsoft.com/v1.0/me/calendars/selected/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/messages")]
    public async Task UntrustedPaginationNeverReceivesToken(string next)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next }) }; }));
        var client = new CalendarMeetingClient(http, _ => Task.FromResult("private-token"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadMicrosoftAsync("selected", From, From.AddDays(7), default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("\"isCancelled\":true,")]
    [InlineData("\"isAllDay\":true,")]
    public async Task GraphSkipsCancelledAndAllDayEvents(string field)
    {
        using var http = new HttpClient(new Handler(_ => Json("{\"value\":[{" + field + GraphEvent[1..] + "]}")));
        Assert.Empty(await new CalendarMeetingClient(http, _ => Task.FromResult("token")).ReadMicrosoftAsync("selected", From, From.AddDays(7), default));
    }

    [Fact]
    public async Task GoogleExpandsRecurrenceAndReadsSelfParticipationAndOffset()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Contains("singleEvents=true", request.RequestUri!.Query);
            Assert.Contains("timeZone=UTC", request.RequestUri.Query);
            return Json("""
              {"items":[
                {"id":"series_20260909","summary":"Weekly","start":{"dateTime":"2026-09-09T12:00:00+02:00"},"end":{"dateTime":"2026-09-09T13:00:00+02:00"},"attendees":[{"self":true,"responseStatus":"tentative"}],"conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"https://meet.google.com/abc-defg-hij"}]}},
                {"id":"declined","start":{"dateTime":"2026-09-09T12:00:00Z"},"end":{"dateTime":"2026-09-09T13:00:00Z"},"attendees":[{"self":true,"responseStatus":"declined"}],"hangoutLink":"https://meet.google.com/abc-defg-hij"},
                {"id":"all-day","start":{"date":"2026-09-09"},"end":{"date":"2026-09-10"},"hangoutLink":"https://meet.google.com/abc-defg-hij"}
              ]}
              """);
        }));
        var result = Assert.Single(await new CalendarMeetingClient(http, _ => Task.FromResult("token")).ReadGoogleAsync("primary", From, From.AddDays(7), default));
        Assert.Equal(From.AddHours(10), result.Start);
        Assert.True(result.PermitsAutomaticStart);
        Assert.Equal("abc-defg-hij", Assert.Single(result.Links).Identity);
        Assert.DoesNotContain("Weekly", result.ToString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task ApiFailuresDoNotLeakPayloadOrReturnPartialCalendar(int status)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("secret-calendar-data") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new CalendarMeetingClient(http, _ => Task.FromResult("token")).ReadGoogleAsync("primary", From, From.AddDays(7), default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("https://acme.zoom.us/j/123456789?pwd=secret", "zoom", "j/123456789")]
    [InlineData("https://example.zoom.us/s/123-456-789", "zoom", "j/123456789")]
    [InlineData("https://zoomgov.com/wc/join/123%20456%20789", "zoom", "j/123456789")]
    [InlineData("zoommtg://zoom.us/join?confno=123456&confno=999999", "zoom", "j/123456")]
    [InlineData("acme.zoom.us/my/Weekly-Room.", "zoom", "my/weekly-room")]
    [InlineData("msteams:/L/MEETUP-JOIN/19%3ameeting_ABC%40thread.v2/0", "teams", "teams.microsoft.com/l/meetup-join/19:meeting_ABC@thread.v2/0")]
    [InlineData("https://teams.live.com/meet/123456?p=private", "teams", "teams.live.com/meet/123456")]
    [InlineData("https://meet.google.com/ABC-defg-HIJ?authuser=1", "googleMeet", "abc-defg-hij")]
    public void MacMeetingLinkFixturesCanonicalize(string input, string provider, string identity)
    {
        Assert.Equal(new MeetingLink(provider, identity), MeetingLink.Parse(input));
    }

    [Theory]
    [InlineData("https://zoom.us.evil.invalid/j/123")]
    [InlineData("https://meet.google.com/")]
    [InlineData("https://zoom.us/j/not-a-number")]
    [InlineData("https://evil@zoom.us/j/123")]
    public void BrowsingProviderAloneIsNotAMeeting(string input) => Assert.Null(MeetingLink.Parse(input));
}
