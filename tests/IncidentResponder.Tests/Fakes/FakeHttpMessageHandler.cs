using System.Net;
using System.Text;

namespace IncidentResponder.Tests.Fakes;

/// <summary>Answers every request with a canned response and records the requested URLs.</summary>
public sealed class FakeHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static FakeHttpMessageHandler FromFixture(string fileName) =>
        new(HttpStatusCode.OK, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lgtm", fileName)));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
